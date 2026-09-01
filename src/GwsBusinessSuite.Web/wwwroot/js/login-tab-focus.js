// Some WKWebView-hosted clients (e.g. the native macOS app) don't move focus on Tab between
// plain form fields the way a full browser does, so wire it explicitly here.
//
// External file, not an inline <script>, for the same reason as theme-init.js: the app's CSP
// (script-src 'self' https://cdn.jsdelivr.net;, no 'unsafe-inline', no nonce) silently blocks a
// literal inline <script> - this one had never actually run in any environment enforcing that
// policy (i.e. always, dev included - the CSP middleware in Program.cs has no dev/prod branch).
(function () {
    var usernameInput = document.getElementById('gws-login-username');
    var passwordInput = document.getElementById('gws-login-password');
    if (usernameInput && passwordInput) {
        usernameInput.addEventListener('keydown', function (e) {
            if (e.key === 'Tab' && !e.shiftKey) {
                e.preventDefault();
                passwordInput.focus();
            }
        });
    }
})();
