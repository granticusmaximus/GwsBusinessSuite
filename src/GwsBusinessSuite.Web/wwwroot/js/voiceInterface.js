// Voice input/output for SentinelGPT, built entirely on the browser's native Web Speech API -
// no server-side speech model, no new external dependency. Speech recognition (STT) and speech
// synthesis (TTS) are supported independently and unevenly across browsers (Firefox has neither
// window.SpeechRecognition nor a reliable webkitSpeechRecognition; Safari differs from Chrome),
// so every entry point guards on the relevant isXSupported() check and no-ops rather than
// throwing when the browser lacks the capability - the caller decides how to reflect that in UI
// (see SentinelGpt.razor, which hides/disables the mic button and the "speak responses" toggle
// when unsupported).
window.gwsVoiceInterface = (function () {
    const instances = {};

    function isSttSupported() {
        return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    }

    function isTtsSupported() {
        return !!window.speechSynthesis;
    }

    function init(elementId, dotNetHelper) {
        if (instances[elementId]) {
            return;
        }
        instances[elementId] = { dotNetHelper, recognition: null };
    }

    function startListening(elementId, lang) {
        const instance = instances[elementId];
        if (!instance || !isSttSupported()) {
            return;
        }
        if (instance.recognition) {
            return; // already listening
        }

        const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        const recognition = new Recognition();
        recognition.lang = lang || navigator.language || "en-US";
        recognition.continuous = false;
        recognition.interimResults = false;
        recognition.maxAlternatives = 1;

        recognition.onresult = (event) => {
            const transcript = event.results[0]?.[0]?.transcript ?? "";
            instance.dotNetHelper.invokeMethodAsync("OnVoiceTranscript", transcript);
        };
        recognition.onerror = (event) => {
            instance.dotNetHelper.invokeMethodAsync("OnVoiceError", event.error || "unknown");
        };
        recognition.onend = () => {
            instance.recognition = null;
            instance.dotNetHelper.invokeMethodAsync("OnListeningEnded");
        };

        instance.recognition = recognition;
        try {
            recognition.start();
        } catch {
            // start() throws if called while already starting/running in some browsers -
            // treat it the same as an ordinary recognition error rather than crashing the circuit.
            instance.recognition = null;
            instance.dotNetHelper.invokeMethodAsync("OnVoiceError", "start-failed");
        }
    }

    function stopListening(elementId) {
        const instance = instances[elementId];
        if (instance && instance.recognition) {
            instance.recognition.stop();
        }
    }

    function speak(elementId, text, lang) {
        const instance = instances[elementId];
        if (!instance || !isTtsSupported() || !text || !text.trim()) {
            return;
        }

        window.speechSynthesis.cancel();
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.lang = lang || navigator.language || "en-US";
        utterance.onend = () => instance.dotNetHelper.invokeMethodAsync("OnSpeakingEnded");
        utterance.onerror = () => instance.dotNetHelper.invokeMethodAsync("OnSpeakingEnded");
        window.speechSynthesis.speak(utterance);
    }

    function stopSpeaking() {
        if (isTtsSupported()) {
            window.speechSynthesis.cancel();
        }
    }

    function destroy(elementId) {
        const instance = instances[elementId];
        if (!instance) {
            return;
        }

        if (instance.recognition) {
            instance.recognition.onresult = null;
            instance.recognition.onerror = null;
            instance.recognition.onend = null;
            try { instance.recognition.stop(); } catch { /* already stopped */ }
        }
        if (isTtsSupported()) {
            window.speechSynthesis.cancel();
        }
        delete instances[elementId];
    }

    return { isSttSupported, isTtsSupported, init, startListening, stopListening, speak, stopSpeaking, destroy };
})();
