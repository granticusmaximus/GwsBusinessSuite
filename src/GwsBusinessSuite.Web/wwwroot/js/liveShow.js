window.liveShow = {
	stream: null,

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
};
