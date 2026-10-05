# Responsive audit continuation — 2026-10-04

## Scope and evidence

Continued Claude's staged responsive work without changing publishing ownership. Grant publishes the repository and production changes.

The first continuation pass crawled 56 entry routes at all 12 planned viewport sizes (672 loads). The isolated local database contains 53 of those routes (636 rendered loads). `/about`, `/cv`, and `/contact` return 404 in this fixture; they are unavailable coverage, not responsive passes. The rendered routes had no measured document overflow or covered controls at normal text size.

The checker now uses 44px touch targets, measures target sizes below the initial viewport, rejects failed HTTP loads and authentication redirects, excludes visually hidden file inputs and closed details, and avoids treating clipped scroll-container rows as covered controls. Raw target counts from the older reports are not directly comparable because their threshold was 32px and their scope was the first viewport.

A second matrix and representative 200% text checks are being completed. Machine reports and screenshots are under `output/playwright/responsive/` (ignored generated artifacts). The audit exercises rendered entry pages; it does not exhaustively open every menu, modal, editor state, or record detail.

## Changes

- Enforce full touch hit areas for compact admin controls, sortable table headers, public navigation, blog filters, pagination, and logo links; retain inline prose links.
- Put Sentinel block actions in a separate row on touch screens and narrow windows; keep the desktop gutter entirely outside the document text/checkboxes.
- Wrap Sentinel's phone document toolbar and compact the shared admin header so 200% text does not hide account actions.
- Keep Sentinel's Share panel within the phone viewport as a scrollable sheet; stack its invitation fields and label its dialog/close action.
- Keep the CMS navigator clipped to its scrolling panel and reserve a usable canvas height when enlarged text grows the header and module dock.
- Wrap Community card actions and enlarge standalone profile/activity links.
- Maintain readable small text; enlarge rich-text toolbar actions, CSV export controls, and News Intelligence headline links.
- Avoid disposing OSINT's browser viewport from the prerendered component instance, which previously logged a JavaScript interop exception on every audited load.

## Running the audit

Use a running local application with a disposable database and a local audit account (the open-state checks type into a Sentinel page). Set `GWS_AUDIT_ADMIN_BASE`, `GWS_AUDIT_PUBLIC_BASE`, `GWS_AUDIT_USER`, `GWS_AUDIT_PASSWORD`, and an absolute `GWS_AUDIT_OUT` path. Optional filters are `GWS_AUDIT_DEVICES` and `GWS_AUDIT_ROUTES`; `GWS_AUDIT_EXTRA_ROUTES` adds record-specific editor routes (`/admin/pages/<id>/edit`, `/admin/automation/<id>`, `/admin/mind-maps/<id>`); `GWS_AUDIT_TEXT_SCALE=2` adds 200% root text scenarios. Use a fresh output directory for a new run. The local login limit is 5 attempts per 15 minutes and each device batch signs in once, so restart the app between long runs.

Each page is scrolled top to bottom before it is measured: the public site's scroll reveal keeps below-the-fold sections at opacity 0, and the first runs silently skipped their controls (carousel dots, tabs). Each route also has open states (`Interactions` in `ResponsiveAuditTests`): navigation drawer, account menu, command palette, notifications, the public site menu, Sentinel's page tree / Share / block menu, and the page editor's module list / page settings. Only controls inside the opened panel are checked for size and covering.

Run `dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj -c Release --filter FullyQualifiedName~ResponsiveAuditTests`. `GWS_AUDIT_ENFORCE=1` fails the audit on load failures, overflow, covered controls, or undersized touch targets. `verify-release.sh` sets enforcement automatically when an audit target is supplied and explicitly reports when the audit is not run.

`python3 scripts/check-responsive-breakpoints.py` runs in release verification; every viewport `@media` width must be one of the six standard tiers. All 55 legacy widths migrated on 2026-10-04 and `scripts/responsive-breakpoint-exceptions.json` is now empty:

- Admin components use `@container gws-main (...)` (the `.gws-content` area) instead of the window width. The old widths assumed the 248px sidebar was open, so each became its content-area equivalent (window minus about 304px; minus 32px below 768px where the sidebar is a drawer). Layouts now also adapt correctly when the sidebar is collapsed, in Fullscreen, and in the full-screen page editor.
- The public stylesheets (`public-site.css`, `cms-public.css`) rounded each width up to the next tier, so a layout collapses slightly earlier rather than overflowing.
- The page editor's device preview renders at real device widths (1280/820/390px) and scales to fit, so its Desktop and Tablet buttons still show those layouts in a narrow stage.

## Phase 2 results (2026-10-04)

Final run: 811 page loads and open states, 59 routes (56 standard plus the page, automation and mind-map editors) across all 12 device sizes, with representative About/résumé and Contact content in the local database. After the fixes below, the enforced re-check of every affected route and size passed with no overflow, covered controls or undersized touch targets.

Found and fixed along the way:

- Sentinel's document toolbar overlapped itself on child pages (breadcrumb under the presence avatars and favorite star) whenever the content area was narrow. It now wraps intrinsically at any width.
- Sentinel's Share panel opened underneath editor content at widths up to 991px (static toolbar ignored its z-index).
- The command palette's result list never scrolled on short screens; lower results ran under the footer and could not be reached.
- Touch sizes: account menu links, command palette input, carousel arrows and dots, tab buttons, accordion questions, the résumé's LinkedIn/GitHub links (live on /about) and the map's "Get directions" link.
- Page editor: "All modules"/"Reusable" on a phone switched a panel that was out of sight; it now scrolls into view below the admin bar. Device previews render at real widths.
- Text below 12px: Sentinel's Trash count badge and the Automation editor's connection labels.
- Audit blind spots: public sections hidden by scroll reveal were never measured (the page is now scrolled first, instantly, because the site uses smooth scrolling); the public menu's id is generated (scope read from aria-controls); an unbounded scroll could hang a batch (capped, and every in-page evaluation now has a 30s limit).

## Remaining acceptance work

- Interactive states not yet scripted: database views, canvas drag interactions, keyboard-only navigation.
- Verify native Mac WebView behavior. Its source hosts the same responsive web UI and hides the floating reload control on MacCatalyst; native window testing remains outstanding.
- Physical iPhone Safari, Android Chrome, iPad, and Windows Edge checks, including notch/home-indicator insets, software keyboard, rotation, text settings, and touch gestures.

The post-Phase-2 Intelligence-source research remains queued until responsive acceptance is complete.
