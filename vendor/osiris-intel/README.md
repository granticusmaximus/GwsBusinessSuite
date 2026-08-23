# osiris-intel (vendored)

The `osiris-intel` service from [github.com/simplifaisoul/osiris](https://github.com/simplifaisoul/osiris)
(`intel/` subdirectory), vendored here rather than built directly against the
upstream repository, so this app's build is pinned to a reviewed, known copy
instead of silently picking up whatever the upstream repo contains on the
next `docker compose build`.

**Why vendored, not pulled live:** the upstream repository also contains an
unrelated `engine/` directory (shipped only as compiled Python bytecode, no
source, named consistently with a crypto-trading system tied to a memecoin
of the same "OSIRIS" name from the same author) that has nothing to do with
this service and is not part of any documented build path. `intel/server.js`
itself was read in full before vendoring and is a small (~780-line),
single-dependency (Express only) sanctions/entity-resolution service with no
overlap with that unrelated code. See [`docs/OSINT_WATCH_SECURITY.md`](../../docs/OSINT_WATCH_SECURITY.md)
for the integration's recorded provenance, controls, and residual risk.

**What it does:** correlates OSINT entities (aircraft, vessel, company,
person, IP, country) against OpenSanctions' public OFAC SDN list and
Wikidata, exposed as `GET /resolve?type=<type>&id=<id>`. Outbound requests
are restricted to an explicit domain allowlist (`query.wikidata.org`,
`data.opensanctions.org`, `www.wikidata.org`, `ip-api.com`, `stat.ripe.net`),
inputs are sanitized before being interpolated into SPARQL queries, and
requests are rate-limited per client IP. No API keys or secrets required.

**Provenance:** unmodified copy of `intel/server.js`, `intel/package.json`,
and `intel/Dockerfile` as of the commit reviewed when this was vendored
(2026-08). MIT licensed - `LICENSE` here is the upstream repository's license
file, carried forward per its terms. If upstream ships a real fix or feature
in this file later, re-vendor deliberately (re-fetch, re-read in full, then
replace) rather than assuming a diff is safe to apply blindly.
