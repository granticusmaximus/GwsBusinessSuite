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
| 4 | Apple UI item 2: simpler navigation | apple-ui-streamlining-2026-11-18 | to do |
| 5 | Apple UI item 3: one main action per page | apple-ui-streamlining-2026-11-18 | to do |
| 6 | Apple UI item 5: motion and feedback (bare spinners) | apple-ui-streamlining-2026-11-18 | to do |
| 7 | Apple UI item 7: detail and copy | apple-ui-streamlining-2026-11-18 | to do |
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
