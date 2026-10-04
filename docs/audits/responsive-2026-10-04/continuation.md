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

Use a running local application with a disposable database and a local audit account. Set `GWS_AUDIT_ADMIN_BASE`, `GWS_AUDIT_PUBLIC_BASE`, `GWS_AUDIT_USER`, `GWS_AUDIT_PASSWORD`, and an absolute `GWS_AUDIT_OUT` path. Optional filters are `GWS_AUDIT_DEVICES` and `GWS_AUDIT_ROUTES`; `GWS_AUDIT_TEXT_SCALE=2` adds 200% root text scenarios. Use a fresh output directory for a new run.

Run `dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj -c Release --filter FullyQualifiedName~ResponsiveAuditTests`. `GWS_AUDIT_ENFORCE=1` fails the audit on load failures, overflow, covered controls, or undersized touch targets. `verify-release.sh` sets enforcement automatically when an audit target is supplied and explicitly reports when the audit is not run.

`python3 scripts/check-responsive-breakpoints.py` runs in release verification. Existing nonstandard viewport widths are listed per stylesheet in `scripts/responsive-breakpoint-exceptions.json`; new CSS must use the six standard tiers. Container queries are independent of viewport tiers. Remove legacy exceptions as those components migrate.

## Remaining acceptance work

- Supply representative local content for the three missing public pages and audit their real layouts.
- Review interactive states beyond entry pages: navigation drawers, resource detail editors, dialogs, database views, canvas interactions, and keyboard navigation.
- Verify native Mac WebView behavior. Its source hosts the same responsive web UI and hides the floating reload control on MacCatalyst; native window testing remains outstanding.
- Physical iPhone Safari, Android Chrome, iPad, and Windows Edge checks, including notch/home-indicator insets, software keyboard, rotation, text settings, and touch gestures.
- Gradually migrate the documented legacy viewport breakpoints; the lint prevents additional ad-hoc widths but does not claim that migration is complete.

The post-Phase-2 Intelligence-source research remains queued until responsive acceptance is complete.
