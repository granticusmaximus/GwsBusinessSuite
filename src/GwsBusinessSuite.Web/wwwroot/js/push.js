// This device's notifications as Web Push (PWA phase 2, 07-11-26), from the bell's panel
// (NotificationBell: [data-gws-push]). Turning them on subscribes this browser or installed app
// with the server's key and saves it (/admin/api/push/*); WebPushDispatcher then sends the bell's
// notifications here too, and sw.js shows them.
//
// The buttons are handled here, not through Blazor: Safari only asks for permission from inside
// the tap itself, and a round trip to the server first would lose it. On iPhone and iPad, push
// works only in the app added to the Home Screen, so a Safari tab says how to get it.
(function () {
    'use strict';
    if (!location.pathname.toLowerCase().startsWith('/admin')) return;

    const ua = navigator.userAgent;
    const appleTouch = /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
    const standalone = window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
    const supported = window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;

    let registration = null;
    let publicKey = null;

    async function prepare() {
        if (!supported) return;
        if (!registration) registration = await navigator.serviceWorker.getRegistration('/admin') || null;
        if (!publicKey) {
            const response = await fetch('/admin/api/push/key', { credentials: 'same-origin' });
            if (response.ok) publicKey = (await response.json()).publicKey;
        }
    }

    async function currentState() {
        if (!supported) return appleTouch && !standalone ? 'add-to-home' : 'unsupported';
        if (Notification.permission === 'denied') return 'blocked';
        await prepare();
        if (!registration || !publicKey) return 'unsupported';
        return await registration.pushManager.getSubscription() ? 'on' : 'off';
    }

    const words = {
        'add-to-home': 'To get these on this iPhone or iPad, add the app to your Home Screen (Share, then Add to Home Screen), then turn them on there.',
        blocked: 'Notifications are blocked for this site. Allow them in the browser\'s settings to get these here.',
        on: 'Notifications are on for this device.',
        off: 'Get these on this device, even when the app is closed.'
    };

    async function render(panel, note) {
        const state = await currentState().catch(() => 'unsupported');
        const text = panel.querySelector('[data-gws-push-text]');
        const toggle = panel.querySelector('[data-gws-push-toggle]');
        const test = panel.querySelector('[data-gws-push-test]');
        panel.dataset.state = state;
        panel.hidden = state === 'unsupported';
        text.textContent = note || words[state] || '';
        toggle.hidden = state !== 'on' && state !== 'off';
        toggle.textContent = state === 'on' ? 'Turn off' : 'Turn on';
        test.hidden = state !== 'on';
    }

    function deviceName() {
        const where = /iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) ? 'iPad'
            : /iPhone/.test(ua) ? 'iPhone' : /Android/.test(ua) ? 'Android' : /Macintosh/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : 'This device';
        const how = standalone ? 'app' : /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox'
            : /Chrome\/|CriOS/.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'browser';
        return `${where} (${how})`;
    }

    function keyBytes(base64Url) {
        const padded = (base64Url + '='.repeat((4 - base64Url.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
        return Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
    }

    async function post(path, body) {
        const response = await fetch('/admin/api/push/' + path, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'X-GWS-Push': '1' },
            body: JSON.stringify(body || {})
        });
        const json = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(json.error || 'The server said no.');
        return json;
    }

    async function turnOn(panel) {
        if (!registration || !publicKey) {
            await prepare().catch(() => undefined);
            return render(panel, 'Still getting ready. Try again in a moment.');
        }
        try {
            // Straight from the tap: this is what asks for permission.
            const subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
            const json = subscription.toJSON();
            try {
                await post('subscriptions', { endpoint: json.endpoint, keys: json.keys, device: deviceName() });
            } catch (error) {
                await subscription.unsubscribe().catch(() => undefined);
                throw error;
            }
            await render(panel, 'Notifications are on for this device. Send a test to check.');
        } catch (error) {
            await render(panel, error && error.name === 'NotAllowedError'
                ? 'Notifications weren\'t allowed. You can allow them in the browser\'s settings.'
                : 'Couldn\'t turn them on: ' + ((error && error.message) || 'unknown error') + '.');
        }
    }

    async function turnOff(panel) {
        const subscription = registration && await registration.pushManager.getSubscription();
        if (subscription) {
            await post('unsubscribe', { endpoint: subscription.endpoint }).catch(() => undefined);
            await subscription.unsubscribe().catch(() => undefined);
        }
        await render(panel, 'Notifications are off for this device.');
    }

    document.addEventListener('click', (event) => {
        const button = event.target instanceof Element && event.target.closest('[data-gws-push-toggle], [data-gws-push-test]');
        const panel = button && button.closest('[data-gws-push]');
        if (!panel) return;
        if (button.hasAttribute('data-gws-push-test')) {
            post('test').then(
                (result) => render(panel, result.sent > 0 ? 'Sent. It should arrive in a few seconds.' : 'Nothing was sent: turn notifications on again.'),
                (error) => render(panel, 'The test wasn\'t sent: ' + error.message));
            return;
        }
        if (panel.dataset.state === 'on') turnOff(panel);
        else turnOn(panel);
    });

    // The bell's panel is drawn when it opens: fill in each new one.
    const seen = new WeakSet();
    let queued = false;
    function scan() {
        queued = false;
        document.querySelectorAll('[data-gws-push]').forEach((panel) => {
            if (seen.has(panel)) return;
            seen.add(panel);
            render(panel);
        });
    }
    new MutationObserver(() => {
        if (!queued) {
            queued = true;
            requestAnimationFrame(scan);
        }
    }).observe(document.body, { childList: true, subtree: true });
    scan();
})();
