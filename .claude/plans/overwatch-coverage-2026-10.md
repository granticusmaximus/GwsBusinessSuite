# Overwatch coverage (October batch item 9) - started 2026-10-04

Grant: "Start work on ... the deferred Overwatch coverage" (2026-10-04). Same standard as the master
plan's Workstream A (humming-mapping-hartmanis.md A.1-A.3): official, free, no-key feeds only,
every endpoint verified live before building, [JsonPropertyName] on every field, one HttpClient
per provider type.

## Done 2026-10-04 - WZDx incidents batch 2 (verified, verify-release PASS)

- 14 new key-less WZDx work-zone feeds from USDOT's registry (datahub.transportation.gov
  69qe-yiui): FL, MD, WI, WA, KY, NJ, KS, IN, NC, MS, DE, LA, MO work zones, Austin TX. Each fetched
  live through the real provider. Counts of current work on 2026-10-04: FL 1629, Austin 1838, IN 948,
  WI 725, MO 533, NJ 434, NC 378, WA 368, KS 337, KY 123, MS 119, MD 83, DE 13, LA 0.
- WzdxIncidentProvider now keeps only current work (start/end dates, "completed" status) - FL had
  26,109 entries, mostly planned/finished.
- Incident pins are clustered on the globe (amber), like cameras.
- "wzdx" HttpClient: automatic decompression (FL always gzips) + User-Agent (WI 403s without).
- Excluded: NY (registry URL serves an SPA), NM (blyncsy 503 - retry), MN (CARS511 events already
  include roadwork), county feeds inside covered states, all key-required feeds.

## Next: cameras - leads verified live 2026-10-04 (not built yet)

| Lead | Endpoint | Notes |
| --- | --- | --- |
| Singapore | https://api.data.gov.sg/v1/transport/traffic-images | 200, no key (guide lists SG as key-gated - that's stale). camera_id, location.latitude/longitude, image URL that rotates every ~1 min - re-fetch list per refresh, cache ~1 min |
| Hong Kong | https://static.data.gov.hk/td/traffic-snapshot-images/code/Traffic_Camera_Locations_En.csv | 200, 377KB CSV (non-UTF8 bytes - check encoding). Images at tdcctv.data.one.gov.hk/{key}.JPG (verify) |
| Iceland | https://gagnaveita.vegagerdin.is/api/vefmyndavelar2014_1 | 200 JSON: Myndavel (name), Skyring (desc), Slod (direct jpg), Breidd (lat), Lengd (lon). Very clean |
| MN/NE plow cams | services.arcgis.com/8lRhdTsQyJpO52F1 .../AVL_Plow_Cam_Images_{Minnesota,Nebraska}_View/FeatureServer/0 | snowplow camera stills (moving, seasonal); PHOTO_LATITUDE/LONGITUDE fields; check image URL field + freshness |
| Tennessee | spatial.tdot.tn.gov/arcgis/rest/services (folder "Smartway") | check for a camera layer |
| Colorado | api-511x-co.carsprogram.org | successor to the retired cotrip endpoint; camera API undocumented |

Never researched yet: AK, NM, SD, WV cameras; MA re-check; Taiwan, Norway, Denmark, Estonia,
Switzerland; Canada NS/NB/MB/SK/YT/NL/PE; Australia WA/SA/TAS/NT.
- Resolved: the 688s suite run on 2026-10-04 was machine load; it ran in 124s on 2026-10-05 with no code change.

## Camera research 2026-10-08 (cameras batch above: Iceland, Hong Kong, Singapore BUILT; TN/CO/MN/NE plow excluded)

**Waiting on Grant - same 511 platform covers 8 regions:** Alaska (511.alaska.gov), Nova Scotia
(511.novascotia.ca), New Brunswick (511.gnb.ca), Manitoba (manitoba511.ca), Saskatchewan
(hotline.gov.sk.ca), Yukon (511yukon.ca), Newfoundland (511nl.ca), PEI (511.gov.pe.ca).
- Key-less public-site data works: `GET /List/GetData/Cameras?query={"start":0,"length":N,...}`
  (header X-Requested-With) returns location, roadway, WKT point, images[] (imageUrl `/map/Cctv/{id}`,
  disabled/blocked flags); images are real JPEGs. `/map/mapIcons/Cameras` gives counts (AK 130 sites).
- BUT the official developer API "Requires a developer key" (free, registered account). Using the
  site's internal endpoint would sidestep that - same class as the deferred "self-registered key"
  states. Decision needed: (A) Grant registers free keys (also unlocks AZ/WI/NV/UT/LA/NC/CT/NJ/PA/
  ON/AB from the guide's key-gated list) and one generic keyed provider reads them from config, or
  (B) use the key-less public endpoint, or (C) skip.

**Built 2026-10-08:**
- Estonia: official Tark Tee ArcGIS `tram/road_cameras` layer; metadata and full-size JPEGs at
  `/images/{image_path}` both verified live.
- Norway: official Statens vegvesen `road-weather-and-view.atlas.vegvesen.no` endpoint returns
  790 measurement sites. Its still-image service returned HTTP 500 for every sampled camera, so
  the provider deliberately exposes only the 134 active cameras with verified public, CORS-enabled
  HLS manifests and segments.

**Still in progress:**
- Denmark: the current official Trafikinfo map exposes no camera layer in its UI or initial data
  calls. Do not add a provider until a first-party camera dataset and real images are found.

**Not yet researched:** Taiwan (tisvcloud.freeway.gov.tw timed out), Switzerland (likely no public
DOT cams), NM (nmroads.com), SD (sd511.org), WV (wv511.org), MA (mass511.com - possibly Iteris like
SC), Australia WA/SA/TAS/NT (SA/TAS behind Cloudflare challenge).

## Research + build 2026-10-09 (everything that didn't need Grant)

**Built (verify-release PASS):**
- South Dakota + Montana: `IterisAtisCameraProvider` (one class, one DI instance per state) reads
  `{state}.cdn.iteris-atis.com/geojson/icons/metadata/icons.cameras.geojson` + `icons.rwis.geojson`
  (sites with nested cameras[]; MT's own cams are only in the RWIS layer). Live: SD 699, MT 464.
  Probed every other state code on that CDN: all 403.
- West Virginia: `WestVirginia511CameraProvider` - camera_data JS object from
  `wsvc/gmap.asmx/buildCamerasJSONjs`, HLS URL per camera from `flowplayeri.aspx?CAMID=` (host
  vtc1/2/3.roadsummary.com is per-camera; other hosts answer 204). Manifest probed; live 111/133.
  CORS-open; native HLS plays under our CSP in Chromium (Playwright check). ~19s to resolve, cached 6h.
- New Mexico: `NmRoadsCameraProvider` - `servicev5.nmroads.com/RealMapWAR/GetCameraInfo` (185), images
  via https `GetCameraImage?cameraName=` (snapshotFile is http-only on ss.nmroads.com, no TLS).

**Excluded:** Massachusetts (Castle Rock OneWeb, undocumented API - same bucket as CO; could join
Grant's internal-endpoint decision), Taiwan (TDX "Valid API Key Required"; freeway.gov.tw hosts
unreachable), Denmark (vejdirektoratet.dk camera page 403s automation; DATEX portal needs
registration), Switzerland/WA/NT (no official public camera feed found), SA/TAS (Cloudflare
challenge), NM WZDx (blyncsy still 503 on 2026-10-09).

Nothing researchable without Grant remains in this file; open items are the 511-platform key
decision (A/B/C above) plus Massachusetts/Colorado if he picks B.
