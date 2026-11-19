# World News: freshness, grouping, tags and Overwatch icons (18-11-26)

Grant asked for fresher World News, better grouping of stories that report the same thing, better
tags, the most fitting available icon per story, and the same on Overwatch's World News layer.

## Design

1. **Freshness.** The bottleneck was the fixed 15-minute read loop, not the feeds: outlet feeds
   already schedule themselves (`FeedSchedule.ShouldRead`, ETag/304, quiet back-off, refusal pause).
   - `WorldNewsService.ReadInterval`: `WorldNews:ReadIntervalMinutes` (default 5, 2-60).
   - Google News editions and configured GDELT queries have no cheap validators, so they're held
     to `WorldNewsOptions.SlowSourceGap` (`WorldNews:SlowSourceMinutes`, default 15, 0-120) via
     `FeedSchedule.IsDueAfter` and the store's last-fetched keys (`google-news:<url>`,
     `gdelt:<query>`). Surge follow-up searches are exempt.
   - Each read's other work is self-limited already (RDAP 10 per read, ownership and summaries throttled).
2. **Grouping.** `GroupingRules` (WorldNewsKit) applied in `EventClusterer.Assign`/`MergeTwins`:
   - Place guard: `HeadlineCountries` (English names, short forms and adjectives) and no shared
     country means no join or merge.
   - Quiet penalty: the join threshold rises linearly by up to `QuietPenalty` once an event has been
     quiet for 12 hours, until the window's end.
   - Config: `WorldNews:Grouping:Enabled|PlaceGuard|QuietPenalty`. The similarity thresholds are
     unchanged (they were tuned on headlines).
3. **Tags.** `SituationSubtypes.Tag(kind, texts...)` returns a `SituationTag`: subtype, label,
   Bootstrap icon (1.11.3, as loaded in `App.razor`) and confidence. Keyword-based, scoped to the
   situation's kind, computed when the map projects a situation, so no schema change is needed.
4. **Overwatch.**
   - `WorldNewsMapSignal.Tag`, and the `icon`/`subtype`/`tagLabel`/`tagConfidence` payload fields.
   - `tactical-globe.js` draws icon pins: the white-disc pin with the glyph cut in near-black, so the
     purple tint still applies. It falls back to the plain pin until the icon font loads, then redraws.
   - The panel shows the subtype.
   - `OverwatchGrid` subscribes to `WorldNewsService.Changed` and redraws 2s after a read finishes.
     It skips time-machine views and mid-read events.

## Progress

- [x] Freshness: read interval, slow-source gap (tests: `IsDueAfter`, and the pipeline reads
      editions once per gap).
- [x] Grouping rules: place guard and quiet penalty (tests: `HeadlineCountries`, `Conflicts`,
      `Required`, `Assign` with and without rules, `MergeTwins` place guard).
- [x] Subtype tags and icons (tests: subtypes per kind, fallback, and that only icons in the
      loaded set are used).
- [x] Overwatch icon pins, the subtype in the panel, and live redraws after a read.
- [x] `docs/WORLD_NEWS_USER_GUIDE.md`: the "Freshness, Grouping and Story Tags" section.
- [x] Verified on Windows (18-11-26):
  - `dotnet build src\GwsBusinessSuite.Web` passes with 0 warnings.
  - The World News and situation unit tests pass, including the new ones. The remaining failures
    there are the same on clean `HEAD`: CRLF prompts, SQLite file locks and the Chromium download.
  - The Overwatch and World News browser tests (63) pass through Edge.
- [x] `./scripts/verify-release.sh` on the Mac. 19-11-26 (Claude, Mac): verify-release.sh PARTIAL, release build and full suite (incl. Playwright) pass; Mac Debug build 0 warnings. A stale editor save had removed the icon pins and panel subtype from the working tree's `tactical-globe.js`; `OverwatchPinRenderingTests` now guards both.
- [ ] Watch production after deploy:
  - how long a read takes against the 5-minute interval;
  - a Google News 429 or refusal;
  - whether the place guard splits a story that should have stayed together (raise it in
    `/admin/logs` or turn it off with `WorldNews:Grouping:PlaceGuard=false`).

## Deferred

- [x] 19-11-26: an hourly re-check of the last 48 hours splits events already merged wrongly
  (`RecentMergeCheck`: the story page's split suggestions applied automatically, up to 25 an hour;
  stories a person corrected are skipped; off with the place guard). Test in `WorldNewsKitTests`.
- [x] 19-11-26: copied wire stories count once when sizing pins. `WorldNewsMapSignal.IndependentCount`
  (`SourceIndependence` across the situation's events, cached with its details) sizes the pin, and
  the panel adds "N INDEPENDENT" when that's fewer than the outlets.
- [x] 19-11-26: publication delay per source. `WorldArticle.FetchedAt` (set by outlet feeds only when
  the feed dated the report), `articles.fetched_at`, `WorldNewsStore.PublicationDelays` (median and
  90th percentile, 7 days, 5+ reports); a table on Sources.
- [x] 19-11-26: SentinelGPT tags stories the keywords left untagged (`SubtypeRefinement`): up to 5
  per read, Mac first, from the kind's own subtype list or none, saved per story
  (`situation_subtypes`); the keyword tag still wins; the panel says "(TAGGED BY SENTINELGPT)".
- [x] 19-11-26: subtype filter on the Overwatch World News layer (`WorldNewsMapService.BySubtype`):
  ALL TYPES plus the types in view with counts.
