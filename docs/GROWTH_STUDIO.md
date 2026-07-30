# Growth Studio

Growth Studio is GWS Business Suite's first-party audience analytics and social publishing
workspace at `/admin/growth`. It takes its product cues from Plausible's focused dashboard,
Matomo's data ownership and segmentation, and Google Analytics' event/conversion model. It is
a clean-room GWS implementation; it does not copy their source, branding, or proprietary
algorithms.

## Delivered foundation

- Cookieless public-site page views, session-scoped visitors, engagement time, custom events,
  referrer host, UTM acquisition dimensions, device category, and browser family.
- No raw IP address, full user-agent, full referrer URL, query string, or admin-route activity
  is stored. Do Not Track and Global Privacy Control disable the browser collector.
- Indexed, server-side date filtering with 7/30/90-day ranges, real-time visitor count,
  audience trend, top pages, acquisition, campaigns, devices, bounce rate, and engagement.
- `window.gwsAnalytics.track("event-name")` for first-party custom events.
- Named conversion goals for exact custom events, exact destination pages, or page-path
  prefixes ending in `*`, with active/paused state, conversion counts, unique converting
  visitors, session conversion rate, and top acquisition source. Removing a goal never
  removes the underlying analytics history.
- SentinelGPT-assisted, network-specific Facebook, X, and LinkedIn copy with editable
  previews, per-channel character limits, drafts, scheduling, delivery state, and retry.
- Encrypted social access tokens. Tokens remain on the server and are never returned to the
  browser after save.
- Direct text-post publishing through the Facebook Pages Graph API, X API v2, and LinkedIn
  Posts API. External developer/app approval and the network-specific user permissions are
  still prerequisites; GWS cannot bypass those platform controls.

## Parity roadmap

The three reference products cover years of features, so "parity" is tracked explicitly
rather than represented as one finished checkbox.

### Analytics phase 2

- Funnel builder and step-dropoff reports
- Saved segments and report filters
- Retention/cohort and new-versus-returning analysis
- Country/region reporting through a privacy-reviewed, locally hosted GeoIP database
- CSV export, scheduled email reports, annotations, and comparison periods

### Analytics phase 3

- Entry/exit flow and user-journey reports
- Revenue/ecommerce events tied to first-party conversion ids
- Search Console and ad-platform attribution imports
- Bot and referrer-spam rules with an auditable exclusion log
- Data-retention controls and automatic aggregate rollups
- Optional self-hosted session replay and heatmaps only after a separate consent, redaction,
  storage, and security design; these are intentionally not hidden inside basic analytics

### Social phase 2

- OAuth connection flows and token refresh for all three networks
- Image/video upload and platform-native preview cards
- Per-network content calendars, approval roles, and reusable campaign templates
- Pull engagement metrics back into the analytics dashboard
- Comments/inbox triage and UTM campaign builder
- Automation nodes for publish, approval, and performance-triggered follow-up

## Channel permissions

- Facebook: a Page access token authorized for the target Page with `pages_manage_posts`
- X: an OAuth 2.0 user access token with `tweet.write`
- LinkedIn member: `w_member_social`
- LinkedIn organization: an approved application and `w_organization_social`

Platform access tiers and review rules can change. Validate them against the current official
developer documentation before connecting production accounts.
