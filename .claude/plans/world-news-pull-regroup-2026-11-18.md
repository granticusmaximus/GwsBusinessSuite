# World News: Pull latest, Regroup and smart grouping (18-11-26)

Grant asked for a manual "pull the crawler now" and a "regroup" feature, made as smart as possible,
in one pass rather than phases.

## Design

- **Pull latest** (`WorldNewsService.StartPull`): plans targeted searches (single-outlet stories
  from the last day, watches, beats) with `PullPlanner`, asks the crawler to search now
  (`WebCrawler.DiscoverNowAsync`), forces a read (`ReadRequest(Force, Queries)`, skips the
  slow-source gap but never a refusal or back-off), regroups, and reports a `PullSummary`.
  A follow-up crawl import runs ~90 s later when pages were queued. Cooldown
  `WorldNews:ManualPullCooldownMinutes` (default 5, 1-60).
- **Regroup** (`StartRegroup`): every-pair comparison over recent events (`EventClusterer.MergeTwins`)
  with remembered keep-apart and AI-confirmed pairs. Close calls (within 0.05 under the bar) go to
  `GroupingAdjudicator` via `IOllamaService` inside `MacRelayScope.ForSharedWork()` (public data),
  at most `WorldNews:Regroup:AiChecks` (default 6, 0-30) per run. Verdicts are stored.
- **Evidence bonus** (`GroupingRules.EvidenceBonus`, 0.03): a shared distinctive number merges just
  under the threshold unless the headlines name different countries.
- **Human corrections** (`WorldNewsStore.Grouping.cs`): split, move and merge, saved as pair
  rules so a regroup respects them; every grouping change is logged with actor and reason.
- **Suggested splits** (`SplitSuggestions`) from headline embeddings; **per-story pull**
  (2-minute cooldown per story).
- UI: header **Regroup** and **Pull latest** buttons on `WorldNews.razor`; `WorldNewsGrouping`
  component on the story view.

## Progress

- [x] Kit: `FeedSchedule.ShouldReadForced`, `GroupingRules`, `EventClusterer`, `WorldNewsPipeline`,
      `WorldNewsStore.Grouping.cs`, `SmartGrouping.cs`
- [x] Crawler `DiscoverNowAsync`
- [x] `WorldNewsService.Pull.cs`, config and dispose
- [x] UI: header buttons, pull summary line, `WorldNewsGrouping` on the story view
- [x] Tests in `WorldNewsKitTests` (12 new, all pass on Windows)
- [x] `docs/WORLD_NEWS_USER_GUIDE.md` (Pull Latest and Regroup section)
- [x] `./scripts/verify-release.sh` on the Mac. 19-11-26 (Claude, Mac): verify-release.sh PARTIAL, release build and full suite (incl. Playwright) pass; Mac Debug build 0 warnings.

## Handoff notes

- Built on Windows: Infrastructure and Web build with 0 warnings. World News and crawler tests:
  24 failures, the same count as clean `HEAD` on Windows (CRLF, SQLite locks, Chromium).
- The Pull latest button re-enables after its cooldown on the next page re-render (each read
  triggers one, about every 5 minutes), not on a timer.
- AI close-call checks add model load per regroup; set `WorldNews:Regroup:AiChecks` to 0 to turn
  them off.
