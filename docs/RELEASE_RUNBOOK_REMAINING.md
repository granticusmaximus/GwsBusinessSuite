# Remaining steps to close GWS 1.0

`RELEASE_READINESS.md` is the contract; this is the execution checklist for what's left after
the 2026-08-11 pass (new operational alerting/retention/disk-space/CRM-deal test coverage,
refreshed local + deployed health/security-header evidence — see that file's evidence table).
Everything below needs real credentials, a signing identity, production infrastructure access,
or a human sign-off that no amount of further code change can substitute for. Each item names
the exact command or contact point, not just the gap.

## 1. Production rehearsals (droplet access required)

These all run **on the droplet** (`/opt/gwssuite`, per `docs/SENTINEL_OPERATIONS.md`), not from
a local checkout — `docker compose exec` targets whatever stack is running on the machine it's
invoked from.

- **Backup restore rehearsal — automated production portion passed 2026-08-21.** Deployment run
  [32528155116](https://github.com/granticusmaximus/GwsBusinessSuite/actions/runs/32528155116)
  created a fresh encrypted production archive on the droplet and verified it as valid before
  deploying commit `50da93e`. Verification covered the manifest, isolated database migration and
  integrity check, matching Data Protection key ring, active-admin MFA requirement, Sentinel
  readability, audit chain, Live Show recording content, and protected connector credentials.
  The deployed app then returned ready internally and passed the external liveness, readiness,
  login-surface, and public-homepage checks. **Still required:** run the manual isolated-container
  browser portion from `docs/SENTINEL_OPERATIONS.md` (sign in with MFA, open Sentinel, test one
  connector without saving), confirm the backup volume is copied off-host, and record that the
  backup encryption key is escrowed separately. The archive deliberately excludes that key.
- **Deployment rollback rehearsal**: trigger a deploy of a deliberately broken commit (or use
  `.github/workflows/deploy.yml`'s existing rollback path) and confirm the workflow rebuilds the
  previous commit while the data volume stays forward-migrated, per the "Deployment rollback"
  section of `docs/SENTINEL_OPERATIONS.md`.
- **Migration-copy rehearsal**: copy the *production* `gws-suite.db` (from a fresh backup, not
  the live file) into an isolated environment and apply the latest migration to that copy
  specifically, not just a synthetic empty/upgraded test database.
- **SentinelGPT production latency objectives** (added 2026-08-12, `RELEASE_READINESS.md`'s
  SentinelGPT section): establish first-token and total-response-time objectives from actual
  droplet measurements against the deployed Ollama instance, then confirm they're met. No local
  test can substitute — this needs the real droplet's hardware and model-loading behavior, not a
  synthetic benchmark.

## 2. Deployed acceptance journeys (needs a real browser session against the live app)

- **Mandatory MFA**: enroll a real admin TOTP secret against `https://admin.gwsapp.net`, sign
  out, and sign back in through the full MFA challenge + a recovery-code path once.
- **Security audit ledger**: perform a few real admin actions (user create, role change,
  password reset) against production and confirm they appear in `/admin/security-audit` with an
  intact hash chain.

Once both pass, flip their `RELEASE_READINESS.md` evidence-table status from "Local pass;
deployed acceptance required" to "Pass" with the date.

## 3. Legal sign-off (not an engineering task)

- **Privacy and incident operations**: send the current `/admin/privacy-operations` behavior
  (subject export, retention preview, incident register, 72-hour breach clock) to whoever does
  legal/compliance review for this app, and get their explicit approval on record. Nothing here
  is blocked on code.

## 4. Controlled external-account acceptance runs (need your credentials, not mine)

Each of these is marked "Not run" in `RELEASE_READINESS.md` because it needs a real,
controlled third-party account — plug in credentials and I can pick these up in a follow-up
session:

- **Real Notion sync + guarded write-back**: a real (non-production) Notion workspace connected
  via OAuth or a manual token, exercising discovery, selective sync, a write-back action, and
  webhook refresh.
- **Real social publish**: Facebook, X, and LinkedIn developer-app credentials plus real
  destination accounts, to exercise OAuth/refresh, publish success and a deliberate failure,
  and engagement import.
- **Live TURN relay**: a client genuinely behind a restrictive/symmetric NAT (a phone on
  cellular data works) joining a Live Show broadcast against the deployed coturn config.
- **Signed/notarized macOS install**: your Apple Developer signing identity, wired into the
  packaging step, then installed on a clean Mac that's never had this app on it.

## 5. What's already closed as of 2026-08-11

- Local build/test/audit/Compose checks: green (1098 tests).
- Deployed liveness, readiness, login surface, and public homepage: verified live against
  `admin.gwsapp.net` and `grantwatson.dev`.
- HTTP security headers (CSP, X-Frame-Options, Referrer-Policy, Permissions-Policy, HSTS):
  confirmed present on the live response.
- Previously-untested operational code now has test coverage: `OperationalAlertService`,
  `OperationalDataRetentionService`, `DiskSpaceHealthCheck`, and the CRM deal/pipeline additions
  to `CrmService` (all shipped 2026-08-10 with no tests until this pass).

Update this file's items to done (and remove them) as each is completed, so it stays a live
punch list rather than a historical record.
