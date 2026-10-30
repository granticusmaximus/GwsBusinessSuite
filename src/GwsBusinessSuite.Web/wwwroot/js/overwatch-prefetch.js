// Cesium into the browser's cache while the admin portal is idle (30-10-26), so opening Overwatch
// doesn't wait for its 4 MB download. Only for people who can open Overwatch, once a session.
// Overwatch itself stays a full page load: Cesium needs 'unsafe-eval', which only /admin/osint's
// own CSP allows, and an enhanced navigation keeps the previous page's.
(function () {
    // The same address as OverwatchGrid.razor's <script>, so that load is a cache hit.
    var CesiumScript = 'https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/Cesium.js';

    function prefetchCesium() {
        if (window.Cesium || !document.querySelector('a[href="admin/osint"]')) return;
        if (navigator.connection && navigator.connection.saveData) return;
        try {
            if (sessionStorage.getItem('gws-cesium-prefetched')) return;
            sessionStorage.setItem('gws-cesium-prefetched', '1');
        } catch (e) {
            // Without storage it's fetched once per page load, which the browser cache absorbs.
        }
        // Asked for as a <script> asks, so the cached copy serves it.
        fetch(CesiumScript, { mode: 'no-cors', credentials: 'include' }).catch(function () { });
    }

    window.addEventListener('load', function () {
        if (window.requestIdleCallback) window.requestIdleCallback(prefetchCesium, { timeout: 10000 });
        else setTimeout(prefetchCesium, 3000);
    });
})();
