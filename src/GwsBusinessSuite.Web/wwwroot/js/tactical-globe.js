// Tactical Globe (Phase 1+2) - a self-contained CesiumJS interop module. Cesium itself is
// loaded from jsdelivr (see TacticalGlobe.razor) rather than vendored into this repo (its
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

    function init(containerId, dotNetRef) {
        const container = document.getElementById(containerId);
        if (!container || viewers.has(containerId)) return;

        // Every default Cesium widget is disabled - this page builds its own retro-terminal
        // chrome around the bare 3D viewport instead (see TacticalGlobe.razor.css). No Ion
        // access token is configured or needed: no terrain (Viewer's own token-free
        // EllipsoidTerrainProvider default is left alone) and the explicit OSM baseLayer below
        // means Cesium never falls back to an Ion-backed default imagery layer.
        const viewer = new Cesium.Viewer(container, {
            baseLayer: Cesium.ImageryLayer.fromProviderAsync(
                Cesium.OpenStreetMapImageryProvider.fromUrl('https://tile.openstreetmap.org/')),
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

        // A dark, high-contrast, stylized look reads more "military terminal" than a photoreal
        // basemap - and sidesteps needing any imagery-provider token. Applied once the async
        // baseLayer above actually resolves.
        viewer.imageryLayers.layerAdded.addEventListener(function (layer) {
            layer.brightness = 0.55;
            layer.contrast = 1.35;
            layer.gamma = 0.8;
            layer.hue = 3.4;
            layer.saturation = 0.15;
        });

        viewer.camera.setView({
            destination: Cesium.Cartesian3.fromDegrees(-98.5, 39.8, 18000000)
        });

        const cameraEntities = new Map();
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

        viewers.set(containerId, { viewer, cameraEntities, clickHandler });

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
    }

    // Cesium's default credit container floats over the canvas with its own light-on-dark
    // styling that clashes with the terminal theme - redirect it into the attribution bar this
    // page already renders (see TacticalGlobe.razor's .tg-attribution) instead of hiding it
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

    return { init: init, setCameraPins: setCameraPins, dispose: dispose };
})();
