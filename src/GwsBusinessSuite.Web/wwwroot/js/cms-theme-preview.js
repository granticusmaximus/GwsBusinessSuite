// Read-only theme preview (Appearance > Customize). The preview is browsable: clicking one of the
// site's own links opens that page inside the preview, still wearing the previewed theme, so a
// theme can be judged across the whole site rather than one page at a time. Everything that
// would leave the preview or act on live data stays blocked - external links and form submits.
(function () {
    var match = window.location.pathname.match(/^(\/admin\/api\/cms\/[^/]+\/theme-preview\/[^/]+)/);
    var previewBase = match ? match[1] : null;

    function previewUrlFor(anchor) {
        if (!previewBase) return null;
        var url;
        try {
            url = new URL(anchor.getAttribute('href') || '', window.location.href);
        } catch (e) {
            return null;
        }
        if (url.origin !== window.location.origin) return null;
        // Already a preview URL (e.g. a link this script rewrote earlier).
        if (url.pathname.indexOf(previewBase) === 0) return url.pathname + url.hash;
        var path = url.pathname.replace(/^\/cms\/[^/]+/, '').replace(/^\/+|\/+$/g, '');
        return previewBase + '/' + (path || 'home') + url.hash;
    }

    document.addEventListener('click', function (event) {
        var anchor = event.target.closest('a[href]');
        if (!anchor) return;
        var href = anchor.getAttribute('href') || '';
        // In-page anchors (table of contents, "#section" buttons) keep working as normal.
        if (href.charAt(0) === '#') return;
        event.preventDefault();
        var target = previewUrlFor(anchor);
        if (target) window.location.assign(target);
    }, true);

    document.addEventListener('submit', function (event) { event.preventDefault(); }, true);
})();
