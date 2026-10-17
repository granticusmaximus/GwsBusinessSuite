# Sentinel: lock pages with Touch ID / passkey / PIN - options and plan (DRAFT, 2026-10-17)

Status: **built 2026-10-17** (Grant: "Go"). Progress log at the end. Grant asked (2026-10-17) for the
ability to lock Sentinel pages so they open only after Touch ID on the MacBook (passkey) or a PIN, "on
command", and to investigate the options first.

## What "locked" should mean (the threat model decides everything)

| Kind of lock | Protects against | Cost |
|---|---|---|
| **A. Access lock** (recommended v1) | Someone using your already signed-in browser or Mac app (unattended laptop, shoulder-surfing, a shared admin session), and other Sentinel users who could otherwise see the page | Content stays readable to anyone with database or server access |
| **B. Access lock + encryption at rest** (server-held key) | Also leaked database copies and backups | Little; a compromised running server can still read it |
| **C. End-to-end encrypted lock** (key from the passkey itself, WebAuthn PRF) | Also the server and its admins | Server search, Ask/SentinelGPT, export, previews and live co-editing can't read the page. The master plan already ruled E2E out for this reason. Needs a Safari 18+/Chrome passkey with PRF; losing every passkey loses the page |

**Recommendation:** build A now, with B as a small add-on in the same phase, because both use the
same enforcement points. Keep C as an optional "vault" page type later.

## How Touch ID can reach the server - the options

| Option | Where it works | Strength | Notes |
|---|---|---|---|
| **1. WebAuthn passkey** (platform authenticator = Touch ID) | Safari, Chrome and Edge on the Mac, and iPhone. **Not** inside the Mac app's web view | Strong: the server verifies a signature from a key in the Secure Enclave / iCloud Keychain | Standard. Needs a server library (fido2-net-lib, MIT). Relying party = `admin.gwsapp.net`; `localhost` works in dev. WKWebView only allows WebAuthn with an Apple-provisioned entitlement or associated domain, which this ad-hoc build doesn't have |
| **2. Native Touch ID in the Mac app** (LocalAuthentication / LAContext) + device key | The Mac app (Workspace tab) | Good. Best if the key is in the Secure Enclave, weaker if it's a key file in the app container | LAContext needs no entitlement, so it works in this build. The problem is key storage: keychain entitlements break this app's launch (see the Aug 2026 memory). Phase 0 tests whether a Secure Enclave key can be created without the keychain; if not, v1 uses a P-256 key file in the sandbox container, released only after Touch ID. Same server protocol either way: the server sends a challenge and the device signs it, so upgrading to the Secure Enclave later is a client-only change |
| **3. PIN** | Everywhere (iPhone/Android app, any browser, fallback) | Weakest. Mitigated by server-side hashing, strict attempt limits and lockout | Needed anyway as recovery, in case a passkey or Mac is lost |
| 4. Apple "Sign in with passkey" via associated domains in the Mac app | Mac app | Strong | Needs a paid Apple Developer team, a provisioning profile and an `apple-app-site-association` file on the domain. Not possible with the current ad-hoc build; revisit if the app ever gets provisioned |
| 5. macOS-level only (FileVault, screen lock) | - | Doesn't protect a page inside a signed-in app | Not an answer to the request on its own |

**Recommendation:** 1 (browsers) + 2 (Mac app) + 3 (PIN fallback), all granting the same short-lived
**unlock grant** on the server.

## Proposed design

**Locking a page**
- Page ⋯ menu → **Lock page**, plus the command palette, a shortcut and a **Lock all now** button
  that re-locks everything immediately.
- "Lock" sets `WikiPage.LockedBy` / `LockedAtUnix`. Only the owner or an Admin can lock or unlock it
  permanently.
- Option (decision 2): sub-pages inherit the lock.

**Unlocking**
- Opening a locked page shows a lock screen instead of the editor: "Unlock with Touch ID" (passkey,
  or native in the Mac app) and "Use PIN".
