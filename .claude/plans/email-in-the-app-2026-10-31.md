# Email in the app (31-10-26)

Grant: "For the areas that are not yet inside the app, start working on those. I want to be able to
connect to my actual emails as well within the app." That covers the three gaps listed the same day:

1. The app's mail server login (SMTP host, username, password) could only be set in the droplet's `.env`.
2. The connected Google account could only send (gmail.send + calendar.events), so receiving needed an
   app password.
3. There was no inbox to read and answer your own mail in the app.

## Phase 1 - the app's mail account in Settings > Email
- `MailServerSettings` (one row): Enabled, Host, Port, Security, Username, PasswordProtected
  (ISecretProtector), FromAddress, last test result. Migration `AddMailServerSettings`.
- `IMailServerSettingsService`: get (no password, HasPassword), save, test. Keeps a snapshot in a
  singleton (`MailServerAccount`) so the synchronous `IMailTransport.Describe` never reads the database.
- Routing: when enabled, this account wins over `.env` and the Google account for every feature's mail.
  The From address becomes this account's (FromAddress, else the login), and each feature's display name
  stays. Off, nothing changes.
- `ServerMailAccount` (the plus-address mail-in default) uses it too when it's a Google login.
- UI: presets (Google, Microsoft 365/Outlook, iCloud, Fastmail, other), password kept unless retyped,
  Save, Send test. The existing route table shows it per feature.

## Phase 2 - a Google connection that can read mail
- GoogleOAuthService asks for gmail.modify as well (read, mark read, archive, trash; it also allows
  messages.send). Keep gmail.send and calendar.events. Record the granted scopes in the credential JSON.
- Automation > Credentials says when the Google connection is "send only" and needs reconnecting.
- Mail-in (email-to-ticket, Sentinel email-in) uses the Google connection when there's no app password
  and no server account: Gmail API list `deliveredto:<plus-address> is:unread`, fetch raw, the same
  pipeline, then remove UNREAD.
- Google note for the guide: a Workspace "Internal" OAuth app needs no verification for gmail.modify;
  an External app left in Testing loses its refresh token every 7 days.

## Phase 3 - Mail: your own inbox (/admin/mail)
- `MailAccount` per person: Provider (google | imap), Address, DisplayName; Google: the Automation
  credential "Mail - <username>" (token refresh reused); IMAP: host/port, SMTP host/port/security,
  login, PasswordProtected. Migration `AddMailAccounts`.
- `IMailboxClient` with a Gmail API and an IMAP/SMTP (MailKit) implementation: folders, list (search,
  paging), read, attachments, send (reply/forward keep In-Reply-To/References), mark read/unread, star,
  archive, trash. Read live from the mailbox; nothing stored in the app.
- Page: folders, message list, reading pane, compose (new, reply, reply all, forward).
  - HTML mail sanitized on the server (HtmlSanitizer) and shown in a sandboxed iframe (no scripts, no
    same-origin). Remote images off until "Show images" (tracking pixels).
  - Dates dd-MM-yy HH:mm. Loading states use LoadingIndicator. Admin only for now; accounts are per person.
- Nav: Mail under the business group, beside Support.

## Phase 4 - docs
- `docs/MAIL_USER_GUIDE.md`, indexed in `docs/USER_GUIDES.md` and mirrored into Sentinel like the others.

## Progress
- **Phase 1 built 31-10-26:**
  - Code: `MailServerSettings` + migration `AddMailServerSettings`, `MailServerSettingsService` /
    `AppMailAccount` snapshot, MailTransport routing and From rewrite, ServerMailAccount fallback, and
    `MailServerSettingsPanel` on Settings > Email.
  - Tests: `MailServerSettingsTests` (8).
- **Phase 2 built 31-10-26:**
  - Code:
    - GoogleOAuthService asks for full Gmail (`https://mail.google.com/`, which Gmail's IMAP needs
      for XOAUTH2) and records the granted scope and address.
    - Reconnecting updates the credential in place; before, it added a duplicate "Google".
    - `CompleteMailAuthorizationAsync` for per-person Mail.
    - `GoogleMailConnection` snapshot, kept fresh by MailTransport.
    - ServerMailAccount falls back to it with token sign-in (`ImapMailboxConnector.ConnectWithTokenAsync`);
      email-to-ticket, Sentinel email-in and board addresses pass the credential through.
  - Tests: OAuth mail test, scope theory, and ServerMailAccount fallback.
- **Phase 3 built 31-10-26:**
  - Code:
    - `MailAccount` + migration `AddMailAccounts` (unique per Username).
    - `IMailService`/`MailService`, with `GmailMailboxBackend` (API) and `ImapMailboxBackend` (MailKit).
    - `MailMessageRenderer` (HtmlSanitizer, cid images, remote images held back).
    - `/admin/mail` page and CSS; Mail in the nav.
    - Attachment download endpoint, where unsafe types are served as octet-stream.
    - `/auth/google/connect?purpose=mail`.
  - Tests: `MailServiceTests` (6, including a Gmail HTTP stub) and `MailMessageRendererTests` (4).
- **Phase 4 done:** `docs/MAIL_USER_GUIDE.md`, indexed and added to the Sentinel mirror list.
- Next (not built): attachments on new messages, a rich-text compose, an unread count in the nav,
  Mail for non-admins.
