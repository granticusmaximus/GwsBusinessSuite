# Overwatch native shader recovery

## Goal

Prove that Overwatch degrades safely when Cesium cannot compile a fragment shader, while the
browser, installed PWA, and Mac Catalyst client all load the production app origin and cannot
retain the affected Cesium release in a WebView cache.

## Progress

- [x] Audited the handoff against the current repository and recent commits.
- [x] Confirmed `gws-pooldp` is a DigitalOcean droplet name, not a resolvable HTTPS application
      origin; the architecture handoff identifies `https://admin.gwsapp.net` as the web droplet.
- [ ] Restore the clients' safe production default while retaining explicit environment overrides.
- [ ] Handle Cesium `Scene.renderError` shader failures, not only synchronous Viewer construction
      failures and uncaught window errors.
- [ ] Add focused regression coverage for the endpoint default and asynchronous render failure.
- [ ] Run focused Overwatch/PWA tests and build/run the Mac Catalyst app with the matching Xcode
      environment available on this Mac.
- [ ] Run `./scripts/verify-release.sh` and record the result.

## Acceptance evidence

Pending.
