// Plays the House Clerk's official live HLS feed (CORS-open, no auth wall - see
// FederalCivicFeedService for the discovery API) directly in a <video> element, no iframe.
// Safari plays HLS natively; every other browser needs hls.js since <video src> alone
// doesn't understand HLS manifests there.
window.civicWatchVideo = {
    attach(videoEl, url) {
        if (!videoEl || !url) return;

        this.detach(videoEl);

        if (videoEl.canPlayType('application/vnd.apple.mpegurl')) {
            videoEl.src = url;
            videoEl.play().catch(() => { });
            return;
        }

        if (window.Hls && window.Hls.isSupported()) {
            const hls = new window.Hls();
            hls.loadSource(url);
            hls.attachMedia(videoEl);
            hls.on(window.Hls.Events.MANIFEST_PARSED, () => videoEl.play().catch(() => { }));
            videoEl._civicHls = hls;
        }
    },

    detach(videoEl) {
        if (videoEl && videoEl._civicHls) {
            videoEl._civicHls.destroy();
            videoEl._civicHls = null;
        }
    }
};
