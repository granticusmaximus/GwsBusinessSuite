# Overwatch macOS renderer recovery

## Goal

Restore Overwatch in the Mac Catalyst app's WKWebView without changing the working browser/PWA rendering path or routing the app to the incomplete native-renderer placeholder.

## Confirmed failure

- Browser/PWA Overwatch works with Cesium 1.144.0.
- The native renderer diagnostics captured WKWebView rejecting a Cesium WebGL 2 fragment shader containing `#define OES_texture_float`.
- Disabling float extensions in JavaScript did not help because WebGL 2 exposes the capability as core functionality and Cesium still generated the define.

## Implementation

- [x] Install a Mac-Catalyst-only `shaderSource` compatibility shim at WKWebView document start, before Cesium can capture WebGL methods.
- [x] Remove only Cesium's confirmed failing float feature defines from fragment shaders.
- [x] Leave the hosted browser and PWA code path unchanged.
- [x] Add a source-contract test for injection timing and the shader rewrite.
- [x] Run focused Overwatch native/browser tests.
- [ ] Rebuild and launch the Mac Catalyst app.
- [ ] Complete authenticated native acceptance and confirm no new shader compile failure in the native diagnostics log.
- [ ] Run `./scripts/verify-release.sh` after the native acceptance result is known.

## Acceptance boundary

A successful build and source tests do not prove the WKWebView renderer works. Completion requires loading Overwatch in the rebuilt, authenticated Mac app and checking both the rendered globe and the native renderer diagnostics log.

## 10-11-26 failure investigation

The visible `3D globe unavailable on this GPU/browser` message is the generic JavaScript fallback
for a shader compile failure, not a platform block. The current source clears WebView caches,
installs the Catalyst shader shim at document start, and retries Overwatch once from origin. The
latest synced commits still require a rebuild and authenticated Mac acceptance. Inspect
`gws-renderer-diagnostics.log` under the app's `FileSystem.Current.AppDataDirectory` to determine
whether the shim ran and which fragment shader WebKit rejected; source-only inspection cannot
identify that device-specific error.
