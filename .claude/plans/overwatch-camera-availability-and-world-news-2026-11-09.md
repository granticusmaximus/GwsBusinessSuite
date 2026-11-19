# Overwatch camera availability and World News context

## Goal

Make publicly accessible cameras easier to find and safer to use in Overwatch. Every camera will
show its actual viewing capability and permission mode, while World News will surface suitable
nearby cameras as visual context without presenting them as evidence that a reported event
occurred.

## Guardrails

- Ingest only government/open-data feeds, providers with explicit API or syndication permission,
  provider-approved embeds, and verified opt-in submissions.
- Do not scan networks, bypass logins, CAPTCHAs, token protection, bot protection, rate limits,
  robots restrictions, or access controls.
- Persist source terms, attribution, display/caching rights, rate limits, and review date for
  every provider. Disable a source when its permission expires or changes.
- Probe only the configured, provider-approved public endpoint at its allowed interval. A failed
  probe must not retry aggressively.
- A World News camera is contextual, not corroborating evidence. Only official sensors remain
  eligible for the existing "Backed by" section.

## Phase 1 — Provider registry and camera capabilities

1. Extend the existing `CameraFeed`/directory model with a provider reference and immutable
   source policy: attribution URL, public-use basis, allowed presentation mode, permitted cache
   duration, rate limit, review date, and enabled state.
2. Add a persisted capability record per camera: `LiveVideo`, `Snapshot`, `Timelapse`,
   `ProviderEmbed`, `LinkOnly`, `LoginRequired`, `Unavailable`, or `Unknown`; include the last
   successful probe, last failure, failure reason, detected content type, redirect target host,
   confidence (`ProviderConfirmed` or `Detected`), and next permitted probe time.
3. Use a migration and a single background capability service. It performs bounded `HEAD` or
   provider-approved `GET` checks only, recognizes HLS/DASH/video/image/embed responses, and
   records redirects/authentication/error status without following an unapproved host.
4. Preserve current `Snapshot` and `Hls` behavior. New capability data refines availability and
   presentation; it must not change a stream URL or infer permission.

## Phase 2 — Legal camera acquisition workflow

1. Add provider adapters only after documenting an explicit public API, open-data license,
   permitted embed, or written agreement. Start with state/provincial 511 agencies, municipal
   traffic APIs, transit/airport/port systems, public weather/wildfire/park cameras, and ArcGIS,
   Socrata, or CKAN catalogs.
2. Create an admin source-review workflow for opt-in agency/business submissions: ownership or
   authorization, public endpoint, precise location, source terms, attribution, display mode,
   rate limit, and takedown contact.
3. Add source-level health and compliance reporting: disabled/past-review sources, probe failure
   rate, stale camera metadata, and cameras removed by a provider. No automatic discovery from
   arbitrary web pages.

## Phase 3 — Overwatch availability UX

1. Render capability-specific pins and clusters: live, still, archive/timelapse, provider embed,
   link-only, restricted, unavailable, and unknown. Retain accessible text equivalents and do
   not rely on color alone.
2. Add layer filters for `Available now`, `Live video`, `Still image`, `Recent archive`,
   `Public embed`, `Link only`, provider, region, and stale/unavailable status.
3. In the camera panel show source, attribution, capability, confidence, last confirmed time,
   refresh cadence, and availability reason. Present media only when the policy permits it;
   otherwise show the official provider link/approved embed.
4. Add viewport camera lists and provider/type clustering so a dense region can be searched and
   sorted by availability, distance, and last confirmation without opening every stream.

## Phase 4 — World News camera context

1. Extend `WorldNewsContextService` to rank nearby cameras by distance, current availability,
   capability, source-policy eligibility, camera direction/coverage when known, and freshness.
   Keep the existing 25-mile city-level limit; country-level stories receive no local-camera
   claim.
2. Replace the undifferentiated nearby-camera list with `View nearby cameras`: availability,
   distance, source, viewing mode, and last confirmation. Offer only policy-permitted actions:
   open live/still, approved embed, timelapse, or official link.
3. Add a separate **Visual context** label and plain disclaimer: nearby public cameras may show
   conditions at a location but do not verify the news report. Do not mix these with official
   backing signals or AI-generated factual summaries.
4. For a relevant public snapshot camera, keep the existing Incident Capture flow. Save the
   capture with provider attribution, access time, selected story, and the camera's capability
   state. Do not capture live/embedded/link-only sources unless their policy explicitly permits
   storage.

## Phase 5 — Verification and rollout

1. Unit-test capability classification, source-policy enforcement, probe backoff, allowed-host
   redirects, camera ranking, and capture eligibility.
2. Add browser coverage for pin semantics, filters, source attribution, inaccessible-camera
   behavior, and the World News visual-context disclaimer.
3. Update `OVERWATCH_USER_GUIDE.md` and the World News portion of
   `INTELLIGENCE_USER_GUIDE.md`.
4. Run `./scripts/verify-release.sh`; manually validate a sample of each camera capability in
   browser, PWA, and Mac Catalyst before enabling new providers in production.

## Progress

- [x] Reviewed existing `CameraFeed` and `WorldNewsContextService` contracts.
- [x] Add Transport for NSW's CC BY public snapshot camera feed with focused parsing coverage.
- [ ] Implement phases 1–2: provider policy, capability persistence, and compliant probing.
- [ ] Implement phase 3: availability map/list UX.
- [ ] Implement phase 4: World News visual context and capture policy.
- [ ] Implement phase 5: tests, guides, release gate, and native/browser validation.
- [x] 19-11-26: World News visual context (phase 4 items 1-3, phase 3 item 3 in part).
  `CameraAvailability` (Application/CameraIntel) classifies each camera from its stream kind and
  the existing `CameraHealthService` checks: Live video / Still image; Available now / Not
  confirmed / Not working; last seen working. `WorldNewsContextService.NearbyCameras` ranks the
  25-mile list working-first, then by distance (country-level stories still get none). The
  Overwatch news panel shows **VISUAL CONTEXT · NEARBY CAMERAS** with mode, state, source and time,
  plus the "don't verify the report" line. Capture stays still-image only (`IncidentCaptureService`
  already filtered to `Snapshot`). `CameraAvailabilityTests`. Overwatch guide updated.
- Already in place before this plan's later phases (found 19-11-26, not rebuilt): server-side
  health checks with a 90 s minimum interval per camera (`CameraHealthService`), the camera panel's
  health badge and source link, and the "hide frozen, stale and offline" camera filter.
- [x] 19-11-26: phase 3 items 1-3 in part. Camera pins take their shape from `CameraAvailability`
  (round = still image, rounded square = live video, hollow = not working; `cameraPinImage` in
  tactical-globe.js, shape not colour since billboard.color is the alert tint), and reshape when a
  health check changes them. The camera window shows mode · state · source. **LIVE VIDEO ONLY**
  filters server-side (`CameraFilter`), so the Mac map gets it too, though the native MapKit pins
  don't take the shapes yet. Guard in `CameraAvailabilityTests`.
- [ ] Still open: persisted provider policy and capability records (phases 1-2; health is
  in-memory and restarts empty), native MapKit pin shapes, the provider/region filters and
  viewport camera list (phase 3 items 2, 4), the opt-in submission workflow, and Mac/PWA manual
  checks.
