// When Blazor's live connection can't even start (the browser can't reach /_blazor/negotiate -
// typically a workplace web filter such as Zscaler, a proxy, or an extension blocking it),
// interactive pages would otherwise stay blank and every button would silently do nothing.
// Show a plain explanation instead. A connection that drops later is handled separately by
// ReconnectModal; this only covers the initial start failing.
(function () {
    var shown = false;
    var startFailure = /negotiat|Failed to start the connection|Circuit host not initialized/i;

    function showBanner() {
        if (shown) return;
        shown = true;
        var banner = document.createElement('div');
        banner.className = 'gws-connection-banner';
        banner.setAttribute('role', 'alert');
        banner.innerHTML =
            '<strong>This page can’t connect to the server.</strong> ' +
            'Buttons and editors won’t respond. Your network may be blocking this site’s live ' +
            'connection - a workplace web filter (for example Zscaler), a proxy, VPN or browser extension. ' +
            'Try a different network, or ask your IT team to allow this site.' +
            '<button type="button" class="gws-connection-banner-retry">Retry</button>';
        banner.querySelector('button').addEventListener('click', function () { window.location.reload(); });
        (document.body || document.documentElement).appendChild(banner);
    }

    window.addEventListener('unhandledrejection', function (event) {
        var reason = event.reason;
        var message = reason && (reason.message || String(reason));
        if (message && startFailure.test(message)) showBanner();
    });
})();
