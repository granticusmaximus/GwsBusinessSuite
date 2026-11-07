// GWS Business Suite as an installed app (PWA, 07-11-26).
// - Registers sw.js for /admin, so the app can be installed and shows an offline page.
// - Phones and tablets get a one-time offer to install it: Install where the browser can prompt
//   (Android, Chrome, Edge), or how to add it on iPhone and iPad, where no browser can: from the
//   Share menu, in Safari or (iOS 16.4 and later) Chrome, Edge or Firefox. Desktop browsers have
//   their own install button, and the compact-layout offer sits where this would.
// - In the installed app on iPhone and iPad, downloads go to the Share sheet (see the end).
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

    // ---- Downloads in the installed app on iPhone and iPad (07-11-26) ----
    // There a download link (a World News story's PDF, an invoice, a page export) opens the file
    // over the whole app, with no address bar or Back button to leave it by: Grant was left on a
    // story's PDF. So in the installed app the file goes to the Share sheet instead (Save to Files,
    // Books, Print, AirDrop) and the app stays on the page it was on. Browsers keep their own
    // downloads, and so do Android and desktop installs, which have a downloads list.
    if (!(standalone && ios && canShareFiles())) return;

    function canShareFiles() {
        if (typeof navigator.share !== 'function' || typeof navigator.canShare !== 'function') return false;
        try {
            return navigator.canShare({ files: [new File(['%PDF-'], 'check.pdf', { type: 'application/pdf' })] });
        } catch {
            return false;
        }
    }

    const downloading = new WeakSet();
    let downloadCard = null;

    document.addEventListener('click', (event) => {
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        const link = event.target instanceof Element ? event.target.closest('a[download][href]') : null;
        if (!link) return;
        const url = new URL(link.href, location.href);
        // Another site's file can't be fetched from here; it opens in the app's own browser sheet,
        // which has a Done button.
        if (url.protocol !== 'data:' && url.origin !== location.origin) return;
        event.preventDefault();
        saveFromLink(link, url);
    });

    async function saveFromLink(link, url) {
        if (downloading.has(link)) return; // a second tap while the first one's file is on its way
        downloading.add(link);
        link.setAttribute('aria-busy', 'true');
        let file;
        try {
            const response = await fetch(url.href, { credentials: 'same-origin' });
            if (!response.ok) throw new Error(`The server answered ${response.status}.`);
            const blob = await response.blob();
            file = new File([blob], fileName(response, link, url), { type: blob.type || 'application/octet-stream' });
        } catch {
            showCard("That file didn't download", 'Check the connection, then try again.');
            return;
        } finally {
            downloading.delete(link);
            link.removeAttribute('aria-busy');
        }
        await share(file);
    }

    async function share(file) {
        if (!navigator.canShare({ files: [file] })) {
            showCard("This file can't be saved from the app", file.name);
            return;
        }
        try {
            await navigator.share({ files: [file] });
            removeCard();
        } catch (error) {
            const name = error && error.name;
            if (name === 'AbortError') {
                removeCard(); // the Share sheet was closed
            } else if (name === 'NotAllowedError') {
                // A tap only counts for a few seconds, and a big file can take longer to arrive:
                // one more tap opens the Share sheet.
                showCard('Your file is ready', file.name, file);
            } else {
                showCard("That file couldn't be shared", file.name);
            }
        }
    }

    // The server's name for the file (Content-Disposition, its UTF-8 form first), else the link's
    // download name, else the last part of the address.
    function fileName(response, link, url) {
        const disposition = response.headers.get('Content-Disposition') || '';
        const utf8 = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(disposition);
        if (utf8) {
            try {
                return decodeURIComponent(utf8[1].trim());
            } catch {
                // Badly encoded: the plain name below.
            }
        }
        const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(disposition);
        if (plain && plain[1].trim()) return plain[1].trim();
        const named = (link.getAttribute('download') || '').trim();
        if (named) return named;
        if (url.protocol === 'http:' || url.protocol === 'https:') {
            try {
                const last = decodeURIComponent(url.pathname.split('/').pop() || '');
                if (last) return last;
            } catch {
                // Badly encoded: the default below.
            }
        }
        return 'download';
    }

    // A card above the bottom bar, like the install offer's: what happened, with Save or share
    // when the file is waiting for a tap.
    function showCard(title, detail, file) {
        removeCard();
        const card = document.createElement('aside');
        card.id = 'gws-download-card';
        card.className = 'gws-install-offer';
        card.setAttribute('role', 'status');

        const text = document.createElement('div');
        text.className = 'gws-install-offer-text';
        const heading = document.createElement('strong');
        heading.textContent = title;
        const body = document.createElement('span');
        body.textContent = detail;
        text.append(heading, body);

        const actions = document.createElement('div');
        actions.className = 'gws-install-offer-actions';
        if (file) {
            const save = document.createElement('button');
            save.type = 'button';
            save.className = 'btn btn-sm btn-primary';
            save.textContent = 'Save or share';
            save.addEventListener('click', () => share(file));
            actions.append(save);
        }
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'btn btn-sm btn-outline-secondary';
        close.textContent = 'Close';
        close.addEventListener('click', removeCard);
        actions.append(close);

        card.append(text, actions);
        document.body.append(card);
        downloadCard = card;
    }

    function removeCard() {
        downloadCard?.remove();
        downloadCard = null;
    }
})();
