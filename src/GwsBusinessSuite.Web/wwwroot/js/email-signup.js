// "Email signup" page widget (CmsBlockHtmlRenderer.RenderEmailSignup). Submits in place and shows
// the result; adds a Cloudflare Turnstile check when the server has it configured. Without this
// script the form still works as a normal POST that returns a full page.
(function () {
    'use strict';

    var forms = document.querySelectorAll('form[data-gws-email-signup-form]');
    if (!forms.length) return;

    function setStatus(form, text, isError) {
        var status = form.querySelector('.gws-email-signup-status');
        status.textContent = text;
        status.classList.toggle('is-error', !!isError);
    }

    function wire(form, siteKey) {
        var button = form.querySelector('.gws-email-signup-submit');
        var label = button.textContent;
        var widgetId = null;
        var verified = !siteKey;
        var sending = false;

        if (siteKey && window.turnstile) {
            button.disabled = true;
            widgetId = window.turnstile.render(form.querySelector('.gws-email-signup-verification'), {
                sitekey: siteKey,
                action: 'subscribe',
                size: 'flexible',
                callback: function () { verified = true; button.disabled = sending; },
                'expired-callback': function () { verified = false; button.disabled = true; },
                'error-callback': function () { verified = false; button.disabled = true; setStatus(form, 'Verification could not load. Please refresh the page and try again.', true); }
            });
        }

        form.addEventListener('submit', async function (event) {
            event.preventDefault();
            if (sending) return;
            if (!verified) { setStatus(form, 'Please complete the verification first.', true); return; }
            var email = form.querySelector('input[name="email"]');
            if (!email.checkValidity()) { setStatus(form, 'Please enter a valid email address.', true); email.focus(); return; }

            sending = true;
            button.disabled = true;
            button.textContent = 'Signing you up…';
            setStatus(form, '', false);
            try {
                var response = await fetch(form.action, {
                    method: 'POST',
                    headers: { Accept: 'application/json' },
                    body: new FormData(form),
                    credentials: 'same-origin'
                });
                var data = await response.json().catch(function () { return {}; });
                if (response.ok && data.ok) {
                    form.classList.add('is-done');
                    setStatus(form, form.dataset.success || data.message, false);
                    return;
                }
                setStatus(form, response.status === 429
                    ? 'Too many attempts. Please wait a few minutes and try again.'
                    : (data.error || 'Something went wrong. Please try again shortly.'), true);
            } catch (_) {
                setStatus(form, 'We could not reach the server. Please check your connection and try again.', true);
            }
            sending = false;
            button.textContent = label;
            if (widgetId !== null) { verified = false; window.turnstile.reset(widgetId); button.disabled = true; }
            else button.disabled = false;
        });
    }

    function wireAll(siteKey) {
        forms.forEach(function (form) { wire(form, siteKey); });
    }

    fetch('/campaigns/signup-config', { headers: { Accept: 'application/json' } })
        .then(function (response) { return response.ok ? response.json() : {}; })
        .catch(function () { return {}; })
        .then(function (config) {
            var siteKey = config && config.turnstileSiteKey;
            if (!siteKey) { wireAll(null); return; }
            if (window.turnstile) { wireAll(siteKey); return; }
            window.gwsEmailSignupTurnstileReady = function () { wireAll(siteKey); };
            var script = document.createElement('script');
            script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit&onload=gwsEmailSignupTurnstileReady';
            script.async = true;
            script.defer = true;
            document.head.appendChild(script);
        });
})();
