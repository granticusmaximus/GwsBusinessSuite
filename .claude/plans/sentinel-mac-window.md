# Sentinel-only Mac window

## Progress

- [x] Inspect the existing Sentinel, Mac window, sign-in and toolbar paths.
- [x] Add a Sentinel-only hosted route and an independent Mac window.
- [x] Cover the new behavior with focused tests and update the user guide.
- [x] 18-11-26: reported blank window and missing menu item. The window now starts its own load
  (`MainPage.StartSentinelWindow`, from `Window.Created`/`Activated`) instead of relying on
  `OnAppearing`, and logs to `gws-mainpage-debug.log`. The missing menu item means the Mac's
  checkout was behind `origin/main`: pull, then `open gws`.
- [x] 18-11-26 (`f690e8e7`): dashboard button and right-click "Open Sentinel in New App Window"
  on the Knowledge rail button, Sentinel nav links and pinned Sentinel pages
  (`wwwroot/js/sentinel-window.js`). In the Mac app it navigates to
  `gws-native://sentinel-window?page=…`, which `MainPage.HandleNativeRequestAsync` turns into
  `App.OpenSentinelWindow(query)`. Browsers get a popup. `SentinelWindowNavigation.WindowUrl`
  keeps only GUID `page`/`database`/`row`. Unit and Playwright tests pass on Windows.
- [x] 19-11-26 review fixes (Claude, on the Mac):
  - Multiple scenes made UIKit add File > New Window (Cmd+N), which opened a second AppShell
    window sharing the static `MacToolbarActions`. Removed in `AppDelegate.BuildMenu`
    (`NewItem` on macOS 26+, `NewScene` before).
  - A relaunch restored a Sentinel scene as a second main window. Sentinel windows now save
    `gws.window=sentinel` (and their query) in `Backgrounding`; `App.CreateWindow` builds a
    Sentinel window for a restored Sentinel scene, or for any scene made while a main window is
    open (`SentinelWindowNavigation.IsSentinelScene`, unit-tested).
  - `StartSentinelWindow` now subscribes to connectivity (offline notice), and
    `StopSentinelWindow` (on `Destroying`) drops it and the active-window pointer.
  - The Sentinel user guide had no section on the window (`docs/` doesn't sync from Windows):
    "Sentinel in its own window" added.
  - Mac Debug build 0 warnings; `verify-release.sh` PARTIAL with the full suite passing.
- [ ] Mac smoke test: menu item, dashboard button and rail right-click each open the window
  (confirm WKWebView raises `Navigating` for the scripted `gws-native://` navigation), window loads Sentinel, main window unaffected. Send the
  `Sentinel window:` lines from `gws-mainpage-debug.log` if it is still blank.
- [x] Tests and release gate. 19-11-26 (Claude, Mac): `verify-release.sh` PARTIAL, release build and full suite incl. Playwright pass; Mac Debug build 0 warnings. Mac-only check: the smoke test above.

The Mac window must reuse the existing authorized Sentinel editor, preserve the
shared session and page-lock handling, and not take over the main window's
toolbar or navigation. A Mac Catalyst build and two-window smoke test require
macOS and cannot be performed on the Windows development machine.
