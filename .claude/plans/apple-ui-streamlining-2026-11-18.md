# Apple-style UI streamlining (18-11-26)

Grant asked for the app to feel "like it was developed by the senior UI/UX Apple developer".
Seven items were proposed; this pass does items 1 (Mac app feels native) and 4 (enforce the
design system) in one go.

## Items

1. **Mac app feels native** - done (needs a Mac build to confirm).
2. **Simplify navigation** - done 19-11-26: section headings in every workspace over five links (Content, Relationships, Intelligence, Platform); Relationships regrouped (Clients, Billing & time, Team). Nothing removed; hiding apps stays in Settings (HiddenNavKeys). Guard: `NavMenuSectionTests`.
3. One main action per page (World News 30 buttons, Overwatch 137) - to do.
4. **Enforce the design system** - done.
5. **Motion and feedback** - done 19-11-26: `InlineProgress` replaces all 20 Bootstrap spinners.
6. Neumorphism pushback - Grant's call, not started.
7. **Detail and copy** - done 19-11-26: real ellipsis in visible text (58 in 41 files, guarded), the one display date not in dd-MM-yy fixed, a screen-reader label reworded.

## What changed

- `app.css` `:root`: `--gws-radius-xs/sm/md/lg/xl/pill`, `--gws-type-xs`..`5xl`, and the font
  stack now starts with the system font (SF Pro on Apple devices, Inter elsewhere).
- Every literal `font-size` (rem/px) and `border-radius` length in `app.css` and the scoped
  `*.razor.css` files maps to those tokens (88 files). Sizes shift by up to ~1-2px.
- 25 references to tokens that don't exist on admin pages (`--neu-border`, `--gws-accent`,
  `--gws-text-muted`, ...) repointed to real ones; they were silently dropping declarations.
- Mac app: toolbar Sidebar (leading) and Search (trailing) buttons; View > Toggle Sidebar and
  Search...; the injected
  script sets `__gwsMacNativeChrome`.
- Web: `theme-init.js`/`app.js` set `data-gws-mac` (`native` or `app`). In the app the bar's brand
  hides; the page's sidebar toggle and search stay (hiding them removed the only way to collapse
  the sidebar when the toolbar button was missing); the status strip hides
  until an item is warning or danger. `app.js` listens for `gws-toggle-sidebar` and `gws-open-search`.
- `DesignSystemGuardTests`: type scale, radius scale, tokens that exist, a shadow ratchet (185)
  and the Mac toolbar wiring.

## Progress

- [x] Tokens and consolidation
- [x] Undefined token references fixed
- [x] Mac toolbar, menu and page marker
- [x] Guard tests
- [x] Sentinel user guide (Mac app section)
- [x] `./scripts/verify-release.sh` and a Mac build. 19-11-26 (Claude, Mac): verify-release.sh PARTIAL, release build and full suite (incl. Playwright) pass; Mac Debug build 0 warnings.
- [ ] Items 2, 3, 5, 6, 7
