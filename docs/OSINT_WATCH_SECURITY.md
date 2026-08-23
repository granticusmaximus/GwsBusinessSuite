# OSINT Watch integration and security boundary

OSINT Watch embeds the open-source [OSIRIS](https://github.com/simplifaisoul/osiris) dashboard
inside the GWS admin portal. This note records the exact reviewed build, what was and was not
audited, and the controls that keep the service from becoming a public side door into GWS.

## Pinned provenance

- The main `osiris` service is pinned to multi-platform image index digest
  `sha256:040f84f0a5ef00f69ed2903b9732657d882fca3afbffcc1f43b3b281732ed268`.
- That image was published successfully from upstream commit
  `daebcc9e2e2e8e15ed5a8ccb01eb990a810d1ce1` on 2026-08-22. Its Dockerfile uses a conventional
  Node 22 multi-stage Next.js build and runs the final application as an unprivileged user.
- The `osiris-intel` sanctions/entity-resolution service is built from the reviewed source under
  `vendor/osiris-intel/`; its separate README records that component's more detailed audit.
- Updating either component is a deliberate re-vendoring/review task. Do not replace the digest
  with a mutable tag.

The main OSIRIS application is large and was not line-by-line security audited. The integration
review covered its container build, dependency manifest, public routes, Next.js asset/API paths,
middleware, iframe requirements, and the features exposed by the GWS proxy. Treat the digest pin
as supply-chain drift control, not as a claim that every upstream data parser is defect-free.

## GWS controls

- Neither OSIRIS container publishes a host port. Only the `gwssuite` container can reach them on
  the private Compose network.
- Every proxy route requires the `AdminOnly` authorization policy and the existing `public-read`
  rate limit. More-specific native GWS `/api/...` routes continue to win over the OSIRIS API
  catch-all through ASP.NET Core route precedence.
- The proxy never forwards GWS cookies, authorization or antiforgery headers, client IP headers,
  browser referrers, or the browser's User-Agent. The internal HTTP handler has redirects and its
  shared cookie jar disabled; upstream `Set-Cookie` headers are discarded.
- The proxied document receives a dedicated CSP because Next.js needs inline RSC bootstrapping,
  workers, and remote map/media sources. That relaxation applies only beneath
  `/admin/osint-root`; normal GWS pages keep the stricter portal CSP. Framing remains limited to
  the same origin and the proxied document sends no referrer.
- Only GET, HEAD, and POST are forwarded. Active RECON scanning stays disabled because
  `SCANNER_URL` and `SCANNER_KEY` are not configured.

## Residual risk and operating rules

The iframe is same-origin so its root-relative `/api` calls can use the AdminOnly proxy. That also
means its JavaScript executes within the authenticated admin origin. Digest pinning, the private
network, and the proxy's credential stripping reduce the risk but do not provide the isolation of
a dedicated OSINT subdomain. Re-review upstream changes before updating the digest; if OSIRIS will
be maintained by a different trust group, move it to a separate authenticated origin instead of
loosening this boundary.

OSIRIS aggregates third-party public data. Results can be stale, incomplete, misidentified, or
temporarily unavailable. Use them as investigative leads and verify consequential conclusions
against authoritative sources. Do not enter private client data, secrets, or credentials into
OSINT queries.
