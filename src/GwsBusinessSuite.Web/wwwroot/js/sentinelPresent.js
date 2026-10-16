// Presentation mode (SentinelPresent.razor, Sentinel item 18). One slide at a time:
//   next  - Right, Down, Space, Page Down, Enter, N, a click on the right two thirds, swipe left
//   back  - Left, Up, Page Up, Backspace, P, a click on the left third, swipe right
//   Home / End - first / last slide;  F - full screen;  S - speaker view;  Esc - exit
// The slide number lives in the address (#3), so a reload stays put. The speaker view is the same
// page in a second window (?speaker=1), kept on the same slide over a BroadcastChannel, showing
// the next slide and a timer. Code, equations and diagrams are drawn as each slide first shows.
(function () {
    function setup() {
        var root = document.querySelector('.sentinel-present');
        if (!root || root.dataset.wired) return;
        root.dataset.wired = 'true';
        var slides = Array.prototype.slice.call(root.querySelectorAll('.sentinel-slide'));
        if (!slides.length) return;
        var counter = root.querySelector('.sentinel-present-counter');
        var exitUrl = root.dataset.presentExit || '/admin/sentinel';
        var isSpeaker = new URLSearchParams(window.location.search).get('speaker') === '1';
        var channel = 'BroadcastChannel' in window ? new BroadcastChannel('sentinel-present:' + root.dataset.presentKey) : null;
        var hydrated = new Set();
        var current = 0;

        function clamp(index) { return Math.max(0, Math.min(slides.length - 1, index)); }

        function hydrate(slide) {
            if (hydrated.has(slide) || !window.wikiRichContentHydrate) return;
            hydrated.add(slide);
            window.wikiRichContentHydrate(slide);
        }

        function show(index, fromChannel) {
            current = clamp(index);
            slides.forEach(function (slide, i) { slide.hidden = i !== current; });
            hydrate(slides[current]);
            if (counter) counter.textContent = (current + 1) + ' / ' + slides.length;
            // The full path: the app's <base href="/"> would resolve a bare "#3" to the site root.
            try { history.replaceState(null, '', window.location.pathname + window.location.search + '#' + (current + 1)); } catch (e) { /* sandboxed */ }
            if (isSpeaker) renderSpeaker();
            if (channel && !fromChannel) channel.postMessage({ slide: current });
        }

        function exit() {
            if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen().catch(function () { });
            if (isSpeaker) { window.close(); return; }
            window.location.href = exitUrl;
        }

        function toggleFullscreen() {
            if (document.fullscreenElement) document.exitFullscreen().catch(function () { });
            else if (root.requestFullscreen) root.requestFullscreen().catch(function () { });
        }

        function openSpeaker() {
            var url = window.location.pathname + '?speaker=1#' + (current + 1);
            window.open(url, 'sentinel-speaker-' + root.dataset.presentKey, 'width=1000,height=640');
        }

        // Speaker view: the current slide large, the next one small, a counter and a timer.
        var speaker = root.querySelector('.sentinel-present-speaker');
        var started = Date.now();
        function renderSpeaker() {
            if (!speaker) return;
            speaker.querySelector('.sentinel-present-speaker-counter').textContent = 'Slide ' + (current + 1) + ' of ' + slides.length;
            var next = speaker.querySelector('.sentinel-present-speaker-next-slide');
            next.replaceChildren();
            if (current + 1 < slides.length) {
                var copy = slides[current + 1].cloneNode(true);
                copy.hidden = false;
                copy.removeAttribute('data-slide');
                next.appendChild(copy);
            } else {
                next.textContent = 'End of the presentation';
            }
        }
        if (isSpeaker && speaker) {
            root.classList.add('is-speaker');
            speaker.hidden = false;
            var timer = speaker.querySelector('.sentinel-present-speaker-timer');
            setInterval(function () {
                var seconds = Math.floor((Date.now() - started) / 1000);
                timer.textContent = String(Math.floor(seconds / 60)).padStart(2, '0') + ':' + String(seconds % 60).padStart(2, '0');
            }, 1000);
        }
        if (channel) channel.onmessage = function (event) {
            if (event.data && Number.isInteger(event.data.slide) && event.data.slide !== current) show(event.data.slide, true);
        };

        document.addEventListener('keydown', function (event) {
            if (event.altKey || event.ctrlKey || event.metaKey) return;
            if (event.target.closest && event.target.closest('input, textarea, select, [contenteditable="true"]')) return;
            var key = event.key;
            var handled = true;
            if (key === 'ArrowRight' || key === 'ArrowDown' || key === ' ' || key === 'PageDown' || key === 'Enter' || key === 'n' || key === 'N') show(current + 1);
            else if (key === 'ArrowLeft' || key === 'ArrowUp' || key === 'PageUp' || key === 'Backspace' || key === 'p' || key === 'P') show(current - 1);
            else if (key === 'Home') show(0);
            else if (key === 'End') show(slides.length - 1);
            else if (key === 'f' || key === 'F') toggleFullscreen();
            else if ((key === 's' || key === 'S') && !isSpeaker) openSpeaker();
            else if (key === 'Escape') exit();
            else handled = false;
            if (handled) event.preventDefault();
        });

        root.addEventListener('click', function (event) {
            var control = event.target.closest('[data-present]');
            if (control) {
                var action = control.dataset.present;
                if (action === 'exit') { event.preventDefault(); exit(); return; }
                if (action === 'next') show(current + 1);
                else if (action === 'prev') show(current - 1);
                else if (action === 'fullscreen') toggleFullscreen();
                else if (action === 'speaker') openSpeaker();
                return;
            }
            // Links, media and anything else interactive keep their own click.
            if (event.target.closest('a, button, video, audio, iframe, input, summary, details, .sentinel-present-speaker')) return;
            var bounds = root.getBoundingClientRect();
            show(event.clientX < bounds.left + bounds.width / 3 ? current - 1 : current + 1);
        });

        var touchX = null;
        root.addEventListener('touchstart', function (event) { touchX = event.touches[0].clientX; }, { passive: true });
        root.addEventListener('touchend', function (event) {
            if (touchX === null) return;
            var dx = event.changedTouches[0].clientX - touchX;
            touchX = null;
            if (Math.abs(dx) > 50) show(dx < 0 ? current + 1 : current - 1);
        });

        var fromHash = parseInt(window.location.hash.replace('#', ''), 10);
        root.classList.add('is-ready');
        show(Number.isFinite(fromHash) ? fromHash - 1 : 0, true);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();
    document.addEventListener('blazor:enhancedload', setup);
})();
