# Sentinel-only Mac window

## Progress

- [x] Inspect the existing Sentinel, Mac window, sign-in and toolbar paths.
- [x] Add a Sentinel-only hosted route and an independent Mac window.
- [x] Cover the new behavior with focused tests and update the user guide.
- [ ] Run the available tests and release gate; record Mac-only checks separately.

The Mac window must reuse the existing authorized Sentinel editor, preserve the
shared session and page-lock handling, and not take over the main window's
toolbar or navigation. A Mac Catalyst build and two-window smoke test require
macOS and cannot be performed on the Windows development machine.
