// Broadcaster preview (getUserMedia) plus a real broadcaster<->viewer WebRTC mesh: the
// server (LiveShowHub) only relays signaling messages, never media. ICE configuration is
// supplied by the server before joining so production can add short-lived TURN relay
// credentials without embedding the coturn shared secret in this public script.
window.liveShow = {
	stream: null,
	connection: null,
	role: null, // "broadcaster" | "viewer"
	sessionId: null,
	peerConnections: new Map(), // broadcaster: viewerConnectionId -> RTCPeerConnection
	viewerPeerConnection: null, // viewer: its single connection to the broadcaster
	broadcasterConnectionId: null, // viewer: who to send answers/ICE candidates to
	mediaRecorder: null,
	recordingStartedAt: null,
	iceServers: [{ urls: "stun:stun.l.google.com:19302" }],

	configureIceServers(iceServers) {
		if (!Array.isArray(iceServers) || iceServers.length === 0) {
			this.iceServers = [{ urls: "stun:stun.l.google.com:19302" }];
			return;
		}

		this.iceServers = iceServers
			.map((server) => ({
				urls: server.urls ?? server.Urls,
				...((server.username ?? server.Username)
					? { username: server.username ?? server.Username }
					: {}),
				...((server.credential ?? server.Credential)
					? { credential: server.credential ?? server.Credential }
					: {}),
			}))
			.filter((server) => Array.isArray(server.urls) && server.urls.length > 0);
	},

	async start(videoElement) {
		try {
			this.stream = await navigator.mediaDevices.getUserMedia({
				video: true,
				audio: true,
			});
			videoElement.srcObject = this.stream;
		} catch (err) {
			throw new Error(`Camera access denied: ${err.message}`);
		}
	},

	stop() {
		if (this.stream) {
			this.stream.getTracks().forEach((t) => t.stop());
			this.stream = null;
		}
	},

	toggleAudio(enabled) {
		if (this.stream) {
			this.stream.getAudioTracks().forEach((t) => {
				t.enabled = enabled;
			});
		}
	},

	toggleVideo(enabled) {
		if (this.stream) {
			this.stream.getVideoTracks().forEach((t) => {
				t.enabled = enabled;
			});
		}
	},

	isActive() {
		return !!(this.stream && this.stream.active);
	},

	// --- Broadcaster ---

	async startBroadcast(sessionId) {
		if (!this.stream) {
			throw new Error("Camera must be started before going live.");
		}

		this.role = "broadcaster";
		this.sessionId = sessionId;

		this.connection = new signalR.HubConnectionBuilder()
			.withUrl("/hubs/live-show")
			.withAutomaticReconnect()
			.build();

		this.connection.on("ViewerJoined", (viewerConnectionId) => this._onViewerJoined(viewerConnectionId));
		this.connection.on("ReceiveAnswer", (viewerConnectionId, sdp) => this._onReceiveAnswer(viewerConnectionId, sdp));
		this.connection.on("ReceiveIceCandidate", (fromConnectionId, candidateJson) =>
			this._onReceiveIceCandidate(this.peerConnections.get(fromConnectionId), candidateJson));
		this.connection.on("ViewerLeft", (viewerConnectionId) => {
			const pc = this.peerConnections.get(viewerConnectionId);
			if (pc) {
				pc.close();
				this.peerConnections.delete(viewerConnectionId);
			}
		});

		// withAutomaticReconnect() gives the browser a new SignalR connectionId - the
		// server's BroadcasterConnections/group membership from before the drop is gone,
		// so any viewer who joins during the gap (or whose offer/answer was mid-flight)
		// would otherwise get silently stuck with no video and no error. Re-registering
		// re-establishes it for anyone who joins afterward; already-connected viewers keep
		// playing since WebRTC media flows peer-to-peer, not through this signaling
		// connection.
		this.connection.onreconnected(async () => {
			try {
				await this.connection.invoke("JoinAsBroadcaster", sessionId);
			} catch {
				// best-effort - if the session already ended server-side there's nothing to rejoin
			}
		});

		await this.connection.start();
		const joined = await this.connection.invoke("JoinAsBroadcaster", sessionId);
		if (!joined) {
			throw new Error("Could not join as broadcaster - the session may have already ended.");
		}

		this._startRecording(sessionId);
	},

	async _onViewerJoined(viewerConnectionId) {
		const pc = new RTCPeerConnection({ iceServers: this.iceServers });
		this.peerConnections.set(viewerConnectionId, pc);
		this.stream.getTracks().forEach((track) => pc.addTrack(track, this.stream));

		pc.onicecandidate = (event) => {
			if (event.candidate) {
				this.connection.invoke("SendIceCandidate", viewerConnectionId, JSON.stringify(event.candidate));
			}
		};

		const offer = await pc.createOffer();
		await pc.setLocalDescription(offer);
		await this.connection.invoke("SendOffer", viewerConnectionId, JSON.stringify(offer));
	},

	async _onReceiveAnswer(viewerConnectionId, sdpJson) {
		const pc = this.peerConnections.get(viewerConnectionId);
		if (pc) {
			await pc.setRemoteDescription(JSON.parse(sdpJson));
		}
	},

	// Previously each ondataavailable event fired its own uncoordinated fetch() with no
	// try/catch, retry, or ordering guarantee. MediaRecorder fires ondataavailable on its own
	// 3s timer regardless of whether the previous handler invocation (or its fetch) has
	// finished - a slow or failed upload didn't pause the timer, so a retry or a merely-slow
	// request could easily land after a later chunk's, and a failed request just silently
	// dropped that chunk forever. The recording file is raw-concatenated bytes in arrival
	// order (see LiveShowService.AppendRecordingChunkAsync), so either problem corrupts
	// everything after it. Fixed by queuing chunks with a sequence number and draining the
	// queue strictly one at a time - the next upload never starts until the current one has
	// either succeeded or exhausted its retries - plus a server-side sequence check
	// (recording-chunk's `sequence` param) that can at least detect a gap it can't prevent
	// (e.g. a chunk still lost after every retry).
	_uploadQueue: [],
	_uploadPumpRunning: false,
	_nextChunkSequence: 0,
	hadRecordingUploadFailure: false,

	_startRecording(sessionId) {
		this.recordingStartedAt = Date.now();
		this._uploadQueue = [];
		this._uploadPumpRunning = false;
		this._nextChunkSequence = 0;
		this.hadRecordingUploadFailure = false;
		this.mediaRecorder = new MediaRecorder(this.stream, { mimeType: "video/webm" });

		this.mediaRecorder.ondataavailable = (event) => {
			if (event.data && event.data.size > 0) {
				this._uploadQueue.push({ sequence: this._nextChunkSequence++, blob: event.data });
				this._pumpUploadQueue(sessionId);
			}
		};

		this.mediaRecorder.start(3000);
	},

	async _pumpUploadQueue(sessionId) {
		if (this._uploadPumpRunning) return;
		this._uploadPumpRunning = true;
		try {
			while (this._uploadQueue.length > 0) {
				const chunk = this._uploadQueue[0];
				const uploaded = await this._uploadChunkWithRetry(sessionId, chunk);
				if (!uploaded) {
					// Retries exhausted for this chunk - drop it (there's nothing left to try)
					// but keep draining the queue so one bad chunk doesn't also block every
					// chunk recorded after it, and flag the failure so the broadcaster UI can
					// warn that this recording may have a gap.
					this.hadRecordingUploadFailure = true;
				}
				this._uploadQueue.shift();
			}
		} finally {
			this._uploadPumpRunning = false;
		}
	},

	async _uploadChunkWithRetry(sessionId, chunk) {
		const maxAttempts = 3;
		for (let attempt = 1; attempt <= maxAttempts; attempt++) {
			try {
				const response = await fetch(
					`/admin/api/live-show/${sessionId}/recording-chunk?sequence=${chunk.sequence}`,
					{
						method: "POST",
						headers: { "Content-Type": "application/octet-stream" },
						body: chunk.blob,
					}
				);
				if (response.ok) return true;
			} catch {
				// network error - fall through to retry/backoff below
			}
			if (attempt < maxAttempts) {
				await new Promise((resolve) => setTimeout(resolve, 500 * attempt));
			}
		}
		return false;
	},

	async stopBroadcast() {
		if (this.mediaRecorder && this.mediaRecorder.state !== "inactive") {
			await new Promise((resolve) => {
				this.mediaRecorder.onstop = resolve;
				this.mediaRecorder.stop();
			});
		}

		// mediaRecorder.stop() fires one last ondataavailable for the final partial chunk, but
		// that handler only enqueues it - the pump uploads it asynchronously, unawaited. Without
		// this wait, finalize-recording below could run before the last (or any still-queued
		// earlier) chunk actually finished uploading, truncating the file.
		while (this._uploadPumpRunning || this._uploadQueue.length > 0) {
			await new Promise((resolve) => setTimeout(resolve, 100));
		}

		const durationSeconds = this.recordingStartedAt
			? Math.round((Date.now() - this.recordingStartedAt) / 1000)
			: 0;

		if (this.sessionId) {
			await fetch(`/admin/api/live-show/${this.sessionId}/finalize-recording?durationSeconds=${durationSeconds}`, {
				method: "POST",
			});
		}

		this.peerConnections.forEach((pc) => pc.close());
		this.peerConnections.clear();

		if (this.connection) {
			await this.connection.stop();
			this.connection = null;
		}

		this.stop();
		this.role = null;
		this.sessionId = null;
		this.mediaRecorder = null;
	},

	// --- Viewer ---

	async startViewer(videoElement, inviteToken) {
		this.role = "viewer";

		this.connection = new signalR.HubConnectionBuilder()
			.withUrl("/hubs/live-show")
			.withAutomaticReconnect()
			.build();

		this.connection.on("ReceiveOffer", async (fromConnectionId, sdpJson) => {
			if (this.viewerPeerConnection) {
				// A fresh offer (reconnect rejoin, or the broadcaster renegotiating) replaces
				// whatever peer connection we already had - close the stale one instead of
				// leaking it.
				this.viewerPeerConnection.close();
			}

			this.broadcasterConnectionId = fromConnectionId;
			const pc = new RTCPeerConnection({ iceServers: this.iceServers });
			this.viewerPeerConnection = pc;

			pc.ontrack = (event) => {
				videoElement.srcObject = event.streams[0];
			};
			pc.onicecandidate = (event) => {
				if (event.candidate) {
					this.connection.invoke("SendIceCandidate", fromConnectionId, JSON.stringify(event.candidate));
				}
			};

			await pc.setRemoteDescription(JSON.parse(sdpJson));
			const answer = await pc.createAnswer();
			await pc.setLocalDescription(answer);
			await this.connection.invoke("SendAnswer", fromConnectionId, JSON.stringify(answer));
		});

		this.connection.on("ReceiveIceCandidate", (fromConnectionId, candidateJson) =>
			this._onReceiveIceCandidate(this.viewerPeerConnection, candidateJson));

		this.connection.on("BroadcasterLeft", () => {
			if (this.viewerPeerConnection) {
				this.viewerPeerConnection.close();
				this.viewerPeerConnection = null;
			}
			videoElement.srcObject = null;
		});

		// Same reasoning as the broadcaster side - a reconnect gets a new connectionId, so
		// this viewer has to re-announce itself to whichever connection is currently
		// registered as the broadcaster, which triggers a fresh offer/answer exchange.
		this.connection.onreconnected(async () => {
			try {
				await this.connection.invoke("JoinAsViewer", inviteToken);
			} catch {
				// best-effort - if the show already ended there's nothing to rejoin
			}
		});

		await this.connection.start();
		return await this.connection.invoke("JoinAsViewer", inviteToken);
	},

	async _onReceiveIceCandidate(pc, candidateJson) {
		if (pc) {
			await pc.addIceCandidate(JSON.parse(candidateJson));
		}
	},

	async stopViewer() {
		if (this.viewerPeerConnection) {
			this.viewerPeerConnection.close();
			this.viewerPeerConnection = null;
		}
		if (this.connection) {
			await this.connection.stop();
			this.connection = null;
		}
		this.role = null;
	},
};
