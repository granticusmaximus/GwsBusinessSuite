# Five business features (19-11-26)

Grant asked for features 6-10 of Claude's second list, split into two phases and built.

## Phase 1 - protect and follow through

1. **Backup restore drills (7).** The full restore check already existed (`VerifyBackupAsync`:
   decrypt, manifest, migrations, migration rehearsal, integrity, audit chain, secrets, admins,
   media) but only ran from the command line. Now `DatabaseBackupBackgroundService` runs it on the
   backup it just made, at most weekly, records the result (`restore-drill.json` in the backups
   folder), alerts on failure, and the backup health check reports a failed or overdue drill.
2. **Loose ends (9).** `/admin/loose-ends`: open deals with no CRM activity in 30 days, sent
   invoices past due, support tickets unanswered for 3 days (or past their first-response time),
   and the latest restore drill. Linked from Home with a count. Not covered: mail awaiting reply
   (mail isn't stored) and domain/certificate expiry (nothing tracks them).
3. **Checks before a page goes live (8).** Publish in the page editor first reviews the rendered
   page: images without a description, skipped heading levels, no or several H1s, empty links,
   missing title or meta description. Problems are listed with "Publish anyway" or "Go back".

## Phase 2 - money and content

4. **Cash-flow forecast (6).** `/admin/cash-flow`: the next 90 days of expected money in, week by
   week, from sent invoices (moved by how late that client usually pays) and open deals weighted by
   stage on their expected close date. A "what if a client pays 30 days late" option. Money out
   isn't modelled: the app holds no expense or recurring-cost records.
5. **Articles that need a refresh (10).** On the articles list: published articles whose views over
   the last 30 days fell well below the 30 before, or that are over a year old, with a "Draft an
   update" that asks SentinelGPT for suggested changes (Mac first) for Grant to apply.

## Progress

- [x] Phase 1 (19-11-26): `RestoreDrill` + `DatabaseBackupBackgroundService.RunRestoreDrillAsync`, health
  check Degraded on a failed/overdue drill; `LooseEnds` (Application/Operations) + `LooseEndsService`
  + `/admin/loose-ends` (Home > More with count; Home's extra links moved under More to keep one main
  action); `PrePublishReview` (Application/CmsBuilder) run by the page editor's Publish
  (`PublishClickedAsync`), regions skipped. Tests: `BusinessFeaturesPhase1Tests`.
- [x] Phase 2 (19-11-26): `CashFlowForecast` + `CashFlowService` + `/admin/cash-flow`;
  `ArticleRefresh` + `ArticleRefreshService` (views from `WebAnalyticsEvents` pageviews on /blog/,
  suggestions via `IOllamaService`, Mac first) + `/admin/article-refresh`. Tests:
  `BusinessFeaturesPhase2Tests`.
- [x] Guides: Platform Operations, CRM, Site Building, Content Creation. verify-release run.
- [ ] Grant: look at the four pages and a Publish with the review; the first restore drill runs
  with the next scheduled backup (check Loose ends after it).
