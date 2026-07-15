// Apple-Podcasts-style resume position for the Podcast Directory's episode <audio>
// elements. Uses capture-phase listeners on the document rather than one listener per
// <audio> element - media events (timeupdate/pause/ended/loadedmetadata) don't bubble,
// but capture-phase listeners still see them on the way down regardless, so a single
// delegated listener covers every episode in the list without re-wiring on each render.
// Each <audio> tags itself with data-episode-id (and data-resume-seconds, applied once via
// data-resume-applied so re-renders don't keep seeking back to the saved position while
// the user is actively listening).
window.gwsPodcastProgress = (function () {
    let _dotNetRef = null;
    const _lastSavedAt = {};
    const SAVE_INTERVAL_MS = 5000;

    function isTrackedAudio(target) {
        return target && target.tagName === "AUDIO" && target.hasAttribute("data-episode-id");
    }

    function saveProgress(audio, force) {
        const episodeId = audio.getAttribute("data-episode-id");
        const now = Date.now();
        if (!force && _lastSavedAt[episodeId] && now - _lastSavedAt[episodeId] < SAVE_INTERVAL_MS) {
            return;
        }

        _lastSavedAt[episodeId] = now;
        const duration = Number.isFinite(audio.duration) ? Math.floor(audio.duration) : null;
        _dotNetRef.invokeMethodAsync("SaveProgress", episodeId, Math.floor(audio.currentTime), duration);
    }

    function handleTimeUpdate(e) {
        if (isTrackedAudio(e.target)) {
            saveProgress(e.target, false);
        }
    }

    function handlePause(e) {
        if (isTrackedAudio(e.target)) {
            saveProgress(e.target, true);
        }
    }

    function handleEnded(e) {
        if (isTrackedAudio(e.target)) {
            _dotNetRef.invokeMethodAsync("MarkCompleted", e.target.getAttribute("data-episode-id"));
        }
    }

    function handleLoadedMetadata(e) {
        const audio = e.target;
        if (!isTrackedAudio(audio) || audio.hasAttribute("data-resume-applied")) {
            return;
        }

        audio.setAttribute("data-resume-applied", "1");
        const resumeSeconds = parseFloat(audio.getAttribute("data-resume-seconds") || "0");
        if (resumeSeconds > 0 && resumeSeconds < audio.duration) {
            audio.currentTime = resumeSeconds;
        }
    }

    function init(dotNetRef) {
        _dotNetRef = dotNetRef;
        document.addEventListener("timeupdate", handleTimeUpdate, true);
        document.addEventListener("pause", handlePause, true);
        document.addEventListener("ended", handleEnded, true);
        document.addEventListener("loadedmetadata", handleLoadedMetadata, true);
    }

    function dispose() {
        document.removeEventListener("timeupdate", handleTimeUpdate, true);
        document.removeEventListener("pause", handlePause, true);
        document.removeEventListener("ended", handleEnded, true);
        document.removeEventListener("loadedmetadata", handleLoadedMetadata, true);
        _dotNetRef = null;
    }

    return { init, dispose };
})();
