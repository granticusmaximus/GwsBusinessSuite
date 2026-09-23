// Overwatch (Phase 1+2) - a self-contained CesiumJS interop module. Cesium itself is
// loaded from jsdelivr (see OverwatchGrid.razor) rather than vendored into this repo (its
// prebuilt release is 100+ MB of binary/minified assets - not appropriate to commit). jsdelivr
// is already a trusted script-src/style-src/font-src origin in this app's CSP for other
// vendored libs (see Program.cs's own comment), so this crosses no new trust boundary.
//
// CESIUM_BASE_URL must be set before any Cesium API call (not before Cesium.js loads - Cesium.js
// itself only defines the API at parse time, it doesn't fetch anything until first use) so
// Cesium knows where to find its own Workers/Assets/ThirdParty at runtime. This has to happen
// here, in an external file - this app's CSP has no 'unsafe-inline' in script-src, so an inline
// <script> block in the .razor page (the usual place this line lives in Cesium's own getting-
// started docs) would simply never run.
window.CESIUM_BASE_URL = 'https://cdn.jsdelivr.net/npm/cesium@1.145.0/Build/Cesium/';

window.tacticalGlobe = (function () {
    const viewers = new Map();
    let streamPanel = null;
    let watchWallPanel = null;
    let incidentPanel = null;
    let weatherPanel = null;

    async function init(containerId, dotNetRef) {
        const container = document.getElementById(containerId);
        if (!container || viewers.has(containerId)) return;

        // Real incident history for this one basemap: it first hit tile.openstreetmap.org
        // directly, which looked fine in every automated check (200 OK, valid PNG, CORS-open)
        // but is OSM's own website tile server, not a general-purpose public API - confirmed
        // live it now actively rejects this app's traffic (`x-blocked: Access denied`, and the
        // "tile" itself is a black placeholder), rendering an invisible globe. Switched to
        // Cesium's bundled Natural Earth II next - safe from third-party blocking, but it tops
        // out at zoom level 2 (confirmed against the loaded library), so zooming in past a
        // whole-continent view just showed a blurrier version of the same low-res texture
        // instead of the "zoom in and see the actual place" behavior of a real map/aerial view.
        // Esri's World_Imagery service (server.arcgisonline.com) is real satellite/aerial
        // imagery up to zoom level 23 - confirmed live: CORS-open, no API key/registration
        // required for this tile access, and (unlike Google Maps) not a Google product.
        // ArcGisMapServerImageryProvider has an async `fromUrl` factory in this Cesium version
        // (confirmed directly against the loaded library) since it has to fetch the service's
        // own metadata first.
        const baseLayer = new Cesium.ImageryLayer(
            await Cesium.ArcGisMapServerImageryProvider.fromUrl(
                'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer'));
        // A dark, high-contrast tint reads more "military terminal" than the raw aerial
        // photography's natural daylight colors - these are plain mutable properties Cesium
        // reads per-frame at draw time, so setting them immediately here is safe. Toned down
        // from the old map-tile values (which pushed hue harder for cartographic flat colors) -
        // a strong hue rotation looks wrong on photographic imagery.
        baseLayer.brightness = 0.7;
        baseLayer.contrast = 1.2;
        baseLayer.gamma = 0.9;
        baseLayer.saturation = 0.4;

        // Every default Cesium widget is disabled - this page builds its own retro-terminal
        // chrome around the bare 3D viewport instead (see OverwatchGrid.razor.css). No Ion
        // access token is configured or needed: no terrain (Viewer's own token-free
        // EllipsoidTerrainProvider default is left alone) and the explicit baseLayer above means
        // Cesium never falls back to an Ion-backed default imagery layer.
        const viewer = new Cesium.Viewer(container, {
            baseLayer: baseLayer,
            baseLayerPicker: false,
            geocoder: false,
            homeButton: false,
            sceneModePicker: false,
            navigationHelpButton: false,
            animation: false,
            timeline: false,
            fullscreenButton: false,
            infoBox: false,
            selectionIndicator: false,
            creditContainer: makeCreditContainer(container)
        });

        viewer.camera.setView({
            destination: Cesium.Cartesian3.fromDegrees(-98.5, 39.8, 18000000)
        });

        // A real, measured problem, not a feel-based guess: Cesium's default zoomFactor (5.0)
        // needs roughly 24 scroll-wheel ticks just to bring the camera from the initial
        // whole-continent view down to 5km - confirmed by directly logging camera height across
        // a real simulated scroll sequence. That is far more scrolling than anyone would
        // actually do to "zoom in on an area," which is exactly why hover-to-identify (gated on
        // being this close) read as completely non-functional, and part of why zooming felt
        // nothing like Google/Apple Maps. Raising this makes each tick zoom in noticeably
        // farther, matching a normal map's zoom pacing.
        viewer.scene.screenSpaceCameraController.zoomFactor = 8.0;

        // A real, reported gap: World_Imagery alone is pure aerial photography with zero labels
        // baked in - no street names, no city/state names anywhere, unlike Google/Apple Maps'
        // hybrid view (imagery + a labels layer on top). Esri's own free "Reference" services are
        // built for exactly this pairing - confirmed live: both are CORS-open, need no API key,
        // and render as transparent-background PNGs (place/boundary names at low-to-mid zoom,
        // road names at high zoom) meant to overlay any base imagery. Left at natural brightness
        // (no dark tint applied, unlike baseLayer above) so the text stays legible.
        const placesLabelsLayer = new Cesium.ImageryLayer(
            await Cesium.ArcGisMapServerImageryProvider.fromUrl(
                'https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer'));
        viewer.imageryLayers.add(placesLabelsLayer);
        const roadsLabelsLayer = new Cesium.ImageryLayer(
            await Cesium.ArcGisMapServerImageryProvider.fromUrl(
                'https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Transportation/MapServer'));
        viewer.imageryLayers.add(roadsLabelsLayer);

        // NOAA nowCOAST's public radar WMS (confirmed CORS-open and token-free directly against
        // the live endpoint during implementation) - added once, hidden by default, toggled via
        // .show rather than added/removed per toggle so re-enabling it is instant.
        const radarLayer = viewer.imageryLayers.addImageryProvider(new Cesium.WebMapServiceImageryProvider({
            url: 'https://nowcoast.noaa.gov/geoserver/observations/weather_radar/ows',
            layers: 'conus_base_reflectivity_mosaic',
            parameters: { transparent: true, format: 'image/png', styles: '' }
        }));
        radarLayer.show = false;
        radarLayer.alpha = 0.75;

        // GDOT (~3,829 cameras) and Datumfeed (~9,000 across several regions) mean a wide zoom
        // can render thousands of individual pins at once - plain viewer.entities has no
        // clustering support at all, so camera pins live on their own CustomDataSource instead,
        // which is the one kind of collection Cesium's EntityCluster can actually group.
        const cameraDataSource = new Cesium.CustomDataSource('tg-cameras');
        await viewer.dataSources.add(cameraDataSource);
        cameraDataSource.clustering.enabled = true;
        cameraDataSource.clustering.pixelRange = 60;
        cameraDataSource.clustering.minimumClusterSize = 3;
        cameraDataSource.clustering.clusterEvent.addEventListener(function (clusteredEntities, cluster) {
            // Restyle Cesium's default cluster billboard (a stock pin icon) to match the
            // terminal theme's own camera-pin look instead - a solid point plus a count label.
            cluster.billboard.show = false;
            cluster.point.show = true;
            cluster.point.pixelSize = 20;
            cluster.point.color = Cesium.Color.fromCssColorString('#7dffb0');
            cluster.point.outlineColor = Cesium.Color.fromCssColorString('#05080a');
            cluster.point.outlineWidth = 2;
            cluster.point.disableDepthTestDistance = Number.POSITIVE_INFINITY;
            cluster.label.show = true;
            cluster.label.text = clusteredEntities.length.toString();
            cluster.label.font = 'bold 13px "Share Tech Mono", monospace';
            cluster.label.fillColor = Cesium.Color.fromCssColorString('#05080a');
            cluster.label.verticalOrigin = Cesium.VerticalOrigin.CENTER;
            cluster.label.horizontalOrigin = Cesium.HorizontalOrigin.CENTER;
            cluster.label.disableDepthTestDistance = Number.POSITIVE_INFINITY;
        });

        const cameraEntities = new Map();
        const alertEntities = new Map();
        let debounceHandle = null;
        viewer.camera.moveEnd.addEventListener(function () {
            if (debounceHandle) clearTimeout(debounceHandle);
            debounceHandle = setTimeout(function () {
                // Tracked for shareable view links (see buildShareLink) - the camera's own
                // lat/lon/height, not the bbox rectangle used for the camera query below.
                const entry = viewers.get(containerId);
                if (entry) {
                    const carto = viewer.camera.positionCartographic;
                    entry.currentView = {
                        lat: Cesium.Math.toDegrees(carto.latitude),
                        lon: Cesium.Math.toDegrees(carto.longitude),
                        height: carto.height
                    };
                }

                const rectangle = viewer.camera.computeViewRectangle();
                if (!rectangle) return;
                dotNetRef.invokeMethodAsync('OnGlobeViewChanged',
                    Cesium.Math.toDegrees(rectangle.north),
                    Cesium.Math.toDegrees(rectangle.south),
                    Cesium.Math.toDegrees(rectangle.east),
                    Cesium.Math.toDegrees(rectangle.west));
            }, 500);
        });

        const incidentEntities = new Map();
        const clickHandler = new Cesium.ScreenSpaceEventHandler(viewer.scene.canvas);
        clickHandler.setInputAction(function (movement) {
            const picked = viewer.scene.pick(movement.position);
            const entity = picked && picked.id;
            // A clustered pin's pick.id is an array of the entities it groups (Cesium's own
            // clustering behavior), not a single Entity - zoom into the cluster instead of
            // treating it as a camera/incident click.
            if (Array.isArray(entity)) {
                const positions = entity
                    .map(function (e) { return e.position && e.position.getValue(viewer.clock.currentTime); })
                    .filter(function (p) { return !!p; });
                if (positions.length > 0) {
                    viewer.camera.flyToBoundingSphere(Cesium.BoundingSphere.fromPoints(positions), { duration: 0.75 });
                }
            } else if (entity && entity._tacticalGlobeCamera) {
                const entry = viewers.get(containerId);
                if (entry && entry.selectMode) {
                    toggleCameraSelection(entry, entity._tacticalGlobeCamera);
                } else {
                    openStreamPanel(entity._tacticalGlobeCamera, entry && entry.dotNetRef);
                }
            } else if (entity && entity._tacticalGlobeIncident) {
                openIncidentPanel(entity._tacticalGlobeIncident);
            }
        }, Cesium.ScreenSpaceEventType.LEFT_CLICK);

        viewers.set(containerId, {
            containerId, viewer, dotNetRef, cameraDataSource, cameraEntities, alertEntities, incidentEntities,
            radarLayer, clickHandler, selectMode: false, selectedCameras: new Map(), currentView: null
        });

        // Fire once for the initial view so cameras appear without requiring a drag/zoom first.
        setTimeout(function () {
            const rectangle = viewer.camera.computeViewRectangle();
            if (rectangle) {
                dotNetRef.invokeMethodAsync('OnGlobeViewChanged',
                    Cesium.Math.toDegrees(rectangle.north),
                    Cesium.Math.toDegrees(rectangle.south),
                    Cesium.Math.toDegrees(rectangle.east),
                    Cesium.Math.toDegrees(rectangle.west));
            }
        }, 400);

        ensureKeyboardShortcutsRegistered();
        setUpLocationSearch(viewer, dotNetRef);
        setUpShareLink(containerId);
        const hoverIdentify = setUpHoverIdentify(viewer, dotNetRef);
        viewers.get(containerId).hoverIdentify = hoverIdentify;
    }

    // Feature: hover-to-identify. Only active once zoomed in close enough for a hover to mean
    // something (MIN_HOVER_HEIGHT_METERS) - both because "what building is this" is meaningless
    // from a whole-continent view, and to avoid firing a reverse-geocode call on every idle
    // mouse wobble while zoomed out. Debounced (waits for the cursor to actually stop, not fired
    // continuously during movement) and skipped entirely while left-dragging, since Cesium's own
    // MOUSE_MOVE event fires throughout a drag/orbit gesture too, not just genuine hovers.
    // pickEllipsoid (not pickPosition/scene.pick) is the right tool here - this globe has no real
    // terrain or 3D building geometry (EllipsoidTerrainProvider default, see init's own comment),
    // just a flat WGS84 ellipsoid with imagery draped on it, so a ray-ellipsoid intersection is
    // both sufficient and doesn't require depth-buffer picking to be enabled.
    const MIN_HOVER_HEIGHT_METERS = 5000;
    const HOVER_DEBOUNCE_MS = 400;

    function setUpHoverIdentify(viewer, dotNetRef) {
        const shell = viewer.container.closest('.tg-shell');
        let tooltip = null;
        let debounceHandle = null;
        let requestToken = 0;
        let isDragging = false;

        function hideTooltip() {
            if (tooltip) tooltip.style.display = 'none';
        }

        function showTooltip(x, y, place) {
            if (!tooltip) {
                tooltip = document.createElement('div');
                tooltip.className = 'tg-hover-tooltip';
                shell.appendChild(tooltip);
            }
            tooltip.innerHTML =
                '<div class="tg-hover-tooltip-name">' + escapeHtml(place.displayName) + '</div>' +
                (place.address ? '<div class="tg-hover-tooltip-meta">' + escapeHtml(place.address) + '</div>' : '') +
                (place.category ? '<div class="tg-hover-tooltip-meta">' + escapeHtml(place.category) + '</div>' : '');
            tooltip.style.left = (x + 16) + 'px';
            tooltip.style.top = (y + 16) + 'px';
            tooltip.style.display = 'block';
        }

        const handler = new Cesium.ScreenSpaceEventHandler(viewer.scene.canvas);
        handler.setInputAction(function () {
            isDragging = true;
            hideTooltip();
        }, Cesium.ScreenSpaceEventType.LEFT_DOWN);
        handler.setInputAction(function () {
            isDragging = false;
        }, Cesium.ScreenSpaceEventType.LEFT_UP);
        handler.setInputAction(function (movement) {
            hideTooltip();
            if (debounceHandle) clearTimeout(debounceHandle);
            if (isDragging) return;
            if (viewer.camera.positionCartographic.height > MIN_HOVER_HEIGHT_METERS) return;

            const screenPosition = movement.endPosition;
            debounceHandle = setTimeout(async function () {
                const cartesian = viewer.camera.pickEllipsoid(screenPosition, viewer.scene.globe.ellipsoid);
                if (!cartesian) return;
                const cartographic = Cesium.Cartographic.fromCartesian(cartesian);
                const lat = Cesium.Math.toDegrees(cartographic.latitude);
                const lon = Cesium.Math.toDegrees(cartographic.longitude);

                const token = ++requestToken;
                const place = await dotNetRef.invokeMethodAsync('ReverseGeocodeAsync', lat, lon);
                if (token !== requestToken) return; // a newer hover already superseded this one
                if (place) showTooltip(screenPosition.x, screenPosition.y, place);
            }, HOVER_DEBOUNCE_MS);
        }, Cesium.ScreenSpaceEventType.MOUSE_MOVE);

        return {
            dispose: function () {
                if (debounceHandle) clearTimeout(debounceHandle);
                handler.destroy();
                if (tooltip && tooltip.parentNode) tooltip.parentNode.removeChild(tooltip);
            }
        };
    }

    // Feature: location search / jump-to. Geocoded server-side via the dotNetRef already used
    // for OnGlobeViewChanged, not a client-side fetch() - see GeocodingService's own comment for
    // why (Photon needs a real identifying User-Agent a browser can't set; its Census Bureau
    // fallback for exact US addresses isn't CORS-open at all). Both providers are free, need no
    // API key, and this feature briefly called Nominatim, then Photon, directly from the browser
    // before landing here - Nominatim started returning "Access denied" for this app (the same
    // OSM Foundation policy enforcement that separately blocked this page's old tile-based
    // basemap), and Photon alone still missed real, current US residential addresses often
    // enough to matter.
    function setUpLocationSearch(viewer, dotNetRef) {
        const input = document.getElementById('tg-search-input');
        const button = document.getElementById('tg-search-button');
        const status = document.getElementById('tg-search-status');
        if (!input || !button || !status) return;

        async function performSearch() {
            const query = input.value.trim();
            if (!query) return;

            status.textContent = 'SEARCHING...';
            try {
                const hit = await dotNetRef.invokeMethodAsync('GeocodeAsync', query);
                if (!hit) {
                    status.textContent = 'NOT FOUND';
                    return;
                }

                status.textContent = '';
                viewer.camera.flyTo({
                    destination: Cesium.Cartesian3.fromDegrees(Number(hit.longitude), Number(hit.latitude), 150000)
                });
            } catch (e) {
                status.textContent = 'SEARCH FAILED';
            }
        }

        button.addEventListener('click', performSearch);
        input.addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                performSearch();
            }
        });
    }

    // Shareable view links: encodes the current camera position + whatever camera(s) are open
    // (a single stream panel's _tgCamera, or a watch wall's _tgCameras - both stamped when
    // opened, see openStreamPanel/openWatchWallWithCameras) via BuildShareLinkAsync, which
    // returns a stateless, self-contained absolute URL (see SharedGridViewCodec) rather than a
    // DB-backed token. Returns the built URL (or null if there's no active viewer) regardless of
    // whether the clipboard write itself succeeds, so a caller can still show/log the link.
    async function buildShareLink(containerId) {
        const entry = viewers.get(containerId || 'tg-viewport');
        if (!entry) return null;

        const carto = entry.viewer.camera.positionCartographic;
        const view = entry.currentView || {
            lat: Cesium.Math.toDegrees(carto.latitude),
            lon: Cesium.Math.toDegrees(carto.longitude),
            height: carto.height
        };

        let openCameras = [];
        if (streamPanel && streamPanel._tgCamera) {
            openCameras = [streamPanel._tgCamera];
        } else if (watchWallPanel && watchWallPanel._tgCameras) {
            openCameras = watchWallPanel._tgCameras;
        }
        const dtoCameras = openCameras.map(function (c) {
            return {
                id: c.id, name: c.name, lat: c.lat, lon: c.lon, streamUrl: c.streamUrl,
                streamKind: c.streamKind, sourceName: c.sourceName, sourceAttributionUrl: c.sourceAttributionUrl
            };
        });

        const url = await entry.dotNetRef.invokeMethodAsync('BuildShareLinkAsync', view.lat, view.lon, view.height, dtoCameras);
        try {
            await navigator.clipboard.writeText(url);
        } catch (e) {
            // Clipboard access can be denied (permissions, a non-secure context, a headless test
            // environment) - the link is still returned so a caller can fall back to showing it.
        }
        return url;
    }

    function setUpShareLink(containerId) {
        const button = document.getElementById('tg-share-button');
        if (!button) return;
        button.addEventListener('click', async function () {
            const originalText = button.textContent;
            try {
                const url = await buildShareLink(containerId);
                button.textContent = url ? 'COPIED!' : 'COPY FAILED';
            } catch (e) {
                button.textContent = 'COPY FAILED';
            }
            setTimeout(function () { button.textContent = originalText; }, 1500);
        });
    }

    // Module-level, registered at most once regardless of how many times init()/dispose() runs
    // across page visits within the same document - an addEventListener per init() call would
    // otherwise accumulate duplicate listeners (each toggling the same button an extra time) if
    // this page is ever navigated away from and back to without a full page reload.
    let keyboardShortcutsRegistered = false;

    // R/A synthesize a click on the actual toggle buttons (reusing their existing Blazor
    // @onclick wiring rather than duplicating the toggle logic here); Escape closes the stream
    // panel directly since that's a purely client-side UI state with no server round-trip.
    function ensureKeyboardShortcutsRegistered() {
        if (keyboardShortcutsRegistered) return;
        keyboardShortcutsRegistered = true;

        document.addEventListener('keydown', function (event) {
            if (viewers.size === 0) return;
            const tag = (event.target && event.target.tagName || '').toLowerCase();
            if (tag === 'input' || tag === 'textarea') return;

            if (event.key === 'r' || event.key === 'R') {
                const button = document.querySelector('[data-tg-toggle="radar"]');
                if (button) button.click();
            } else if (event.key === 'a' || event.key === 'A') {
                const button = document.querySelector('[data-tg-toggle="alerts"]');
                if (button) button.click();
            } else if (event.key === 'i' || event.key === 'I') {
                const button = document.querySelector('[data-tg-toggle="incidents"]');
                if (button) button.click();
            } else if (event.key === 'c' || event.key === 'C') {
                const button = document.querySelector('[data-tg-toggle="coverage"]');
                if (button) button.click();
            } else if (event.key === 's' || event.key === 'S') {
                const button = document.querySelector('[data-tg-toggle="select"]');
                if (button) button.click();
            } else if (event.key === 'f' || event.key === 'F') {
                const button = document.querySelector('[data-tg-toggle="favorites"]');
                if (button) button.click();
            } else if (event.key === 'Escape') {
                closeStreamPanel();
                closeIncidentPanel();
                closeWatchWall();
            }
        });
    }

    // Cesium's default credit container floats over the canvas with its own light-on-dark
    // styling that clashes with the terminal theme - redirect it into the attribution bar this
    // page already renders (see OverwatchGrid.razor's .tg-attribution) instead of hiding it
    // outright, since OSM's attribution requirement still needs to be satisfied somewhere visible.
    function makeCreditContainer(container) {
        const el = document.createElement('div');
        el.style.display = 'none';
        container.appendChild(el);
        return el;
    }

    function setCameraPins(containerIdOrPins, maybePins) {
        // Supports being called as setCameraPins(pins) for the single-globe case this page
        // actually uses today, or setCameraPins(containerId, pins) if a future page ever hosts
        // more than one globe at once.
        let containerId = 'tg-viewport';
        let pins = containerIdOrPins;
        if (typeof containerIdOrPins === 'string') {
            containerId = containerIdOrPins;
            pins = maybePins;
        }

        const entry = viewers.get(containerId);
        if (!entry) return;

        entry.cameraEntities.forEach(function (entity) { entry.cameraDataSource.entities.remove(entity); });
        entry.cameraEntities.clear();

        (pins || []).forEach(function (pin) {
            const entity = entry.cameraDataSource.entities.add({
                position: Cesium.Cartesian3.fromDegrees(pin.lon, pin.lat),
                point: {
                    // Cesium's scene.pick hit-tests against the point's actual rendered size, so
                    // a bigger point is a genuinely bigger, easier click target, not just a
                    // cosmetic change - 9px was hard to land precisely, especially in areas with
                    // hundreds of nearby cameras (e.g. Austin, Georgia).
                    pixelSize: 14,
                    color: Cesium.Color.fromCssColorString('#7dffb0'),
                    outlineColor: Cesium.Color.fromCssColorString('#05080a'),
                    outlineWidth: 2,
                    disableDepthTestDistance: Number.POSITIVE_INFINITY
                }
            });
            entity._tacticalGlobeCamera = pin;
            entry.cameraEntities.set(pin.id, entity);
        });
    }

    // Used by the coverage index (jump straight to a known-covered region) - reuses the exact
    // Cartesian3.fromDegrees(lon, lat, height) + camera.flyTo shape already established for
    // location search, just parameterized instead of driven by a geocode result.
    function flyTo(containerId, lat, lon, heightMeters) {
        const entry = viewers.get(containerId || 'tg-viewport');
        if (!entry) return;
        entry.viewer.camera.flyTo({
            destination: Cesium.Cartesian3.fromDegrees(Number(lon), Number(lat), Number(heightMeters))
        });
    }

    function setRadarVisible(visibleOrContainerId, maybeVisible) {
        let containerId = 'tg-viewport';
        let visible = visibleOrContainerId;
        if (typeof visibleOrContainerId === 'string') {
            containerId = visibleOrContainerId;
            visible = maybeVisible;
        }
        const entry = viewers.get(containerId);
        if (entry) entry.radarLayer.show = !!visible;
    }

    // alerts: [{ id, eventName, severity, areaDescription, rings: [[[lon,lat],...], ...] }] -
    // rings is an array per polygon ring (outer boundary first) since a MultiPolygon alert
    // becomes multiple independent Cesium polygon entities sharing one alert id prefix.
    function setWeatherAlerts(containerIdOrAlerts, maybeAlerts) {
        let containerId = 'tg-viewport';
        let alerts = containerIdOrAlerts;
        if (typeof containerIdOrAlerts === 'string') {
            containerId = containerIdOrAlerts;
            alerts = maybeAlerts;
        }
        const entry = viewers.get(containerId);
        if (!entry) return;

        clearWeatherAlertEntities(entry);

        (alerts || []).forEach(function (alert) {
            alert.rings.forEach(function (ring, ringIndex) {
                const flat = [];
                ring.forEach(function (point) { flat.push(point[0], point[1]); });
                const entity = entry.viewer.entities.add({
                    polygon: {
                        hierarchy: Cesium.Cartesian3.fromDegreesArray(flat),
                        material: severityColor(alert.severity).withAlpha(0.35),
                        outline: true,
                        outlineColor: severityColor(alert.severity),
                        height: 0
                    }
                });
                entry.alertEntities.set(alert.id + ':' + ringIndex, entity);
            });
        });
    }

    function clearWeatherAlerts(containerId) {
        const entry = viewers.get(containerId || 'tg-viewport');
        if (entry) clearWeatherAlertEntities(entry);
    }

    function clearWeatherAlertEntities(entry) {
        entry.alertEntities.forEach(function (entity) { entry.viewer.entities.remove(entity); });
        entry.alertEntities.clear();
    }

    function severityColor(severity) {
        switch (severity) {
            case 'Extreme': return Cesium.Color.fromCssColorString('#ff3b3b');
            case 'Severe': return Cesium.Color.fromCssColorString('#ff9d3b');
            case 'Moderate': return Cesium.Color.fromCssColorString('#ffe83b');
            default: return Cesium.Color.fromCssColorString('#7dffb0');
        }
    }

    // incidents: [{ id, roadwayName, description, eventType, severity, lat, lon, sourceName,
    // sourceAttributionUrl }]. Colored by eventType, not severity - most incident sources' own
    // severity field is coarse/unreliable ("minor" on nearly everything, per GDOT's real feed),
    // while eventType (accidentsAndIncidents/roadwork/closures/specialEvents) is the far more
    // useful at-a-glance distinction for "should I care about this."
    function setTrafficIncidents(containerIdOrIncidents, maybeIncidents) {
        let containerId = 'tg-viewport';
        let incidents = containerIdOrIncidents;
        if (typeof containerIdOrIncidents === 'string') {
            containerId = containerIdOrIncidents;
            incidents = maybeIncidents;
        }

        const entry = viewers.get(containerId);
        if (!entry) return;

        entry.incidentEntities.forEach(function (entity) { entry.viewer.entities.remove(entity); });
        entry.incidentEntities.clear();

        (incidents || []).forEach(function (incident) {
            const entity = entry.viewer.entities.add({
                position: Cesium.Cartesian3.fromDegrees(incident.lon, incident.lat),
                point: {
                    pixelSize: 14,
                    color: incidentTypeColor(incident.eventType),
                    outlineColor: Cesium.Color.fromCssColorString('#05080a'),
                    outlineWidth: 2,
                    disableDepthTestDistance: Number.POSITIVE_INFINITY
                }
            });
            entity._tacticalGlobeIncident = incident;
            entry.incidentEntities.set(incident.id, entity);
        });
    }

    function clearTrafficIncidents(containerId) {
        const entry = viewers.get(containerId || 'tg-viewport');
        if (!entry) return;
        entry.incidentEntities.forEach(function (entity) { entry.viewer.entities.remove(entity); });
        entry.incidentEntities.clear();
    }

    function incidentTypeColor(eventType) {
        switch (eventType) {
            case 'accidentsAndIncidents': return Cesium.Color.fromCssColorString('#ff3b3b');
            case 'closures': return Cesium.Color.fromCssColorString('#ff9d3b');
            case 'roadwork': return Cesium.Color.fromCssColorString('#ffe83b');
            default: return Cesium.Color.fromCssColorString('#8fd3ff');
        }
    }

    function openIncidentPanel(incident) {
        closeIncidentPanel();

        const shell = document.getElementById('tg-viewport').closest('.tg-shell');
        if (!shell) return;

        const panel = document.createElement('div');
        panel.className = 'tg-stream-panel tg-incident-panel';
        panel.innerHTML =
            '<div class="tg-stream-panel-header">' +
            '<span>' + escapeHtml(incident.roadwayName) + '</span>' +
            '<button type="button" class="tg-stream-panel-close" aria-label="Close">X</button>' +
            '</div>' +
            '<div class="tg-stream-panel-body">' +
            '<div class="tg-incident-type">' + escapeHtml(formatIncidentType(incident.eventType)) + '</div>' +
            '<div>' + escapeHtml(incident.description) + '</div>' +
            '<div class="tg-stream-panel-meta">SOURCE: ' + escapeHtml(incident.sourceName) + '</div>' +
            '</div>';

        panel.querySelector('.tg-stream-panel-close').addEventListener('click', closeIncidentPanel);
        shell.appendChild(panel);
        incidentPanel = panel;
        makeDraggableAndResizable(panel, panel.querySelector('.tg-stream-panel-header'));
    }

    function closeIncidentPanel() {
        if (incidentPanel && incidentPanel.parentNode) {
            incidentPanel.parentNode.removeChild(incidentPanel);
        }
        incidentPanel = null;
    }

    function formatIncidentType(eventType) {
        switch (eventType) {
            case 'accidentsAndIncidents': return 'ACCIDENT / INCIDENT';
            case 'closures': return 'CLOSURE';
            case 'roadwork': return 'ROADWORK';
            case 'specialEvents': return 'SPECIAL EVENT';
            default: return (eventType || 'UNKNOWN').toUpperCase();
        }
    }

    // snapshot: { currentTemperatureFahrenheit, currentConditions, forecastTemperatureFahrenheit,
    // shortForecast, detailedForecast, windSpeed, windDirection, chanceOfPrecipitationPercent } or
    // null (e.g. the view center is over open ocean/outside NWS coverage). A fixed HUD panel, not
    // a draggable one like the stream/incident panels - there's only ever one, it always reflects
    // "wherever the view currently is," and it updates automatically on every settle rather than
    // being opened/closed per click.
    function setWeatherSnapshot(containerIdOrSnapshot, maybeSnapshot) {
        let containerId = 'tg-viewport';
        let snapshot = containerIdOrSnapshot;
        if (typeof containerIdOrSnapshot === 'string') {
            containerId = containerIdOrSnapshot;
            snapshot = maybeSnapshot;
        }

        const shell = document.getElementById(containerId);
        const shellEl = shell && shell.closest('.tg-shell');
        if (!shellEl) return;

        if (!snapshot) {
            if (weatherPanel) weatherPanel.style.display = 'none';
            return;
        }

        if (!weatherPanel) {
            weatherPanel = document.createElement('div');
            weatherPanel.className = 'tg-weather-panel';
            shellEl.appendChild(weatherPanel);
        }

        const currentTemp = snapshot.currentTemperatureFahrenheit;
        const tempLine = currentTemp !== null && currentTemp !== undefined
            ? Math.round(currentTemp) + '°F' + (snapshot.currentConditions ? ' — ' + escapeHtml(snapshot.currentConditions) : '')
            : Math.round(snapshot.forecastTemperatureFahrenheit) + '°F (forecast)';
        const precip = snapshot.chanceOfPrecipitationPercent !== null && snapshot.chanceOfPrecipitationPercent !== undefined
            ? snapshot.chanceOfPrecipitationPercent + '% precip'
            : null;
        const windLine = [snapshot.windDirection, snapshot.windSpeed].filter(Boolean).join(' ');

        weatherPanel.innerHTML =
            '<div class="tg-weather-temp">' + tempLine + '</div>' +
            '<div class="tg-weather-meta">' + escapeHtml(snapshot.shortForecast || '') + '</div>' +
            '<div class="tg-weather-meta">' + [windLine, precip].filter(Boolean).map(escapeHtml).join(' · ') + '</div>';
        weatherPanel.style.display = 'block';
    }

    // Shared by the camera-stream and incident-detail panels - drag via the header, resize via a
    // handle in the bottom-right corner. Move/resize listeners are added to the document only
    // for the duration of an active drag/resize gesture and removed on mouseup, rather than
    // staying attached for the panel's whole lifetime - this is what stops them from piling up
    // across repeated open/close cycles (a panel is destroyed and recreated on every click).
    function makeDraggableAndResizable(panel, dragHandle) {
        let dragOffsetX = 0;
        let dragOffsetY = 0;

        function onDragMove(event) {
            const shellRect = panel.parentElement.getBoundingClientRect();
            panel.style.left = (event.clientX - shellRect.left - dragOffsetX) + 'px';
            panel.style.top = (event.clientY - shellRect.top - dragOffsetY) + 'px';
            panel.style.right = 'auto';
        }
        function onDragEnd() {
            document.removeEventListener('mousemove', onDragMove);
            document.removeEventListener('mouseup', onDragEnd);
        }
        dragHandle.addEventListener('mousedown', function (event) {
            const rect = panel.getBoundingClientRect();
            const shellRect = panel.parentElement.getBoundingClientRect();
            // Pin the panel's current on-screen position as explicit left/top before dragging -
            // it starts out positioned via CSS (top/right), which a drag needs to override.
            panel.style.left = (rect.left - shellRect.left) + 'px';
            panel.style.top = (rect.top - shellRect.top) + 'px';
            panel.style.right = 'auto';
            dragOffsetX = event.clientX - rect.left;
            dragOffsetY = event.clientY - rect.top;
            document.addEventListener('mousemove', onDragMove);
            document.addEventListener('mouseup', onDragEnd);
            event.preventDefault();
        });

        const resizeHandle = document.createElement('div');
        resizeHandle.className = 'tg-panel-resize-handle';
        panel.appendChild(resizeHandle);

        let resizeStartX = 0;
        let resizeStartY = 0;
        let startWidth = 0;
        let startHeight = 0;
        function onResizeMove(event) {
            panel.style.width = Math.max(240, startWidth + (event.clientX - resizeStartX)) + 'px';
            panel.style.height = Math.max(160, startHeight + (event.clientY - resizeStartY)) + 'px';
        }
        function onResizeEnd() {
            document.removeEventListener('mousemove', onResizeMove);
            document.removeEventListener('mouseup', onResizeEnd);
        }
        resizeHandle.addEventListener('mousedown', function (event) {
            resizeStartX = event.clientX;
            resizeStartY = event.clientY;
            startWidth = panel.offsetWidth;
            startHeight = panel.offsetHeight;
            document.addEventListener('mousemove', onResizeMove);
            document.addEventListener('mouseup', onResizeEnd);
            event.preventDefault();
            event.stopPropagation();
        });
    }

    // dotNetRef is optional - openCamera()/the coverage/favorites panels can open a camera
    // outside the normal pick flow. When present, it wires the header's favorite-star button to
    // ToggleCameraFavoriteAsync; without it (shouldn't normally happen once a viewer exists) the
    // star is just disabled rather than throwing.
    function openStreamPanel(camera, dotNetRef) {
        closeStreamPanel();

        const shell = document.getElementById('tg-viewport').closest('.tg-shell');
        if (!shell) return;

        const panel = document.createElement('div');
        panel.className = 'tg-stream-panel';
        panel._tgCamera = camera;
        panel.innerHTML =
            '<div class="tg-stream-panel-header">' +
            '<span>' + escapeHtml(camera.name) + '</span>' +
            '<span class="tg-stream-panel-header-actions">' +
            '<button type="button" class="tg-stream-panel-favorite" aria-label="Toggle favorite" aria-pressed="' + (camera.isFavorite ? 'true' : 'false') + '">' + (camera.isFavorite ? '★' : '☆') + '</button>' +
            '<button type="button" class="tg-stream-panel-analyze" aria-label="Analyze snapshot">ANALYZE</button>' +
            '<button type="button" class="tg-stream-panel-close" aria-label="Close">X</button>' +
            '</span>' +
            '</div>' +
            '<div class="tg-stream-panel-body"></div>';

        const favoriteButton = panel.querySelector('.tg-stream-panel-favorite');
        if (dotNetRef) {
            favoriteButton.addEventListener('click', function () {
                dotNetRef.invokeMethodAsync('ToggleCameraFavoriteAsync',
                    camera.id, camera.name, camera.lat, camera.lon, camera.streamUrl, camera.streamKind,
                    camera.sourceName, camera.sourceAttributionUrl
                ).then(function (isFavorite) {
                    camera.isFavorite = isFavorite;
                    favoriteButton.textContent = isFavorite ? '★' : '☆';
                    favoriteButton.setAttribute('aria-pressed', isFavorite ? 'true' : 'false');
                });
            });
        } else {
            favoriteButton.disabled = true;
        }

        const analyzeButton = panel.querySelector('.tg-stream-panel-analyze');
        if (dotNetRef) {
            analyzeButton.addEventListener('click', function () {
                analyzeButton.disabled = true;
                analyzeButton.textContent = 'ANALYZING...';
                dotNetRef.invokeMethodAsync('AnalyzeCameraAsync',
                    camera.id, camera.name, camera.lat, camera.lon, camera.streamUrl, camera.streamKind,
                    camera.sourceName, camera.sourceAttributionUrl
                ).then(function (resultText) {
                    // A slow response can arrive after this exact panel was closed or replaced -
                    // streamPanel is reassigned to null/a different panel by closeStreamPanel/a
                    // later openStreamPanel call, so this identity check is enough to detect
                    // that and silently no-op rather than writing into a detached node.
                    if (streamPanel !== panel || !panel.isConnected) return;
                    analyzeButton.disabled = false;
                    analyzeButton.textContent = 'ANALYZE';
                    renderAnalysisResult(panel.querySelector('.tg-stream-panel-body'), resultText);
                }).catch(function () {
                    if (streamPanel !== panel || !panel.isConnected) return;
                    analyzeButton.disabled = false;
                    analyzeButton.textContent = 'ANALYZE';
                    renderAnalysisResult(panel.querySelector('.tg-stream-panel-body'), 'Analysis failed.');
                });
            });
        } else {
            analyzeButton.disabled = true;
        }

        panel.querySelector('.tg-stream-panel-close').addEventListener('click', closeStreamPanel);
        shell.appendChild(panel);
        streamPanel = panel;
        makeDraggableAndResizable(panel, panel.querySelector('.tg-stream-panel-header'));

        const body = panel.querySelector('.tg-stream-panel-body');
        panel._tgStreamDispose = renderStream(body, camera);
    }

    // Returns a dispose() handle instead of writing to a shared module-level timer/video
    // reference - the watch wall (multiple simultaneous renderStream calls) needs each stream's
    // refresh interval/HLS attachment to be torn down independently, not as one shared global.
    // Every dispose() is idempotent (safe to call more than once) since a watch-wall tile can be
    // closed individually before the whole wall is closed.
    function renderStream(body, camera) {
        let disposed = false;

        if (camera.streamKind === 'Hls') {
            const video = document.createElement('video');
            video.controls = true;
            video.muted = true;
            video.autoplay = true;
            body.appendChild(video);
            if (window.civicWatchVideo) {
                window.civicWatchVideo.attach(video, camera.streamUrl);
            } else if (window.Hls && window.Hls.isSupported()) {
                const hls = new window.Hls();
                hls.loadSource(camera.streamUrl);
                hls.attachMedia(video);
            } else {
                video.src = camera.streamUrl;
            }
            appendStreamMeta(body, camera);
            return function dispose() {
                if (disposed) return;
                disposed = true;
                if (window.civicWatchVideo) window.civicWatchVideo.detach(video);
            };
        }

        const img = document.createElement('img');
        img.alt = camera.name;
        body.appendChild(img);
        let refreshTimer = null;
        const refresh = function () {
            img.src = camera.streamUrl + (camera.streamUrl.indexOf('?') >= 0 ? '&' : '?') + '_t=' + Date.now();
        };
        img.addEventListener('error', function () {
            body.innerHTML = '<div class="tg-stream-panel-error">FEED UNAVAILABLE</div>';
            if (refreshTimer) clearInterval(refreshTimer);
        }, { once: true });
        refresh();
        refreshTimer = setInterval(refresh, 10000);
        appendStreamMeta(body, camera);
        return function dispose() {
            if (disposed) return;
            disposed = true;
            if (refreshTimer) clearInterval(refreshTimer);
        };
    }

    function appendStreamMeta(body, camera) {
        const meta = document.createElement('div');
        meta.className = 'tg-stream-panel-meta';
        meta.textContent = 'SOURCE: ' + camera.sourceName;
        body.appendChild(meta);
    }

    // Replaces-in-place on repeat ANALYZE clicks rather than stacking a new div each time.
    function renderAnalysisResult(body, text) {
        let resultDiv = body.querySelector('.tg-stream-panel-analysis');
        if (!resultDiv) {
            resultDiv = document.createElement('div');
            resultDiv.className = 'tg-stream-panel-analysis';
            body.appendChild(resultDiv);
        }
        resultDiv.textContent = text;
    }

    function closeStreamPanel() {
        if (streamPanel) {
            if (streamPanel._tgStreamDispose) streamPanel._tgStreamDispose();
            if (streamPanel.parentNode) streamPanel.parentNode.removeChild(streamPanel);
        }
        streamPanel = null;
    }

    // Watch wall: view several selected cameras' streams at once instead of one at a time.
    // Reuses renderStream/makeDraggableAndResizable as-is - neither has any single-panel-specific
    // coupling. openWatchWall(containerId) opens whatever is currently selected (see
    // setSelectModeEnabled/toggleCameraSelection); openWatchWallWithCameras is the lower-level
    // entry point, also reused by shareable view links to reopen a shared multi-camera view.
    function openWatchWall(containerId) {
        const entry = viewers.get(containerId || 'tg-viewport');
        if (!entry) return;
        openWatchWallWithCameras(containerId, Array.from(entry.selectedCameras.values()));
    }

    function openWatchWallWithCameras(containerId, cameras) {
        closeStreamPanel();
        closeWatchWall(containerId);
        if (!cameras || cameras.length === 0) return;

        const shell = document.getElementById('tg-viewport') && document.getElementById('tg-viewport').closest('.tg-shell');
        if (!shell) return;

        const panel = document.createElement('div');
        panel.className = 'tg-watch-wall-panel';
        panel._tgCameras = cameras.slice();
        panel.innerHTML =
            '<div class="tg-watch-wall-header">' +
            '<span>WATCH WALL (' + cameras.length + ')</span>' +
            '<button type="button" class="tg-watch-wall-close" aria-label="Close">X</button>' +
            '</div>' +
            '<div class="tg-watch-wall-grid"></div>';

        const grid = panel.querySelector('.tg-watch-wall-grid');
        const columns = Math.max(1, Math.ceil(Math.sqrt(cameras.length)));
        grid.style.gridTemplateColumns = 'repeat(' + columns + ', 1fr)';

        cameras.forEach(function (camera) {
            const tile = document.createElement('div');
            tile.className = 'tg-watch-wall-tile';
            tile.innerHTML =
                '<div class="tg-watch-wall-tile-header">' +
                '<span>' + escapeHtml(camera.name) + '</span>' +
                '<button type="button" class="tg-watch-wall-tile-close" aria-label="Close">X</button>' +
                '</div>' +
                '<div class="tg-watch-wall-tile-body"></div>';
            const body = tile.querySelector('.tg-watch-wall-tile-body');
            const dispose = renderStream(body, camera);
            tile.querySelector('.tg-watch-wall-tile-close').addEventListener('click', function () {
                dispose();
                tile.remove();
                panel._tgCameras = panel._tgCameras.filter(function (c) { return c.id !== camera.id; });
            });
            grid.appendChild(tile);
        });

        panel.querySelector('.tg-watch-wall-close').addEventListener('click', function () { closeWatchWall(containerId); });
        shell.appendChild(panel);
        watchWallPanel = panel;
        makeDraggableAndResizable(panel, panel.querySelector('.tg-watch-wall-header'));

        const entry = viewers.get(containerId || 'tg-viewport');
        if (entry) {
            entry.selectedCameras.clear();
            updateSelectionIndicator(entry);
        }
    }

    function closeWatchWall() {
        if (watchWallPanel) {
            watchWallPanel.querySelectorAll('.tg-watch-wall-tile-close').forEach(function (button) { button.click(); });
            if (watchWallPanel.parentNode) watchWallPanel.parentNode.removeChild(watchWallPanel);
        }
        watchWallPanel = null;
    }

    // Select mode: while enabled, clicking a camera pin adds/removes it from the current
    // selection (and restyles the pin) instead of opening its stream panel directly - see the
    // click handler in init(). A floating indicator shows the running count and opens the wall.
    function setSelectModeEnabled(containerIdOrEnabled, maybeEnabled) {
        let containerId = 'tg-viewport';
        let enabled = containerIdOrEnabled;
        if (typeof containerIdOrEnabled === 'string') {
            containerId = containerIdOrEnabled;
            enabled = maybeEnabled;
        }
        const entry = viewers.get(containerId);
        if (!entry) return;
        entry.selectMode = !!enabled;
        if (!entry.selectMode) {
            entry.selectedCameras.forEach(function (camera, id) { restyleSelectedPin(entry, id, false); });
            entry.selectedCameras.clear();
            updateSelectionIndicator(entry);
        }
    }

    function toggleCameraSelection(entry, camera) {
        if (entry.selectedCameras.has(camera.id)) {
            entry.selectedCameras.delete(camera.id);
            restyleSelectedPin(entry, camera.id, false);
        } else {
            entry.selectedCameras.set(camera.id, camera);
            restyleSelectedPin(entry, camera.id, true);
        }
        updateSelectionIndicator(entry);
    }

    function restyleSelectedPin(entry, cameraId, selected) {
        const entity = entry.cameraEntities.get(cameraId);
        if (!entity || !entity.point) return;
        entity.point.outlineColor = Cesium.Color.fromCssColorString(selected ? '#ffe066' : '#05080a');
        entity.point.outlineWidth = selected ? 4 : 2;
    }

    function updateSelectionIndicator(entry) {
        const shell = document.getElementById('tg-viewport') && document.getElementById('tg-viewport').closest('.tg-shell');
        if (!shell) return;
        const count = entry.selectedCameras.size;
        let indicator = shell.querySelector('.tg-selection-indicator');
        if (count === 0) {
            if (indicator && indicator.parentNode) indicator.parentNode.removeChild(indicator);
            return;
        }
        if (!indicator) {
            indicator = document.createElement('button');
            indicator.type = 'button';
            indicator.className = 'tg-selection-indicator';
            indicator.addEventListener('click', function () { openWatchWall(entry.containerId); });
            shell.appendChild(indicator);
        }
        indicator.textContent = 'SELECTED: ' + count + ' - OPEN WATCH WALL';
    }

    // Opens a camera directly, bypassing the pick-a-rendered-pin flow - needed for a favorited
    // camera or a shared-link camera that may be well outside the globe's current bbox-queried
    // pin set, so no clickable entity for it necessarily exists.
    function openCamera(containerIdOrCamera, maybeCamera) {
        let containerId = 'tg-viewport';
        let camera = containerIdOrCamera;
        if (typeof containerIdOrCamera === 'string') {
            containerId = containerIdOrCamera;
            camera = maybeCamera;
        }
        const entry = viewers.get(containerId);
        openStreamPanel(camera, entry && entry.dotNetRef);
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text || '';
        return div.innerHTML;
    }

    function dispose(containerId) {
        closeStreamPanel();
        closeWatchWall();
        const entry = viewers.get(containerId || 'tg-viewport');
        if (!entry) return;
        entry.clickHandler.destroy();
        if (entry.hoverIdentify) entry.hoverIdentify.dispose();
        entry.viewer.dataSources.remove(entry.cameraDataSource, true);
        entry.viewer.destroy();
        viewers.delete(containerId || 'tg-viewport');
    }

    // The "no setup required" coverage banner (OverwatchGrid.razor) is permanently accurate for
    // an admin who never configures the optional WSDOT/Windy keys - it never clears itself, so
    // it needs an explicit per-admin dismiss. localStorage matches the one other per-viewer UI
    // preference already in this codebase (CivicWatchThemeToggle's own theme key) rather than
    // adding server-side persistence for a purely cosmetic banner.
    const COVERAGE_BANNER_KEY = 'overwatch-grid-coverage-banner-dismissed';

    function isCoverageBannerDismissed() {
        try {
            return window.localStorage.getItem(COVERAGE_BANNER_KEY) === 'true';
        } catch (e) {
            return false;
        }
    }

    function dismissCoverageBanner() {
        try {
            window.localStorage.setItem(COVERAGE_BANNER_KEY, 'true');
        } catch (e) {
        }
    }

    return {
        init: init,
        flyTo: flyTo,
        setCameraPins: setCameraPins,
        setSelectModeEnabled: setSelectModeEnabled,
        openWatchWall: openWatchWall,
        openWatchWallWithCameras: openWatchWallWithCameras,
        closeWatchWall: closeWatchWall,
        openCamera: openCamera,
        buildShareLink: buildShareLink,
        setRadarVisible: setRadarVisible,
        setWeatherAlerts: setWeatherAlerts,
        clearWeatherAlerts: clearWeatherAlerts,
        setTrafficIncidents: setTrafficIncidents,
        clearTrafficIncidents: clearTrafficIncidents,
        setWeatherSnapshot: setWeatherSnapshot,
        dispose: dispose,
        isCoverageBannerDismissed: isCoverageBannerDismissed,
        dismissCoverageBanner: dismissCoverageBanner
    };
})();
