# World News design pass (12-11-26)

Grant asked for World News to be easier to find things in, finished in its details, and to feel
"designed by Steve Jobs" without destroying the overall UI, on the web and in the Mac app (the
same page in a WebView). Approved plan: three steps, each shippable on its own.

Before screenshots (local copy of the 09-11 store, times shifted): header text ran three lines,
"Read now" was the primary button, rows were a run of counts, the phone's first story sat below
the middle of the screen, and the page's own panels ignored the `--gws-neu-*` surfaces.

## Step 1: polish

- [x] Header: one-line description, "Updated 14:05" meta, refresh as a quiet icon button.
- [x] Rows: kind tag, place, headline, "Seen by N outlets in M languages", time (HH:mm today,
  dd-MM-yy HH:mm before), coverage bar. Rows are links (right-click Copy link).
- [x] Neumorphic surfaces for list well, rows, detail; 8px spacing; tabular figures.
- [x] Wide windows auto-select the top item; state (tab, filters, selection) in the URL.
- [x] Keyboard: up/down move, Return opens, Esc back, `/` search.

## Step 2: findability

- [x] Tabs Today / Developing / Latest (old `events`/`situations`/`world` keys and
  `?situation=`/`?event=` links still work). Reports is the archive the search box opens.
- [x] Kind chips on every tab; Where (country the headlines place it in); Filters for source
  country, language, outlet with active chips and Clear all.
- [x] Surge alerts moved out of the toolbar into Developing.

## Step 3: Today and the Mac

- [x] Today opens with the lead story (headline, summary, coverage, Read the story).
- [x] Mail-style independent scrolling at 900px+; sticky language headings.
- [x] Mac app: Cmd+F search, Cmd+R refresh, Cmd+[ back; Share (share sheet or copy link).
- [x] Reduced motion respected.

## Progress

All three steps built 12-11-26.

- Logic in `WorldNewsBrowse` (Infrastructure): tabs and old-key parsing, URL, ranges, row facts
  (kind via `SituationKinds.FromWords`, place via `WorldNewsMapService.Locate`), time label, reach,
  filters, one-word chip labels. Tested in `WorldNewsBrowseTests`.
- `js/world-news.js`: `isWide` (gws-content content box), `setUrl` (replaceState), `share`,
  `attach` (keys; Mac-only Cmd keys). Tested in `WorldNewsScriptBrowserTests`.
- Page: rows are `<a href>` with `@onclick:preventDefault` (right-click Copy link works; Cmd-click
  doesn't open a tab, since a server round trip loses the user gesture). Wide windows auto-select
  the top item after first render. Surge alerts in the Developing list head. Today resets to 3 h.
- Verified on a local copy of the 09-11 store (timestamps shifted, three test situations linked):
  25/25 scripted checks across wide Chromium, phone Chromium and WebKit with the Mac flag
  (auto-select, arrows, /, Cmd+F, chip in URL, reload keeps state, archive search, Today lead to
  story, Share copies link, old `?situation=` link, phone list/story/back, no sideways scroll,
  no console errors).
- Fixed on screenshots: page-wide tabular figures spaced out hyphens (now only pure numbers);
  long kind labels pushed Where to its own line (one-word chips, full label on the story).
- Not checked on screen: light theme (the account's saved theme is dark; the page uses only
  theme tokens). User guide: `docs/INTELLIGENCE_USER_GUIDE.md` "Finding your way".
