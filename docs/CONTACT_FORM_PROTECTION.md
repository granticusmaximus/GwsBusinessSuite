# Contact form verification

The configured Canvas site's root `/contact` page uses Cloudflare Turnstile. Other
CMS forms retain their existing behavior. The `/cms/{siteSlug}/contact` preview
uses the same server verification; changing the host or submitting directly cannot
bypass it.

## Activate before deploying

1. In the Cloudflare dashboard, open **Turnstile → Add widget**. Choose **Managed**.
   The free plan is sufficient; changing DNS or moving hosting is unnecessary.
2. Add `grantwatson.dev` and `www.grantwatson.dev` as allowed hostnames. Add
   `admin.gwsapp.net` if you want to submit from the CMS preview.
3. The widget is created (2026-10-03) and its **public site key**
   (`0x4AAAAAAFM91g1Lw0-PZtBZ`) is already set in `appsettings.json`. Add only the secret to
   the droplet's `.env` (outside source control):

   ```text
   Turnstile__SecretKey=<private secret key from the widget's settings>
   ```

   `Turnstile:AllowedHostnames` already contains the three hostnames above. If you ever rotate
   the widget, override the site key with `Turnstile__SiteKey` in `.env` too.
4. Restart the application when deploying the configuration and code together.

**Without valid keys, the Contact form does not accept submissions.** A missing
site key disables the Send button and displays a temporary-unavailability message.
A missing secret or provider outage rejects the request before saving, notifying,
or triggering CRM workflows. There is no automatic verification bypass.

For local testing, use Cloudflare's documented test key pair and allow your local
hostname in `Turnstile:AllowedHostnames`. Never deploy the always-pass test keys.
Automated tests mock the provider and do not send messages or call production.

## Visitor experience

Visitors complete verification and select Send Message. Failed verification keeps
their entered message on the page so they can retry. Expired tokens refresh through
Turnstile. The server checks the token, hostname, and `contact` action before saving.
The existing honeypot and rate limit remain active. Tokens are never saved with
the message or included in notification emails.

Successful submissions redirect to `/contact/thank-you`, using the site's current
navigation, footer, colors, and typography:

> **Thank you for reaching out.**
>
> Your message has been received. I’ll review your request and contact you using
> the details you provided.
>
> Thank you for your interest.

The page includes a Return to home link and is excluded from search indexing.
No database migration or manually created CMS page is needed.

## Validation before release

- Confirm the widget loads on the actual Contact page under the site's CSP.
- Complete verification, send one test inquiry, and confirm its thank-you redirect
  and a single saved submission/notification.
- Check that missing, expired, and replayed tokens cannot create a submission.
- Check the CMS preview separately if its hostname is enabled.

References: [free plan](https://developers.cloudflare.com/turnstile/plans/),
[widget setup](https://developers.cloudflare.com/turnstile/get-started/),
[server validation](https://developers.cloudflare.com/turnstile/get-started/server-side-validation/),
[test keys](https://developers.cloudflare.com/turnstile/troubleshooting/testing/).
