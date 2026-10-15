// Public Sentinel forms (SentinelPublicForm.razor). The server checks every answer again; this
// only makes the page nicer: questions with a "show only if" condition appear and disappear as
// the answers change (a hidden question is disabled, so it isn't sent or required), answers are
// kept for the tab if a submission comes back with an error, and the Turnstile check is loaded.
(function () {
    function setup() {
        var form = document.querySelector('form[data-form-token]');
        if (!form || form.dataset.wired) return;
        form.dataset.wired = 'true';
        var storageKey = 'sentinel-form:' + form.dataset.formToken;
        var questions = Array.prototype.slice.call(form.querySelectorAll('[data-question]'));

        function answers(id) {
            return Array.prototype.slice.call(form.querySelectorAll('[name="' + id + '"]'))
                .filter(function (field) { return !field.disabled && (field.type === 'checkbox' ? field.checked : field.value.trim() !== ''); })
                .map(function (field) { return field.value.trim(); });
        }

        function isShown(question, seen) {
            var raw = question.dataset.condition;
            if (!raw) return true;
            if (seen.indexOf(question) >= 0) return false;
            var condition;
            try { condition = JSON.parse(raw); } catch (e) { return true; }
            var trigger = form.querySelector('[data-question="' + condition.when + '"]');
            if (!trigger || !isShown(trigger, seen.concat([question]))) return false;
            var given = answers(condition.when).map(function (v) { return v.toLowerCase(); });
            var value = (condition.value || '').trim().toLowerCase();
            switch (condition.op) {
                case 'equals': return given.indexOf(value) >= 0;
                case 'notEquals': return given.indexOf(value) < 0;
                case 'isChecked': return given.indexOf('true') >= 0;
                case 'isNotChecked': return given.indexOf('true') < 0;
                case 'isNotEmpty': return given.length > 0;
                default: return true;
            }
        }

        function refresh() {
            // Twice, so a question that depends on one that just appeared catches up.
            for (var pass = 0; pass < 2; pass++) {
                questions.forEach(function (question) {
                    var shown = isShown(question, []);
                    question.hidden = !shown;
                    question.querySelectorAll('input, select, textarea').forEach(function (field) { field.disabled = !shown; });
                });
            }
        }

        var params = new URLSearchParams(window.location.search);
        try {
            if (params.has('error')) {
                var saved = JSON.parse(sessionStorage.getItem(storageKey) || '{}');
                Object.keys(saved).forEach(function (name) {
                    form.querySelectorAll('[name="' + name + '"]').forEach(function (field) {
                        if (field.type === 'checkbox') field.checked = saved[name].indexOf(field.value) >= 0;
                        else field.value = saved[name][0] || '';
                    });
                });
            } else {
                sessionStorage.removeItem(storageKey);
            }
        } catch (e) { /* storage unavailable: nothing to restore */ }

        form.addEventListener('input', refresh);
        form.addEventListener('change', refresh);
        form.addEventListener('submit', function () {
            try {
                var data = {};
                questions.forEach(function (question) { data[question.dataset.question] = answers(question.dataset.question); });
                sessionStorage.setItem(storageKey, JSON.stringify(data));
            } catch (e) { /* storage unavailable: the answers just aren't kept */ }
        });
        refresh();

        var widget = form.querySelector('.sentinel-public-form-turnstile');
        if (widget) {
            var render = function () {
                window.turnstile.render(widget, { sitekey: widget.dataset.sitekey, action: widget.dataset.action });
            };
            if (window.turnstile) render();
            else {
                window.sentinelPublicFormTurnstileReady = render;
                var script = document.createElement('script');
                script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit&onload=sentinelPublicFormTurnstileReady';
                script.async = true;
                document.head.appendChild(script);
            }
        }
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', setup);
    else setup();
    document.addEventListener('blazor:enhancedload', setup);
})();
