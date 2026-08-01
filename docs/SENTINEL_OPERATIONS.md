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

Production Compose enables an online SQLite backup at startup and every six hours. Each
authenticated `.gwsbackup` archive contains a transactionally consistent `gws-suite.db`, the
matching ASP.NET Core Data Protection key ring, a SHA-256 manifest, and Live Show recordings.
The archive uses AES-256-CBC encryption followed by HMAC-SHA-256 authentication. Archives are
written to the independent `gwssuite-backups` volume and retained for 30 days.

GWS accepts an externally managed Base64 32-byte key through `BACKUP_ENCRYPTION_KEY`. When it
is absent, GWS creates `/app/data/backup-encryption.key` with owner-only permissions. That
generated key is deliberately excluded from the backup archive and **must be escrowed in a
separate password manager or secret store**; losing both the data volume and that key makes
the encrypted backups unrecoverable. Configure this with:

```text
Backups__Enabled=true
Backups__Path=/app/backups
Backups__DataProtectionKeysPath=/app/data/data-protection-keys
Backups__EncryptionKeyPath=/app/data/backup-encryption.key
Backups__EncryptionKey=<Base64 32-byte key from the production secret store>
Backups__LiveShowRecordingsPath=/app/data/live-show-recordings
Backups__IntervalHours=6
Backups__RetentionDays=30
```

The backup volume must also be copied off the droplet by the hosting
provider's snapshot/backup facility. An on-host volume protects against bad deploys and
database corruption; it does not protect against loss of the host.

After the first encrypted rehearsal succeeds and the key has been escrowed, remove legacy
`sentinel-backup-*.zip` files from the backup volume through a reviewed maintenance change;
they are not encrypted. Do not remove them before the new archive and escrowed key have both
been tested.

## Restore rehearsal

Run the automated production-format rehearsal from `/opt/gwssuite`:

```bash
./scripts/rehearse-restore.sh
```

The rehearsal creates a fresh online backup, authenticates and decrypts it only inside the
container's temporary directory, verifies every manifest hash, applies pending migrations to
the isolated copy, runs `PRAGMA integrity_check`, validates the security-audit hash chain,
requires MFA on every active administrator, confirms Sentinel pages remain readable, and
unprotects configured Notion/social/workflow credentials using the restored key ring. It then
removes the plaintext archive and restored directory. Output contains counts and status only,
not credentials or private content.

After that automated rehearsal, complete the manual browser portion against an isolated
container: confirm `/health/ready`, sign in with MFA, open Sentinel, and test one connector
without saving a mutation. Only then may a backup replace production data.

Never restore the database without its matching Data Protection key ring. Doing so preserves
content but makes encrypted Notion credentials unreadable.

## Deployment rollback

The deployment workflow records the previously deployed Git commit and creates plus verifies
an encrypted backup before changing code. If the new release does not become ready, it rebuilds
the previous commit while leaving the forward-migrated data volume untouched. This avoids
silently discarding writes made after migration. If the older application cannot tolerate the
forward-compatible schema, automatic rollback stops and reports that a manual roll-forward is
required; it never restores an older database automatically.

A database restore is a separate, explicitly approved disaster-recovery action. Do not use it
as an ordinary application rollback because it can discard valid post-backup writes.

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
