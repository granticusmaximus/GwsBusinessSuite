# Overwatch native shader recovery

## Goal

Prove that Overwatch degrades safely when Cesium cannot compile a fragment shader, while the
browser, installed PWA, and Mac Catalyst client all load the production app origin and cannot
retain the affected Cesium release in a WebView cache.

## Progress

- [x] Audited the handoff against the current repository and recent commits.
- [x] Confirmed `gws-pooldp` is a DigitalOcean droplet name, not a resolvable HTTPS application
      origin; the architecture handoff identifies `https://admin.gwsapp.net` as the web droplet.
- [x] Restore the clients' safe production default while retaining explicit environment overrides.
- [x] Handle Cesium `Scene.renderError` shader failures, not only synchronous Viewer construction
      failures and uncaught window errors.
- [x] Add focused regression coverage for the endpoint default and asynchronous render failure.
- [x] Run focused Overwatch/PWA tests and build/run the Mac Catalyst app with the matching Xcode
      environment available on this Mac.
- [ ] Run `./scripts/verify-release.sh` and record the result.

## Acceptance evidence

- Focused Overwatch, endpoint, and PWA suite: 64 passed, 0 failed.
- Mac Catalyst Debug arm64 build: succeeded with 0 warnings and 0 errors using the repository's
  documented local `ValidateXcodeVersion=false` override.
- Fresh native bundle: loaded the authenticated production dashboard from
  `https://admin.gwsapp.net/admin`, then reproduced the production-only WKWebView shader failure
  on `/admin/osint`. This proved the 1.144 pin alone was insufficient and that Cesium's own
  `showRenderLoopErrors` dialog was the visible crash surface.
- The repaired runtime disables that blocking Cesium dialog and listens to `Scene.renderError`;
  browser coverage proves the failed Viewer is disposed and replaced by the contained fallback.
- Native acceptance of the repaired JavaScript still requires publication to the app droplet;
  the native client intentionally loads hosted web assets rather than bundling this script.
