// Cesium into the browser's cache while the admin portal is idle (30-10-26), so opening Overwatch
// doesn't wait for its 4 MB download. Only for people who can open Overwatch, once a session.
// Overwatch itself stays a full page load: Cesium needs 'unsafe-eval', which only /admin/osint's
// own CSP allows, and an enhanced navigation keeps the previous page's.
//
// Not everywhere (12-11-26): the Mac app draws Overwatch with MapKit and never loads Cesium, and a
// phone shouldn't spend 4 MB of mobile data each session on a page it may not open. A desktop
// browser fetches it at idle, or straight away when the pointer or focus reaches an Overwatch link.
(function () {
    // The same address as OverwatchGrid.razor's <script>, so that load is a cache hit.
    var CesiumScript = 'https://cdn.jsdelivr.net/npm/cesium@1.144.0/Build/Cesium/Cesium.js';

    function wanted() {
        if (window.__gwsMacCatalystWebView === true) return false;
        if (window.matchMedia && window.matchMedia('(max-width: 767.98px)').matches) return false;
        if (navigator.connection && navigator.connection.saveData) return false;
        return true;
    }

    function prefetchCesium() {
        if (window.Cesium || !wanted() || !document.querySelector('a[href="admin/osint"]')) return;
        try {
            if (sessionStorage.getItem('gws-cesium-prefetched')) return;
            sessionStorage.setItem('gws-cesium-prefetched', '1');
        } catch (e) {
            // Without storage it's fetched once per page load, which the browser cache absorbs.
        }
        // Asked for as a <script> asks, so the cached copy serves it.
        fetch(CesiumScript, { mode: 'no-cors', credentials: 'include' }).catch(function () { });
    }

    function onIntent(event) {
        var target = event.target instanceof Element ? event.target.closest('a[href="admin/osint"]') : null;
        if (target) prefetchCesium();
    }

    document.addEventListener('pointerover', onIntent, { passive: true });
    document.addEventListener('focusin', onIntent);
    window.addEventListener('load', function () {
        if (window.requestIdleCallback) window.requestIdleCallback(prefetchCesium, { timeout: 10000 });
        else setTimeout(prefetchCesium, 3000);
    });
})();
