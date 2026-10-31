# Known gaps (31-10-26)

Grant: "Fix the known gaps", the list given the same day after everything planned was built.
Left out on purpose: the Woffice items an older plan deferred (LMS, HRM, e-commerce, a separate
PM system). The plan itself calls them substantial standalone products, not gaps; propose them
separately if wanted.

## Unit 1 - Mail gaps
- **More than one mailbox per person.** The unique index on MailAccount.Username goes; there's an
  account switcher, "Add another mailbox", and up to 10 accounts each. Every IMailService call
  names the account. The unread count adds up all of a person's inboxes.
- **Folders and labels.** Gmail user labels, and IMAP folders other than the standard ones, are
  listed under the standard folders (keys "label:<id>" and "folder:<path>").
- **Drafts.** Compose has Save draft; a draft opens back into compose. Sending a draft removes it.
  Gmail uses the drafts API; IMAP appends to Drafts (\Draft) and expunges the old copy.
- **Toolbar.** Bold, italic, lists, link and quote insert the Markdown around the selection (an
  external JS module; the CSP blocks inline scripts), Cmd/Ctrl+B and +I, and a Preview tab shows
  the formatted message in the sandboxed frame.

## Unit 2 - Overwatch cameras in unresearched regions
Taiwan, Norway, Denmark, Estonia, Switzerland, Australia (WA, SA, TAS, NT): check for key-less
public camera feeds. Build a provider for each that works (same shape as the Iceland/Singapore
ones); record the dead ends with the reason.

## Unit 3 - Cookie consent on the public site
Check what the public site actually stores or loads. Show a consent banner only for what needs
consent, gate that until it's given, and keep the choice (with a way to change it). External
scripts only (CSP).

## Progress
- **Unit 1 (Mail) built 31-10-26:**
  - Code:
    - Migration `AllowSeveralMailAccounts`. IMailService takes the account id on every call;
      `RequireRoomAsync` caps it at 10.
    - Backends gain `ListFoldersAsync` (Gmail user labels, IMAP non-standard folders) and
      `SaveDraftAsync`/`DeleteDraftAsync` (Gmail drafts API, mapping message id to draft id; IMAP
      Drafts append with \Draft then expunge the old copy).
    - Editing a draft keeps its threading and attachments; sending with DraftId removes the draft.
    - `mail-compose.js` toolbar (edge spaces kept outside markers), Preview through
      `MailFormatting.PreviewDocument`, mailbox switcher, Add/Edit/Remove mailbox, custom folders.
    - The attachment route includes the account; the Google callback returns
      `?account=<id>`.
  - Tests: `MailServiceTests` (17: multi-mailbox, drafts, Gmail labels and drafts, folder keys)
    and `MailComposeToolbarBrowserTests` (3).
- **Unit 2 (cameras) researched 31-10-26; nothing new to build:**
  - Norway (NorwayRoadCameraProvider) and Estonia (EstoniaRoadCameraProvider) were already built
    on 08-10-26. The "never researched" list in overwatch-coverage-2026-10.md was stale.
    - Estonia also has a DATEX II feed (`tarktee.ee/api/v1/datex/roadCameraImages`, Accept
      application/xml). It's a fallback if the ArcGIS layer ever goes.
  - Taiwan: Tainan's camera JSON (`trafficopendata.tainan.gov.tw`) and the freeway list
    (`tisvcloud.freeway.gov.tw`) both time out from outside Taiwan.
  - Denmark: Vejdirektoratet's data needs an account and API key.
  - Norway's DATEX also needs an account; the built provider uses the key-less public-site
    endpoint.
  - Switzerland: no public national road camera feed.
  - Western Australia: neither the Main Roads travel map nor the open-data catalogue has cameras.
  - South Australia: traffic.sa.gov.au refuses scripted requests (403).
  - Tasmania: camera footage is shared only with Police.
  - Northern Territory: the Road Report app has no camera layer.
- **Unit 3 (cookie consent) built 31-10-26:**
  - What needed consent: public-analytics.js (a 90-day visitor id in localStorage) and CJ's
    deep-link script (third-party cookies). Everything else is strictly necessary.
  - Code:
    - `js/consent.js` with a banner (Accept all / Necessary only / Choose) and
      `gws_consent_v1` in localStorage; it exposes `window.gwsConsent` and the `gws-consent`
      event, and loads CJ from `body[data-gws-affiliate-script]` only with consent.
    - The analytics script waits for that choice.
    - `/api/consent/region` (`ConsentRegions`: EEA + UK + CH, or unknown, asks).
    - A Privacy choices line under every footer; styles in public-site.css.
  - Tests: `PrivacyChoicesBrowserTests` (4 browser + 8 region) and the renderer test updated.
  - Not gated: Google Fonts (an IP to Google). Self-hosting the font pairings would remove it.
