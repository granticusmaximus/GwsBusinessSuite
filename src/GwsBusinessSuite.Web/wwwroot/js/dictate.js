// Dictation for SentinelGPT's composer (PWA phase 3, 07-11-26: what the Mac app's voice notes give,
// on a phone or tablet). A [data-gws-dictate="<textarea id>"] button shows only where the browser
// has speech recognition (Safari on iPhone, iPad and Mac; Chrome; Edge), and types what's said into
// that box. Recognition is the browser's own (Apple's or Google's): the app never gets the audio.
(function () {
    'use strict';
    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    let active = null;

    function stop() {
        if (!active) return;
        const { recognition, button } = active;
        active = null;
        button.setAttribute('aria-pressed', 'false');
        button.classList.remove('is-listening');
        try { recognition.stop(); } catch { /* already stopped */ }
    }

    function start(button) {
        const box = document.getElementById(button.getAttribute('data-gws-dictate'));
        if (!box || box.disabled) return;
        const recognition = new Recognition();
        recognition.lang = document.documentElement.lang || navigator.language || 'en-US';
        recognition.interimResults = true;
        recognition.continuous = false;
        const before = box.value.trim();
        recognition.onresult = (event) => {
            let said = '';
            for (const result of event.results) said += result[0].transcript;
            box.value = (before ? before + ' ' : '') + said.trim();
            // Blazor's @bind listens for input.
            box.dispatchEvent(new Event('input', { bubbles: true }));
        };
        recognition.onend = () => { if (active && active.recognition === recognition) stop(); };
        recognition.onerror = () => { if (active && active.recognition === recognition) stop(); };
        active = { recognition, button };
        button.setAttribute('aria-pressed', 'true');
        button.classList.add('is-listening');
        try {
            recognition.start();
        } catch {
            stop();
        }
    }

    document.addEventListener('click', (event) => {
        const button = event.target instanceof Element && event.target.closest('[data-gws-dictate]');
        if (!button || !Recognition) return;
        if (active && active.button === button) stop();
        else {
            stop();
            start(button);
        }
    });

    // Show the buttons where it works (the panel is drawn when it opens).
    if (!Recognition) return;
    let queued = false;
    function reveal() {
        queued = false;
        document.querySelectorAll('[data-gws-dictate][hidden]').forEach((button) => { button.hidden = false; });
    }
    new MutationObserver(() => {
        if (!queued) {
            queued = true;
            requestAnimationFrame(reveal);
        }
    }).observe(document.body, { childList: true, subtree: true });
    reveal();
})();
