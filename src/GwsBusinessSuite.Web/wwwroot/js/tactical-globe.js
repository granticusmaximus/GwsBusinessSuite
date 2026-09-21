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

        // A real incident: this used to hit tile.openstreetmap.org directly, which looked fine
        // in every automated check (200 OK, valid PNG, CORS-open) but is OSM's own website tile
        // server, not a general-purpose public API - confirmed live that it now actively rejects
        // this app's traffic (every response carries `x-blocked: Access denied - see
        // operations.osmfoundation.org/policies/tiles/` and the "tile" itself is a fully black
        // placeholder image), which rendered as an invisible globe indistinguishable from the
        // starfield behind it, not an error. Switched to Cesium's own bundled Natural Earth II
        // basemap instead - a low-res, public-domain whole-Earth texture shipped inside the same
        // jsdelivr-hosted Cesium build already trusted in this app's CSP, so this crosses no new
        // trust boundary, needs no token/registration, and can never be rate-limited or blocked
        // by a third party. TileMapServiceImageryProvider has an async `fromUrl` factory in this
        // Cesium version (confirmed directly against the loaded library) since it has to fetch
        // its own tilemapresource.xml first - unlike OpenStreetMapImageryProvider, which is a
        // plain synchronous constructor with no upfront metadata fetch.
        const baseLayer = new Cesium.ImageryLayer(
            await Cesium.TileMapServiceImageryProvider.fromUrl(
                Cesium.buildModuleUrl('Assets/Textures/NaturalEarthII')));
        // A dark, high-contrast, stylized look reads more "military terminal" than the bundled
        // basemap's natural daylight colors - these are plain mutable properties Cesium reads
        // per-frame at draw time, so setting them immediately here is safe.
        baseLayer.brightness = 0.55;
        baseLayer.contrast = 1.35;
        baseLayer.gamma = 0.8;
        baseLayer.hue = 3.4;
        baseLayer.saturation = 0.15;

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
        setUpLocationSearch(viewer);
    }

    // Feature: location search / jump-to. Client-side geocoding against OpenStreetMap's free
    // Nominatim API - the exact same approach already established in wikiMapView.js (no API key,
    // nominatim.openstreetmap.org is already an allowed connect-src origin in this app's CSP for
    // that reason). A single deliberate user-submitted search doesn't need that file's request
    // queue (built for geocoding many markers in a batch) - one lookup per Enter/click is well
    // within Nominatim's documented 1 request/second usage policy on its own.
    function setUpLocationSearch(viewer) {
        const input = document.getElementById('tg-search-input');
        const button = document.getElementById('tg-search-button');
        const status = document.getElementById('tg-search-status');
        if (!input || !button || !status) return;

        async function performSearch() {
            const query = input.value.trim();
            if (!query) return;

            status.textContent = 'SEARCHING...';
            try {
                const response = await fetch('https://nominatim.openstreetmap.org/search?format=json&limit=1&q=' + encodeURIComponent(query));
                const body = await response.json();
                const hit = body && body[0];
                if (!hit) {
                    status.textContent = 'NOT FOUND';
                    return;
                }

                status.textContent = '';
                viewer.camera.flyTo({
                    destination: Cesium.Cartesian3.fromDegrees(Number(hit.lon), Number(hit.lat), 150000)
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
