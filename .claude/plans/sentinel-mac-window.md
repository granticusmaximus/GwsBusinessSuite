# Sentinel-only Mac window

## Progress

- [x] Inspect the existing Sentinel, Mac window, sign-in and toolbar paths.
- [x] Add a Sentinel-only hosted route and an independent Mac window.
- [x] Cover the new behavior with focused tests and update the user guide.
- [x] 18-11-26: reported blank window and missing menu item. The window now starts its own load
  (`MainPage.StartSentinelWindow`, from `Window.Created`/`Activated`) instead of relying on
  `OnAppearing`, and logs to `gws-mainpage-debug.log`. The missing menu item means the Mac's
  checkout was behind `origin/main`: pull, then `open gws`.
- [ ] Mac smoke test: menu item appears, window loads Sentinel, main window unaffected. Send the
  `Sentinel window:` lines from `gws-mainpage-debug.log` if it is still blank.
- [ ] Run the available tests and release gate; record Mac-only checks separately.

The Mac window must reuse the existing authorized Sentinel editor, preserve the
shared session and page-lock handling, and not take over the main window's
toolbar or navigation. A Mac Catalyst build and two-window smoke test require
macOS and cannot be performed on the Windows development machine.
