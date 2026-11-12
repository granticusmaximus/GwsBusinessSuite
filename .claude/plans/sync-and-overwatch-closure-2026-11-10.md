# Synced work and Overwatch closure (10-11-26)

## Source of truth

This plan is based on the locally synced checkout at `f251d028`; no GitHub fetch or remote write
is part of this work. Preserve the existing edit to `OverwatchPinRenderingTests.cs`.

## Already implemented in the synced code

- Mac Catalyst's WKWebView installs a document-start shader compatibility shim. The latest
  commits also sanitize confirmed `sampler3D` precision and `#line 0` directives, reconfigure on
  page appearance, and reload Overwatch once from origin.
- The web globe has a shader-error fallback. Its “3D globe unavailable on this GPU/browser” text
  is generic; the underlying native WebKit/Metal shader error is written to
  `gws-renderer-diagnostics.log` under the Mac app's data directory.
- Camera, incident, and World News clusters depth-test their billboards and labels, suppress the
  default point primitive, and prefer actionable picks. A real-browser overlap test exists.
- The World News timeline implementation and documentation are recorded complete in its plan.

## Local actions

- [x] Add `@AGENTS.md` to `CLAUDE.md`, as required by the repo's shared rulebook and handoff.
- [x] Correct the stale unchecked implementation boxes in
  `overwatch-pin-integrity-2026-11-10.md` based on current source and regression tests.
- [x] Focused source-contract tests passed (2/2). The browser overlap test is present but could
  not run: Chromium is absent and Playwright's download failed with `UNABLE_TO_GET_ISSUER_CERT_LOCALLY`.
- [x] Attempt the default `./scripts/verify-release.sh` without installing tooling. Restore and
  Release build fail because the MAUI Android/iOS/Mac Catalyst/Windows workloads are absent; the
  full test stage remained idle beyond seven minutes and was stopped.
- [x] Run `./scripts/verify-release.sh --server-only --skip-tests`: restore, dependency audit,
  and Release build passed. Docker Compose rendering failed because Docker is unavailable, and
  responsive lint failed because Python resolves to the Microsoft Store alias.
- [x] Complete the full release gate on a supported environment with its required workloads,
  browser, Docker Compose, and Python available. No tools were installed here.
  (12-11-26: full default gate PASS on the Mac — restore, audit, build, full suite incl. Playwright, Docker Compose, responsive lint, whitespace; PARTIAL only for deployed-site checks.)

## Mac Overwatch failure investigation

- The only matching visible statement is Cesium's generic `3D globe unavailable on this
  GPU/browser` shader-failure fallback, not an explicit platform prohibition. The precise root
  shader error cannot be determined from source alone.
- On the affected Mac, inspect `gws-renderer-diagnostics.log`. Confirm it contains
  `Native WKWebView compatibility script active.` and then the failure plus last fragment shader;
  if available, `Removed Cesium float-texture declarations and zero-line directives before native
  shader compilation.` confirms the shim actually intercepted shaders.
- Authenticated Mac Catalyst acceptance and renderer-log review remain required before this item
  can be called fixed. The current Windows environment cannot produce that device evidence.

## Deferred and unavailable

- The ignored `docs/` directory and Overwatch user guide are absent from this checkout.
- Camera provider policy/capability work requires approved sources and terms. The World News
  expansion and native Mac parity are larger unstarted proposals, not part of this closure.
- Deleting the Copilot branch requires GitHub access and explicit remote-write authorization.
- Docker must not be downloaded or installed; verification may report it as an environment
  blocker.