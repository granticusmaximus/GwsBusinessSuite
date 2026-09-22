// Overwatch Grid (Phase 1+2) - a self-contained CesiumJS interop module. Cesium itself is
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
    let snapshotRefreshTimer = null;

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

        const cameraEntities = new Map();
        const alertEntities = new Map();
        let debounceHandle = null;
        viewer.camera.moveEnd.addEventListener(function () {
            if (debounceHandle) clearTimeout(debounceHandle);
            debounceHandle = setTimeout(function () {
                const rectangle = viewer.camera.computeViewRectangle();
                if (!rectangle) return;
                dotNetRef.invokeMethodAsync('OnGlobeViewChanged',
                    Cesium.Math.toDegrees(rectangle.north),
                    Cesium.Math.toDegrees(rectangle.south),
                    Cesium.Math.toDegrees(rectangle.east),
                    Cesium.Math.toDegrees(rectangle.west));
            }, 500);
        });

        const clickHandler = new Cesium.ScreenSpaceEventHandler(viewer.scene.canvas);
        clickHandler.setInputAction(function (movement) {
            const picked = viewer.scene.pick(movement.position);
            const entity = picked && picked.id;
            if (entity && entity._tacticalGlobeCamera) {
                openStreamPanel(entity._tacticalGlobeCamera);
            }
        }, Cesium.ScreenSpaceEventType.LEFT_CLICK);

        viewers.set(containerId, { viewer, cameraEntities, alertEntities, radarLayer, clickHandler });

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
            } else if (event.key === 'Escape') {
                closeStreamPanel();
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

        entry.cameraEntities.forEach(function (entity) { entry.viewer.entities.remove(entity); });
        entry.cameraEntities.clear();

        (pins || []).forEach(function (pin) {
            const entity = entry.viewer.entities.add({
                position: Cesium.Cartesian3.fromDegrees(pin.lon, pin.lat),
                point: {
                    pixelSize: 9,
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

    function openStreamPanel(camera) {
        closeStreamPanel();

        const shell = document.getElementById('tg-viewport').closest('.tg-shell');
        if (!shell) return;

        const panel = document.createElement('div');
        panel.className = 'tg-stream-panel';
        panel.innerHTML =
            '<div class="tg-stream-panel-header">' +
            '<span>' + escapeHtml(camera.name) + '</span>' +
            '<button type="button" class="tg-stream-panel-close" aria-label="Close">X</button>' +
            '</div>' +
            '<div class="tg-stream-panel-body"></div>';

        panel.querySelector('.tg-stream-panel-close').addEventListener('click', closeStreamPanel);
        shell.appendChild(panel);
        streamPanel = panel;

        const body = panel.querySelector('.tg-stream-panel-body');
        renderStream(body, camera);
    }

    function renderStream(body, camera) {
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
        } else {
            const img = document.createElement('img');
            img.alt = camera.name;
            body.appendChild(img);
            const refresh = function () {
                img.src = camera.streamUrl + (camera.streamUrl.indexOf('?') >= 0 ? '&' : '?') + '_t=' + Date.now();
            };
            img.addEventListener('error', function () {
                body.innerHTML = '<div class="tg-stream-panel-error">FEED UNAVAILABLE</div>';
                if (snapshotRefreshTimer) clearInterval(snapshotRefreshTimer);
            }, { once: true });
            refresh();
            snapshotRefreshTimer = setInterval(refresh, 10000);
        }

        const meta = document.createElement('div');
        meta.className = 'tg-stream-panel-meta';
        meta.textContent = 'SOURCE: ' + camera.sourceName;
        body.appendChild(meta);
    }

    function closeStreamPanel() {
        if (snapshotRefreshTimer) {
            clearInterval(snapshotRefreshTimer);
            snapshotRefreshTimer = null;
        }
        if (streamPanel && streamPanel.parentNode) {
            const video = streamPanel.querySelector('video');
            if (video && window.civicWatchVideo) window.civicWatchVideo.detach(video);
            streamPanel.parentNode.removeChild(streamPanel);
        }
        streamPanel = null;
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text || '';
        return div.innerHTML;
    }

    function dispose(containerId) {
        closeStreamPanel();
        const entry = viewers.get(containerId || 'tg-viewport');
        if (!entry) return;
        entry.clickHandler.destroy();
        if (entry.hoverIdentify) entry.hoverIdentify.dispose();
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
        setCameraPins: setCameraPins,
        setRadarVisible: setRadarVisible,
        setWeatherAlerts: setWeatherAlerts,
        clearWeatherAlerts: clearWeatherAlerts,
        dispose: dispose,
        isCoverageBannerDismissed: isCoverageBannerDismissed,
        dismissCoverageBanner: dismissCoverageBanner
    };
})();
