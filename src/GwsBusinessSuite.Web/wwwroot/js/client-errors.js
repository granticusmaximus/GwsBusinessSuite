// Logging plan, Phase 1 (12-11-26): browser errors on admin pages reach the server's log, with
// the trace id of the request that served the page (the gws-trace meta tag), so one search finds
// the browser's error and the server's lines behind it. The Mac app reports its native failures
// through window.gwsClientLog.report as well. Public pages report nothing: the endpoint needs a
// signed-in user.
(function () {
    'use strict';

    var MaximumPerPage = 10;
    var sent = 0;
    var seen = Object.create(null);

    function signedIn() {
        return !!document.querySelector('.gws-layout');
    }

    function trace() {
        var meta = document.querySelector('meta[name="gws-trace"]');
        return meta ? meta.getAttribute('content') || null : null;
    }

    function report(entry) {
        if (!entry || !entry.message || sent >= MaximumPerPage || !signedIn()) return;
        var key = (entry.origin || 'browser') + '|' + entry.message;
        if (seen[key]) return;
        seen[key] = true;
        sent++;
        var body = JSON.stringify({
            origin: entry.origin || 'browser',
            kind: entry.kind || 'error',
            message: String(entry.message).slice(0, 1000),
            stack: entry.stack ? String(entry.stack).slice(0, 4000) : null,
            page: location.pathname,
            trace: trace()
        });
        try {
            fetch('api/diagnostics/client', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: body,
                credentials: 'same-origin',
                keepalive: true
            }).catch(function () { });
        } catch (e) {
            // Reporting must never cause an error of its own.
        }
    }

    window.addEventListener('error', function (event) {
        // "Script error." is a cross-origin script hiding its details: nothing to act on.
        if (!event.error && (!event.message || event.message === 'Script error.')) return;
        report({
            kind: 'error',
            message: event.message || (event.error && event.error.message),
            stack: (event.error && event.error.stack) || (event.filename ? event.filename + ':' + event.lineno + ':' + event.colno : null)
        });
    });

    window.addEventListener('unhandledrejection', function (event) {
        var reason = event.reason;
        var message = reason && (reason.message || String(reason));
        // connection-check.js explains a connection that can't start; the server can't be told.
        if (!message || /negotiat|Failed to start the connection/i.test(message)) return;
        report({ kind: 'rejection', message: message, stack: reason && reason.stack });
    });

    window.gwsClientLog = { report: report };
})();
