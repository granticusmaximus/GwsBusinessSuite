// Runs before any stylesheet so the correct theme attribute is already on <html> for the very
// first paint - setting it any later (e.g. from app.js after DOMContentLoaded) would flash the
// default dark theme first for anyone who chose light. Absence of the attribute (never chosen,
// or storage unavailable) keeps the app's unconditional dark look exactly as it was before this
// feature existed - dark stays the default, light is opt-in.
//
// This has to be an external file, not an inline <script> in App.razor's <head>: the app's CSP
// (Program.cs) is script-src 'self' https://cdn.jsdelivr.net; with no 'unsafe-inline' and no
// nonce mechanism, so a literal inline <script> here is silently blocked by the browser and
// never runs at all - confirmed live (a fresh page load, or any real page.goto()/reload, always
// reverted to the dark default even with a stored "light" preference; only the SPA-style
// enhanced navigation between clicks inside one already-loaded session preserved it, since that
// never tears down <html> at all). A plain <script src="..."> with no defer/async still blocks
// parsing and runs before first paint exactly like the inline version would have, and is allowed
// by 'self'.
(function () {
    try {
        var stored = localStorage.getItem('gwsTheme');
        if (stored === 'light' || stored === 'dark') document.documentElement.setAttribute('data-theme', stored);
    } catch (e) { /* storage unavailable (private mode, etc.) - fall back to default dark */ }
})();
