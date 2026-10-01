(function () {
    'use strict';

    window.gwsContactTurnstileReady = function () {
        document.querySelectorAll('form[data-contact-form]').forEach(function (form) {
            const container = form.querySelector('.gws-contact-verification');
            const button = form.querySelector('button[type="submit"]');
            const status = form.querySelector('.gws-form-status');
            if (!container || container.dataset.initialized) return;
            container.dataset.initialized = 'true';
            let verified = false;
            let sending = false;
            let keepError = false;
            const label = button.textContent;

            function resetVerification(message) {
                verified = false;
                button.disabled = true;
                if (!sending && !keepError) status.textContent = message;
            }

            const widget = window.turnstile.render(container, {
                sitekey: container.dataset.sitekey,
                action: 'contact',
                size: 'flexible',
                callback: function () {
                    verified = true;
                    button.disabled = sending;
                    if (!sending && !keepError) status.textContent = 'Verification complete. You can send your message.';
                },
                'expired-callback': function () {
                    resetVerification('Verification expired. Please verify again to send your message.');
                },
                'error-callback': function () {
                    resetVerification('Verification could not load. Please refresh the page and try again.');
                },
                'timeout-callback': function () {
                    resetVerification('Verification timed out. Please try the verification again.');
                }
            });

            form.addEventListener('submit', async function (event) {
                event.preventDefault();
                if (sending || !verified) return;
                sending = true;
                keepError = false;
                button.disabled = true;
                button.textContent = 'Sending…';
                status.textContent = 'Sending your message…';
                try {
                    const response = await fetch(form.action, {
                        method: 'POST',
                        headers: { Accept: 'application/json' },
                        body: new FormData(form),
                        credentials: 'same-origin'
                    });
                    const data = await response.json().catch(function () { return {}; });
                    if (response.ok && data.redirectUrl) {
                        const destination = new URL(data.redirectUrl, window.location.origin);
                        if (destination.origin !== window.location.origin) throw new Error('Unexpected redirect');
                        window.location.assign(destination.href);
                        return;
                    }
                    status.textContent = response.status === 429
                        ? 'Too many attempts. Please wait a few minutes, then verify again and resend.'
                        : data.error || 'Your message could not be sent. Please try again shortly.';
                } catch (_) {
                    status.textContent = 'We could not confirm delivery. Your message is still here. Please check your connection before trying again.';
                }
                sending = false;
                verified = false;
                keepError = true;
                button.disabled = true;
                button.textContent = label;
                window.turnstile.reset(widget);
            });
        });
    };

    // A blocked provider script never calls onload; keep the form closed and explain why.
    window.setTimeout(function () {
        document.querySelectorAll('form[data-contact-form]').forEach(function (form) {
            const container = form.querySelector('.gws-contact-verification');
            if (container && !container.dataset.initialized) {
                form.querySelector('.gws-form-status').textContent =
                    'Verification could not load. Please refresh the page and try again.';
            }
        });
    }, 15000);
})();
