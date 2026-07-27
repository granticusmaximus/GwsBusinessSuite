# Sentinel operations

## Production readiness contract

Sentinel is ready when all of the following are true:

- `dotnet build GwsBusinessSuite.slnx -c Release` completes with zero warnings.
- the full test suite, including Playwright editor journeys, passes;
- `dotnet list GwsBusinessSuite.slnx package --vulnerable --include-transitive`
  reports no vulnerable packages;
- the latest EF Core migration applies to an empty database and an upgraded database;
- `/health/live` returns 200 while the process can serve requests;
- `/health/ready` returns 200 only when SQLite, Ollama, and scheduled backups are healthy;
- a backup can be restored into a standalone SQLite file and contains the Data Protection
  key ring needed to decrypt connector credentials.

`/health` remains as a compatibility endpoint and runs the complete health-check set.

## Automated backups

Production Compose enables an online SQLite backup at startup and every six hours. Each ZIP
contains a transactionally consistent `gws-suite.db` plus the ASP.NET Core Data Protection
keys. Archives are written to the independent `gwssuite-backups` volume and retained for
30 days. Configure this with:

```text
Backups__Enabled=true
Backups__Path=/app/backups
Backups__DataProtectionKeysPath=/app/data/data-protection-keys
Backups__IntervalHours=6
Backups__RetentionDays=30
```

The application data and backup volumes must also be copied off the droplet by the hosting
provider's snapshot/backup facility. An on-host volume protects against bad deploys and
database corruption; it does not protect against loss of the host.

## Restore rehearsal

1. Stop writes to the app and copy the chosen archive out of the backup volume.
2. Extract the archive into a temporary directory.
3. Run `PRAGMA integrity_check;` against the extracted `gws-suite.db`; the result must be `ok`.
4. Start the same application version with `ConnectionStrings__DefaultConnection` pointing
   to the extracted database and `Backups__DataProtectionKeysPath` pointing to the extracted
   key directory.
5. Confirm `/health/ready`, sign in, open Sentinel, and validate one encrypted Notion
   connection without saving it.
6. Only after the rehearsal succeeds, replace the production database and key directory,
   then start the production stack.

Never restore the database without its matching Data Protection key ring. Doing so preserves
content but makes encrypted Notion credentials unreadable.

## Notion connection webhook

Open Sentinel's Notion connection panel, copy the HTTPS webhook URL into the connection's
Webhooks tab in Notion, and subscribe to page, data-source, view, and comment events. Notion
sends a one-time verification token; refresh the panel and paste that token into Notion.
Subsequent requests are authenticated with HMAC-SHA256 over the exact request body, deduplicated
by event ID, and dispatch a server-owned incremental sync.

Verification is intentionally one-time so an anonymous request cannot replace an established
signing secret. Use **Replace webhook subscription** in Sentinel before deleting and recreating
the subscription in Notion.

## Incident checks

- Process unavailable: check `/health/live`, then container logs.
- Ready endpoint fails on `database`: validate the mounted data volume and migration logs.
- Ready endpoint fails on `ollama`: check the Ollama container and `ollama list`.
- Ready endpoint fails on `backups`: inspect `/app/backups`, permissions, free disk, and the
  latest scheduled-backup error.
- Notion changes are delayed: inspect the webhook status and sync job in the connector panel.
  Repeated event delivery is safe because event IDs are durable and idempotent.
- Concurrent Notion and Sentinel edits: resolve each field in **Changes requiring review**;
  Sentinel does not silently select a winner.
