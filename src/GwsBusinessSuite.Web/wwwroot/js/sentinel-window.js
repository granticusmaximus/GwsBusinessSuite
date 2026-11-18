// Opens Sentinel on its own, with no other part of the app around it (18-11-26).
//  - In the Mac app: a new app window, through gws-native://sentinel-window (MainPage handles it).
//  - In a browser: a popup window of /admin/sentinel/window.
// Used by any element with data-gws-sentinel-window (the dashboard button), and by a right-click
// menu on the nav's Sentinel links: the Knowledge rail button, a pinned Sentinel page on the rail
// and the Sentinel entry in the side menu. Shift+right-click keeps the normal menu.
(() => {
    const WINDOW_PATH = '/admin/sentinel/window';
    const SENTINEL_PATHS = ['/admin/sentinel', '/admin/wiki', WINDOW_PATH];
    const KEPT_KEYS = ['page', 'database', 'row'];

    const inMacApp = () => window.__gwsMacCatalystWebView === true;

    // The page or database a Sentinel link points at, as "?page=..." (or "" for Sentinel's home);
    // null when the link isn't a Sentinel link.
    function sentinelQuery(href) {
        let url;
        try { url = new URL(href, document.baseURI); } catch { return null; }
        if (url.origin !== location.origin) return null;
        const path = url.pathname.replace(/\/+$/, '').toLowerCase();
        if (!SENTINEL_PATHS.includes(path)) return null;
        const kept = new URLSearchParams();
        for (const key of KEPT_KEYS) {
            const value = url.searchParams.get(key);
            if (value) kept.set(key, value);
        }
        const query = kept.toString();
        return query ? `?${query}` : '';
    }

    function openWindow(query) {
        query = query || '';
        if (inMacApp()) {
            api.navigate(`gws-native://sentinel-window${query}`);
            return;
        }
        const popup = window.open(WINDOW_PATH + query, 'gws-sentinel', 'popup,width=1180,height=820');
        if (popup) popup.focus();
        else location.assign(WINDOW_PATH + query);
    }

    function openHere(query) {
        const target = `/admin/sentinel${query || ''}`;
        if (window.Blazor && typeof window.Blazor.navigateTo === 'function') window.Blazor.navigateTo(target);
        else location.assign(target);
    }

    // What a right-click on this element should offer, or null for the normal menu.
    function menuTarget(element) {
        const explicit = element.closest('[data-gws-sentinel-window]');
        if (explicit) return { query: sentinelQuery(explicit.getAttribute('href') || WINDOW_PATH) ?? '' };
        if (!element.closest('.gws-nav')) return null;
        if (element.closest('[data-gws-workspace="knowledge"]')) return { query: '' };
        const link = element.closest('a[href]');
        if (!link) return null;
        const query = sentinelQuery(link.getAttribute('href'));
        return query === null ? null : { query };
    }

    let menu = null;
    let returnFocus = null;

    function closeMenu(restoreFocus) {
        if (!menu) return;
        menu.remove();
        menu = null;
        if (restoreFocus && returnFocus && typeof returnFocus.focus === 'function') returnFocus.focus();
        returnFocus = null;
    }

    function menuItem(label, icon, action) {
        const item = document.createElement('button');
        item.type = 'button';
        item.className = 'gws-context-menu-item';
        item.setAttribute('role', 'menuitem');
        item.tabIndex = -1;
        const glyph = document.createElement('i');
        glyph.className = `bi ${icon}`;
        glyph.setAttribute('aria-hidden', 'true');
        const text = document.createElement('span');
        text.textContent = label;
        item.append(glyph, text);
        item.addEventListener('click', () => {
            closeMenu(false);
            action();
        });
        return item;
    }

    function showMenu(x, y, query, opener) {
        closeMenu(false);
        returnFocus = opener;
        menu = document.createElement('div');
        menu.className = 'gws-context-menu';
        menu.setAttribute('role', 'menu');
        menu.setAttribute('aria-label', 'Sentinel');
        menu.append(
            menuItem(inMacApp() ? 'Open Sentinel in New App Window' : 'Open Sentinel in New Window', 'bi-window-plus', () => openWindow(query)),
            menuItem('Open Sentinel Here', 'bi-journal-bookmark-fill', () => openHere(query)));
        document.body.append(menu);

        const box = menu.getBoundingClientRect();
        const left = Math.max(8, Math.min(x, window.innerWidth - box.width - 8));
        const top = Math.max(8, Math.min(y, window.innerHeight - box.height - 8));
        menu.style.left = `${left}px`;
        menu.style.top = `${top}px`;
        menu.querySelector('.gws-context-menu-item')?.focus();

        menu.addEventListener('keydown', event => {
            const items = [...menu.querySelectorAll('.gws-context-menu-item')];
            const index = items.indexOf(document.activeElement);
            if (event.key === 'Escape' || event.key === 'Tab') {
                event.preventDefault();
                closeMenu(true);
            } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                const step = event.key === 'ArrowDown' ? 1 : -1;
                items[(index + step + items.length) % items.length].focus();
            }
        });
    }

    window.addEventListener('contextmenu', event => {
        if (event.shiftKey || !(event.target instanceof Element)) return;
        const target = menuTarget(event.target);
        if (!target) return;
        event.preventDefault();
        const opener = event.target.closest('a, button') || event.target;
        // The context-menu key and Shift+F10 report no pointer position: open beside the element.
        let { clientX: x, clientY: y } = event;
        if (x === 0 && y === 0) {
            const rect = opener.getBoundingClientRect();
            x = rect.right;
            y = rect.top;
        }
        showMenu(x, y, target.query, opener);
    }, true);

    // Capture phase, ahead of Blazor's link handling and the nav loading bar, so the button opens
    // the window instead of navigating this one. A modified click keeps the browser's own handling.
    window.addEventListener('click', event => {
        if (menu && !(event.target instanceof Element && menu.contains(event.target))) closeMenu(false);
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        const button = event.target instanceof Element ? event.target.closest('[data-gws-sentinel-window]') : null;
        if (!button) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        openWindow(sentinelQuery(button.getAttribute('href') || WINDOW_PATH) ?? '');
    }, true);

    window.addEventListener('blur', () => closeMenu(false));
    window.addEventListener('resize', () => closeMenu(false));
    document.addEventListener('scroll', () => closeMenu(false), true);

    const api = {
        open: openWindow,
        sentinelQuery,
        // The Mac app's request; a test replaces it to see the address.
        navigate: url => { location.href = url; }
    };
    window.gwsSentinelWindow = api;
})();
