// Broadcaster preview (getUserMedia) plus a real broadcaster<->viewer WebRTC mesh: the
// server (LiveShowHub) only relays signaling messages, never media. STUN-only NAT
// traversal (no TURN server configured) - fine for the small invited-viewer audience this
// was scoped for, but a peer behind a strict/symmetric NAT may fail to connect; that's a
// known, disclosed limitation rather than a bug.
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

	_startRecording(sessionId) {
		this.recordingStartedAt = Date.now();
		this.mediaRecorder = new MediaRecorder(this.stream, { mimeType: "video/webm" });

		this.mediaRecorder.ondataavailable = async (event) => {
			if (event.data && event.data.size > 0) {
				await fetch(`/admin/api/live-show/${sessionId}/recording-chunk`, {
					method: "POST",
					headers: { "Content-Type": "application/octet-stream" },
					body: event.data,
				});
			}
		};

		this.mediaRecorder.start(3000);
	},

	async stopBroadcast() {
		if (this.mediaRecorder && this.mediaRecorder.state !== "inactive") {
			await new Promise((resolve) => {
				this.mediaRecorder.onstop = resolve;
				this.mediaRecorder.stop();
			});
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
