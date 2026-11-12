// World News page helpers (12-11-26): the address bar, the keyboard, and Share. The page's
// state lives in Blazor; this only mirrors it into the URL and turns keys into clicks on the
// page's own controls, so there is one code path for mouse, keyboard and the Mac app.

// The content area the page's @container gws-main breakpoints measure (its content box).
export function isWide(element, minimum) {
    const content = element && element.closest('.gws-content');
    if (!content) return window.innerWidth >= minimum;
    const style = getComputedStyle(content);
    const width = content.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0');
    return width >= minimum;
}

// Mirrors the page's state into the address without a navigation, so reload, the Mac app
// reopening and a copied link land on the same view. replaceState: Back leaves the page, as
// it does from Mail's selection.
export function setUrl(url) {
    try {
        const target = new URL(url, document.baseURI);
        if (target.href !== location.href) history.replaceState(history.state, '', target.href);
    } catch {
        // A malformed URL leaves the address as it was.
    }
}

// Share sheet where there is one (Safari, the Mac app, phones), else the clipboard.
export async function share(url, title) {
    const href = new URL(url, document.baseURI).href;
    if (typeof navigator.share === 'function') {
        try {
            await navigator.share({ title: title, url: href });
            return 'shared';
        } catch (error) {
            if (error && error.name === 'AbortError') return 'cancelled';
        }
    }
    try {
        await navigator.clipboard.writeText(href);
        return 'copied';
    } catch {
        return 'failed';
    }
}

function isTyping(target) {
    if (!target) return false;
    const tag = (target.tagName || '').toLowerCase();
    return tag === 'input' || tag === 'textarea' || tag === 'select' || target.isContentEditable;
}

function visible(element) {
    return !!element && element.offsetParent !== null;
}

// Up/Down move through the list (like Mail), Return opens (native for links), Escape goes back,
// "/" jumps to search. In the Mac app: Cmd+F search, Cmd+R read now, Cmd+[ back. Browsers keep
// their own Cmd+F and Cmd+R.
export function attach(root) {
    const macApp = window.__gwsMacCatalystWebView === true;
    const click = function (selector) {
        const target = root.querySelector(selector);
        if (visible(target)) { target.click(); return true; }
        return false;
    };
    const focusSearch = function () {
        const search = root.querySelector('.world-news-search');
        if (!search) return false;
        search.focus();
        search.select();
        return true;
    };
    const move = function (step) {
        const rows = Array.from(root.querySelectorAll('.world-news-list .world-news-event')).filter(visible);
        if (rows.length === 0) return false;
        const current = rows.findIndex(function (row) { return row === document.activeElement || row.getAttribute('aria-current') === 'true'; });
        const next = rows[Math.min(rows.length - 1, Math.max(0, current < 0 ? 0 : current + step))];
        next.focus({ preventScroll: false });
        next.scrollIntoView({ block: 'nearest' });
        next.click();
        return true;
    };
    const onKey = function (event) {
        if (!root.isConnected) return;
        if (document.querySelector('.world-news-document-modal')) return; // the dialog has its own keys
        const command = event.metaKey || event.ctrlKey;
        let handled = false;
        if (macApp && command && !event.altKey) {
            if (event.key === 'f' || event.key === 'F') handled = focusSearch();
            else if (event.key === 'r' || event.key === 'R') handled = click('.world-news-refresh');
            else if (event.key === '[') handled = click('.world-news-back');
        } else if (!command && !event.altKey) {
            if (event.key === 'Escape' && isTyping(event.target) && event.target.classList.contains('world-news-search')) {
                event.target.blur();
                handled = true;
            } else if (isTyping(event.target)) {
                return;
            } else if (event.key === '/') {
                handled = focusSearch();
            } else if (event.key === 'ArrowDown' || event.key === 'j') {
                handled = move(1);
            } else if (event.key === 'ArrowUp' || event.key === 'k') {
                handled = move(-1);
            } else if (event.key === 'Escape') {
                handled = click('.world-news-back');
            }
        }
        if (handled) event.preventDefault();
    };
    document.addEventListener('keydown', onKey);
    return {
        dispose: function () { document.removeEventListener('keydown', onKey); }
    };
}