- Success creates a server-side **unlock grant** (user + sign-in session id + page or "all my locked
  pages" + expiry, e.g. 15 min, configurable).
- Grants end on expiry, **Lock all now**, sign-out, or (Mac app) when the app is hidden or the Mac
  sleeps.
- The grant is tied to the sign-in session (a new `sid` claim at sign-in), not the browser tab, so
  the Mac app's native call and its web view share it.

**Server-side enforcement (the real work).** Every path that hands out page content checks
`IsReadableNow(page, user, session)`; a locked page without a grant returns only its title and
icon, or nothing:
- Editor load and side pane
- Hover previews and page cards on whiteboards
- Workspace and suite search: locked pages kept out of the semantic index (chunks removed on lock)
  and out of keyword search snippets
- Ask/SentinelGPT tools (search_wiki etc.), caretaker digests, knowledge gaps
- Exports (PDF/Word/Markdown/workspace ZIP) and presentation mode
- Public shares (refused while locked) and offline copies (left out)
- Live co-editing broadcasts and backlink previews
- Revisions/history, suggested edits, duplicate/templates, synced blocks sourced from a locked page
- The read-only Developer API / SentinelCLI
- Attachments: `/admin/sentinel/files/{id}` refuses files that only a locked page references
  ("lock files")
- Sweep: grep every reader of `WikiPage.BlocksJson`/`Markdown` and gate or exclude it. A test
  enumerates these so a new reader can't silently bypass the lock

**Enrollment** (Settings → Security):
- **Passkeys:** register one or more ("MacBook Touch ID", "iPhone"), each named, last used,
  revocable.
- **Mac app:** "Enable Touch ID unlock on this Mac" creates the device key and registers its public
  key.
- **PIN:** 6-8 digits, stored with the Identity password hasher. After 5 wrong tries in 15 min the
  PIN is locked for that user; passkeys still work. Every attempt is written to the security audit
  log.
- **Recovery:** an Admin can remove a lock. This is audited, and the owner gets an email and a bell
  notification. With option C it's impossible by design.

**Optional B (encryption at rest):** a locked page's BlocksJson (and its revisions) is stored
encrypted with ASP.NET Data Protection (purpose `Sentinel.PageLock.v1`) and decrypted only for a
granted reader. Unlocking permanently decrypts it.

## Phases (each verified with `./scripts/verify-release.sh`)

| # | Unit | Done when |
|---|---|---|
| 0 | Spikes on Grant's Mac: (a) Secure Enclave P-256 key creation in the ad-hoc MacCatalyst build without keychain entitlement; (b) LAContext Touch ID prompt from MAUI; (c) WebAuthn passkey + Touch ID against `localhost` in Safari and Chrome with fido2-net-lib | Each answered yes/no, recorded here; picks the device-key storage for phase 3 |
| 1 | Lock model + enforcement: migration (lock columns, unlock grants, PIN hash/attempts), `sid` claim, `ISentinelPageLockService`, gate every content reader (list above), lock screen + PIN unlock, Lock all now, audit entries | A locked page can't be read through any path without a grant (tests per path), and PIN unlock works |
| 2 | Passkeys: fido2-net-lib registration and assertion (userVerification=required), credential table, Settings → Security UI, Permissions-Policy `publickey-credentials-get/create=(self)`, JS interop module (no inline scripts - CSP) | Register with Touch ID in Safari/Chrome; unlock a page with it; a cloned or replayed assertion is refused |
| 3 | Mac app native Touch ID: device key (Secure Enclave if spike 0a passed, else key file in the container), enrolment endpoint, challenge/sign unlock endpoint, a web-view → native bridge (custom-scheme navigation the app already intercepts in OnWorkspaceNavigating), re-lock on app hide/sleep | In the Mac app, Touch ID unlocks a locked page; quitting or sleeping re-locks it |
| 4 | (Optional) encryption at rest for locked pages and their revisions | The database file shows no plaintext for a locked page; unlocking restores it |
| 5 | Docs: `docs/SENTINEL_USER_GUIDE.md` (locking, enrolment, recovery), security notes, plan log | Guide updated |

## Risks / honest limits

- An access lock is only as strong as the server: anyone with the database (without option B) or
  the server can read locked pages.
- The Mac app key file (if the Secure Enclave spike fails) is protected by Touch ID only through
  the app; malware already running as Grant could copy it. It's still much better than nothing for
  "laptop left open", and upgrades cleanly to the Secure Enclave.
- The enforcement sweep is the main risk: one forgotten reader leaks content. That's why phase 1
  includes an enumerating test.
- Passkeys are bound to `admin.gwsapp.net`. Changing that domain means re-enrolling.

## Decisions (Grant, 2026-10-17)

- **Lock strength:** A + B, an access lock plus encryption at rest (phase 4 is in scope, not
  optional).
- **Who unlocks:** only the person who locked the page. An Admin can remove a lock (audited, owner
  notified), but can't read the page without removing it.
- **Mac app:** if the Secure Enclave spike fails, use the Touch ID-gated key file in the app
  container, upgrading to the Secure Enclave later.
- **Unlock window:** 15 minutes. Also re-lock on sign-out, Lock all now, and when the Mac app is
  hidden or the Mac sleeps.
- **Sub-pages:** not asked. Default: sub-pages inherit their parent's lock, so locking a parent
  protects what's under it. Confirm when phase 1 starts.

## Phase 0 results (2026-10-17)

- (a) Secure Enclave key creation from the real ad-hoc MacCatalyst build: **no** -
  `OSStatus -34018 (failed to add key to keychain)`, the same missing-entitlement cause as the Aug
  2026 SecureStorage note. As Grant decided, the Mac app uses a P-256 key file in its container,
  used only after LocalAuthentication confirms Touch ID. The code still tries the Secure Enclave
  first, so a provisioned build would pick it up with no server change.
- (c) WebAuthn: verified end to end with Chromium's virtual authenticator (user verification on)
  against the real server code - register, unlock, replay refused. Real Touch ID in Safari/Chrome
  is still for Grant to try.

## Added 2026-10-17 (Grant, mid-build): sign in with Touch ID

"I also want to be able to log into that app itself with my Touch ID, especially if I am running
the macOS app." Same keys as page unlocking:
- **Mac app:** at launch, if this Mac has a Touch ID key, the app signs a one-time server
  challenge (Touch ID prompt) and is signed in without password or MFA. This replaces the device
  secret file, which skips MFA with no biometric. The session carries the Mac-app claim so
  Content Studio still works.
- **Browser:** "Sign in with Touch ID" on the login page, using a discoverable passkey (the ones
  registered for page locks). A user-verified passkey counts as phishing-resistant multi-factor,
  so it replaces password + MFA.
- Both are recorded in the security audit log and have the login rate limit.

## Original questions

1. **Lock strength:** access lock (A), A plus encryption at rest (B), or the end-to-end "vault" (C)?
2. **Who unlocks:** only the person who locked it (Apple Notes style), or anyone with access, using
   their own Touch ID/PIN?
3. **Sub-pages:** inherit the parent's lock, or lock each page separately?
4. **Unlock window:** how long a page stays unlocked (5 / 15 / 60 min, or until the app is hidden).
5. **Mac app Touch ID:** accept a key file in the app container if the Secure Enclave doesn't work
   in this build, or only use passkeys in a real browser?


## Progress log

- 2026-10-17, all phases built in one pass (plus Touch ID sign-in, added mid-build).
  - **Deviation that makes it stronger:** a locked page's content is stored only encrypted
    (`WikiPage.LockedContentProtected`, Data Protection purpose `GwsBusinessSuite.Sentinel.PageLock.v1`;
    BlocksJson `[]`, Markdown empty). Every reader that misses the lock check therefore sees an
    empty page (fail closed), so the "enumerating test" became a stronger one: after locking, a
    scan of every TEXT column in every table finds no trace of the page's text.
  - **Locking also moves out:** version history (`WikiPageRevision.ContentProtected`), suggested
    edits (`locked:` + protected ChangesJson), text #tags and the search-index entry.
  - **Writes:** WikiService refuses to save a locked page unless the saver is its owner with a
    live unlock in the same sign-in session (`sid` claim, new per sign-in; `LockSessionId` on
    the editor model). The server-side writers are refused too: daily note append,
    duplicate/revert/compare, and Notion re-import skips.
  - **Shown as locked:** public share, export, offline copies and the side pane.
  - **Phases 2-3:** passkeys via fido2-net-lib 4.3.1 (MIT; RPID = page host, allowlist
    `Sentinel:Passkeys:Hosts` default admin.gwsapp.net, plus localhost). Mac app key: Secure
    Enclave impossible in this build (phase 0), key file + LAContext. Endpoints:
    native lock-device/-challenge/-unlock/-session, `/auth/touchid/challenge|login` and
    `/auth/passkey/options|login`. Sign-in gives `amr` device-key (counts as a Mac app session:
    Content Studio policy, NavMenu, Home) or passkey.
  - **Adding a second unlock method needs a live unlock** (stolen-session guard).
  - **Found while testing:**
    1. The locked-page placeholder editor reported itself as a change, and the unlock's
       re-open flushed it over the real content (data loss). That's fixed:
       `_editorHoldsLockedPlaceholder`.
    2. "Lock page" could miss keystrokes not yet reported by the editor (`SaveEditorNowAsync`).
  - **Tests:** 7 lock (incl. the DB-wide scan), 3 device-key, 3 passkey browser (Chromium virtual
    authenticator, incl. discoverable sign-in). A live local run with a signed-in browser passed:
    PIN set → lock → lock screen (text absent from the DOM) → PIN unlock → edit → add passkey →
    Lock now → Touch ID unlock → sign out → Sign in with Touch ID → page opens unlocked. Afterwards
    the DB dump had zero copies of the text.
  - **Not run:** real Touch ID hardware (Safari/Chrome passkeys) and the Mac app flows on screen -
    for Grant.
