// The Overwatch boot screen over the content while an admin page loads (30-10-26). An enhanced
// navigation shows nothing until the server has rendered the new page, so a slow one (World News
// was) looked like the click hadn't landed. It appears only if the page takes longer than DelayMs,
// and goes when Blazor swaps the page in (enhancedload) or the browser shows a new document.
(function () {
    var DelayMs = 150;
    // A navigation that never lands (an error page, a file) doesn't leave it up for good.
    var GiveUpMs = 15000;
    // .gws-boot's opacity transition.
    var FadeMs = 600;
    var showTimer = null;
    var giveUpTimer = null;
    var overlay = null;
    var current = location.pathname + location.search;

    function isAdminPage(url) {
        return url.origin === location.origin
            && url.pathname.indexOf('/admin') === 0
            && url.pathname.indexOf('/admin/api/') !== 0
            && !/\.(csv|pdf|zip|json|ics|xml|txt)$/i.test(url.pathname);
    }

    function show() {
        showTimer = null;
        var content = document.querySelector('main.gws-content');
        if (!content) return;
        var box = content.getBoundingClientRect();
        var top = Math.max(box.top, 0);
        overlay = document.createElement('div');
        overlay.className = 'gws-boot gws-boot-overlay';
        overlay.setAttribute('role', 'status');
        overlay.setAttribute('aria-live', 'polite');
        overlay.style.top = top + 'px';
        overlay.style.left = box.left + 'px';
        overlay.style.width = box.width + 'px';
        overlay.style.height = Math.max(0, Math.min(box.bottom, window.innerHeight) - top) + 'px';
        overlay.innerHTML =
            '<div class="gws-boot-line" aria-hidden="true">INITIALIZING TACTICAL UPLINK...</div>' +
            '<div class="gws-boot-bar" aria-hidden="true"><div class="gws-boot-bar-fill"></div></div>' +
            '<div class="gws-boot-line" aria-hidden="true">STANDBY</div>' +
            '<span class="visually-hidden">Loading the page</span>';
        document.body.appendChild(overlay);
    }

    function hide() {
        clearTimeout(showTimer);
        clearTimeout(giveUpTimer);
        showTimer = giveUpTimer = null;
        current = location.pathname + location.search;
        var leaving = overlay;
        overlay = null;
        if (!leaving) return;
        leaving.classList.add('gws-boot-hidden');
        setTimeout(function () { leaving.remove(); }, FadeMs);
    }

    function begin() {
        hide();
        showTimer = setTimeout(show, DelayMs);
        giveUpTimer = setTimeout(hide, GiveUpMs);
    }

    // Decided once the click has been handled: Blazor's enhanced navigation pushes the new address
    // in its click handler, a full page load leaves the click alone, and a click a page took over
    // (prevented, address unchanged) isn't a navigation at all.
    document.addEventListener('click', function (event) {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        var link = event.target instanceof Element ? event.target.closest('a[href]') : null;
        if (!link || link.hasAttribute('download') || (link.target && link.target !== '_self')) return;
        var url = new URL(link.href, location.href);
        if (!isAdminPage(url) || url.pathname + url.search === current) return;
        var before = location.href;
        setTimeout(function () {
            if (location.href !== before || !event.defaultPrevented) begin();
        }, 0);
    }, true);

    // Back and forward between pages (not a #section on the same one).
    window.addEventListener('popstate', function () {
        if (location.pathname + location.search !== current && isAdminPage(new URL(location.href))) begin();
    });

    // Coming back to a page the browser kept (back button after a full load).
    window.addEventListener('pageshow', hide);

    function bindEnhancedLoad() {
        if (!window.Blazor || typeof window.Blazor.addEventListener !== 'function') return false;
        window.Blazor.addEventListener('enhancedload', hide);
        return true;
    }
    if (!bindEnhancedLoad()) document.addEventListener('DOMContentLoaded', bindEnhancedLoad);
})();
