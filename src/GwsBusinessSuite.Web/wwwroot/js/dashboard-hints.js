// External file, not an inline <script>, so the app's CSP (script-src 'self' ...; no
// 'unsafe-inline', no nonce) doesn't silently block it - see theme-init.js/login-tab-focus.js
// for the same fix applied elsewhere this session.
window.gwsDashboardHints = {
    isDismissed: function (key) {
        try {
            return localStorage.getItem(key) === '1';
        } catch {
            return false;
        }
    },
    dismiss: function (key) {
        try {
            localStorage.setItem(key, '1');
        } catch {
            // Storage unavailable (private browsing, disabled cookies) - the hint just
            // reappears next visit, which is a harmless fallback, not an error.
        }
    }
};
