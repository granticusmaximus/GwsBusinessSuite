# Ten-item phase (19-11-26)

Grant: "Do all 10 items in one phase." The ten units listed on 19-11-26 after the World News
outage work. Each item lands shippable on its own (Grant pushes mid-phase); the release gate runs
after each code item.

| # | Item | Source plan | State |
|---|---|---|---|
| 8 | Overwatch pin overlap browser test + guide | overwatch-pin-integrity-2026-11-10 | done |
| 9 | World News duplicate/noise rates; discovery counts live | world-news-situations-macos-2026-10, world-news-active-scraping-2026-11 | done |
| 10 | Tick release-gate boxes covered by today's green runs | several | done |
| 3 | WMO severe-weather alerts on Overwatch | overwatch-world-signals-2026-11-05 | done |
| 1 | World News expansion Unit 1: regional outlets, adaptive outlet threshold | world-news-overwatch-expansion-2026-11 | done |
| 4 | Apple UI item 2: simpler navigation | apple-ui-streamlining-2026-11-18 | done |
| 5 | Apple UI item 3: one main action per page | apple-ui-streamlining-2026-11-18 | to do |
| 6 | Apple UI item 5: motion and feedback (bare spinners) | apple-ui-streamlining-2026-11-18 | done |
| 7 | Apple UI item 7: detail and copy | apple-ui-streamlining-2026-11-18 | done |
| 2 | Camera availability phases 1-5 | overwatch-camera-availability-and-world-news-2026-11-09 | to do |

Not in this phase: the AI droplet capacity follow-up (generations still time out while embedding
runs; see ai-droplet-availability-2026-11-19), and the six items that need Grant.

## Progress

- [x] 8: `OverlappingDecorativeMarker_ShouldNotPreventTheActionablePinFromOpening` runs and passes in
      Chromium on the Mac; the Overwatch guide already has the 10-11-26 marker repair note. Pin plan closed.
- [x] 9: measured in production (see world-news-situations-macos-2026-10): duplicates 1.6-2.9%,
      crawled pages noisier (37% single-report stories vs 19%), grouping is the bottleneck (65%
      ungrouped). Discovery: 0 failures since the SearXNG fix, ~1 new page per run.
- [x] 10: gate-only boxes ticked in 5 plans; interactive checks needing a signed-in session kept open.
- [x] 3: WORLD WARNINGS layer (`WmoWarningService`): WMO SWIC index only (its alert files answer 403,
      respected), one pin per country at its worst warning, US left to NWS ALERTS; native contract,
      shapes and layer catalogue updated; `WmoWarningServiceTests`; Overwatch guide.
- [x] 1: 17 regional outlets (tested live), thin-coverage allowance with a LOCAL REPORTING ONLY label,
      tests, Overwatch and Intelligence guides.
- [x] 6: `InlineProgress` (Components/InlineProgress.razor, `.gws-progress` in app.css): one indicator
      that sizes to and takes the colour of its text, breathes instead of turning with Reduce Motion,
      and has an optional screen-reader label. All 20 Bootstrap spinners in 16 files replaced; the Wiki
      search box lost its hard-coded `bg-white`; the public share page shows a labelled indicator (the
      admin boot screen would read oddly to visitors). Guard: no `spinner-border`/`spinner-grow` in any
      .razor file. UI_DESIGN_SYSTEM.md interaction rule added.
- [x] 7: Audit for measurable detail issues. Real ellipsis in visible text (58 "..." in 41 files; the
      Overwatch HUD and boot screen keep their terminal dots), guarded by
      `VisibleText_ShouldUseTheEllipsisCharacter`. The one visible date not in dd-MM-yy (Threat
      Intelligence scan link) now uses `ToLocalGwsDate`; the other 23 hits are machine formats (inputs,
      data attributes, `<time datetime>`, CSV). Street View's screen-reader label no longer says "click
      here". Left alone on purpose: 153 `bg-white`/`bg-light`/`text-dark` classes (app.css remaps them to
      the theme in dark and light, so they render correctly) and "Please" in error messages.
- [x] 4: sidebar section headings (Write/Reach/Audience; Clients/Billing & time/Team; News/Monitoring;
      Operations/Tools/Trust), Support and Email Campaigns moved beside CRM. No link removed or renamed.
      `NavMenuSectionTests`; nav and rail browser tests pass.
- [x] Paused 19-11-26 for Grant's report that SentinelGPT is too slow in World News: fixed (Mac-first routing, Tailscale restarted, hourly Mac-down warning); see ai-droplet-availability-2026-11-19.
- [x] Grant 19-11-26 (screenshot): conflict windows said "4 deaths in 1 event ... coded monthly" and
      groups said "+N more". `ConflictEventService.Describe`: the latest event in words from UCDP's
      side_a/side_b, where_description/adm_1, deaths_civilians, source_headline/source_office; "XXXnnn"
      shown as "Unidentified armed group"; UCDP pins no longer show a time (date-only data read as the
      evening before). GDELT pins name the actors. Mac map clusters get "N conflict reports" plus the
      first names (`NativeShapes.ClusterLabel`); the grouped list shows a summary line per item;
      hazard panels keep line breaks (`.tg-hazard-detail`). Tests in WorldLayerServicesTests.
- [x] Grant 19-11-26: OPEN IN WORLD NEWS on every hazard/conflict window opens a World News briefing
      (`/admin/world-news/brief`, WorldNewsOverwatchBrief): what it is, SentinelGPT's executive summary
      (`WorldNewsService.BriefSummaryAsync`, prompt in `OverwatchBrief`), the report and its source, and
      the matching stories with their latest summaries (`BriefCoverageAsync` over `FindAsync`). Search
      words from `HazardFeature.NewsQuery` (conflict sides + place) or the title. A first version that
      jumped to a story or the search was replaced the same day. Tests: OverwatchBriefTests.