// GWS Business Suite as an installed app (PWA, 07-11-26).
// - Registers sw.js for /admin, so the app can be installed and shows an offline page.
// - Phones and tablets get a one-time offer to install it: Install where the browser can prompt
//   (Android, Chrome, Edge), or how to add it on iPhone and iPad, where no browser can: from the
//   Share menu, in Safari or (iOS 16.4 and later) Chrome, Edge or Firefox. Desktop browsers have
//   their own install button, and the compact-layout offer sits where this would.
// - Not inside the Mac or iOS app's own web view (no service workers there, nothing to install).
(function () {
    'use strict';

    const onAdmin = () => location.pathname.toLowerCase().startsWith('/admin');
    if (!onAdmin()) return;

    if ('serviceWorker' in navigator && window.isSecureContext) {
        navigator.serviceWorker.register('/sw.js', { scope: '/admin' }).catch(() => {
            // Not supported here (an app's web view, private browsing): the app works the same.
        });
    }

    const storageKey = 'gws-install-offer';
    const ua = navigator.userAgent;
    const standalone = window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
    const touch = window.matchMedia('(pointer: coarse)').matches;
    // iPadOS reports itself as a Mac; a touch screen gives it away.
    const ios = /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
    // How to add it on iPhone and iPad (07-11-26: Grant looked in Chrome on his iPad and found no way).
    // Every browser there is Safari's engine and none can prompt; each adds to the Home Screen from
    // its own Share menu, Chrome, Edge and Firefox only since iOS 16.4. Null where it can't.
    const iosVersion = /OS (\d+)_(\d+)/.exec(ua);
    const iosNewEnough = !iosVersion || Number(iosVersion[1]) > 16 || (Number(iosVersion[1]) === 16 && Number(iosVersion[2]) >= 4);
    const iosHow = !ios ? null
        : /CriOS\//.test(ua) ? (iosNewEnough ? 'In Chrome, tap the Share button (by the address bar), then Add to Home Screen.' : null)
        : /EdgiOS\//.test(ua) ? (iosNewEnough ? 'In Edge, tap the Share button, then Add to Home Screen.' : null)
        : /FxiOS\//.test(ua) ? (iosNewEnough ? 'In Firefox, open the menu, tap Share, then Add to Home Screen.' : null)
        : /Version\/[\d.]+.*Safari\//.test(ua) && !/GSA\//.test(ua) ? 'Tap the Share button, then Add to Home Screen.'
        : null;

    function answered() {
        try { return localStorage.getItem(storageKey) !== null; } catch { return false; }
    }

    function answer(value) {
        try { localStorage.setItem(storageKey, value); } catch { /* private mode: asks again next time */ }
        document.getElementById('gws-install-offer')?.remove();
        offer = null;
    }

    let deferredPrompt = null;
    let offer = null;

    // iosHow: how to add it on iPhone and iPad, or null for the browser's own Install prompt.
    function build(iosHow) {
        const aside = document.createElement('aside');
        aside.id = 'gws-install-offer';
        aside.className = 'gws-install-offer';
        aside.setAttribute('aria-label', 'Install the app');

        const icon = document.createElement('img');
        icon.src = '/img/app-icons/icon-192.png';
        icon.alt = '';
        icon.className = 'gws-install-offer-icon';

        const text = document.createElement('div');
        text.className = 'gws-install-offer-text';
        const title = document.createElement('strong');
        title.textContent = 'Install GWS Business Suite';
        const body = document.createElement('span');
        body.textContent = (iosHow || 'Add it to your home screen.') + ' It opens full screen, like an app.';
        text.append(title, body);

        const actions = document.createElement('div');
        actions.className = 'gws-install-offer-actions';
        if (!iosHow) {
            const install = document.createElement('button');
            install.type = 'button';
            install.className = 'btn btn-sm btn-primary';
            install.textContent = 'Install';
            install.addEventListener('click', async () => {
                const prompt = deferredPrompt;
                deferredPrompt = null;
                if (!prompt) return answer('unavailable');
                prompt.prompt();
                const choice = await prompt.userChoice.catch(() => ({ outcome: 'dismissed' }));
                answer(choice.outcome === 'accepted' ? 'installed' : 'dismissed');
            });
            actions.append(install);
        }
        const later = document.createElement('button');
        later.type = 'button';
        later.className = 'btn btn-sm btn-outline-secondary';
        later.textContent = iosHow ? 'Got it' : 'Not now';
        later.addEventListener('click', () => answer('dismissed'));
        actions.append(later);

        aside.append(icon, text, actions);
        return aside;
    }

    function show(iosHow) {
        if (standalone || !touch || answered() || !onAdmin()) return;
        offer = offer || build(iosHow);
        if (!offer.isConnected) document.body.append(offer);
    }

    window.addEventListener('beforeinstallprompt', (event) => {
        // Kept for the offer's Install button instead of the browser's own mini-bar.
        event.preventDefault();
        deferredPrompt = event;
        show(null);
    });

    window.addEventListener('appinstalled', () => answer('installed'));

    if (iosHow) {
        // After the page has settled, not over the first thing someone reads.
        setTimeout(() => show(iosHow), 4000);
    }

    // Blazor's enhanced navigation can drop elements it didn't render: put the offer back.
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', () => {
            if (offer && !offer.isConnected && onAdmin() && !answered()) document.body.append(offer);
        });
    }
})();
