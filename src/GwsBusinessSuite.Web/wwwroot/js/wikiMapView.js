// Real Map view (Phase 5.1) - Leaflet (vendored via CDN, see App.razor) plus client-side
// geocoding against OpenStreetMap's free Nominatim API, since Place properties store plain
// address text (see WikiDatabasePropertyTypes.Place) rather than stored coordinates and this
// app has no paid geocoding service configured. Geocoded coordinates are cached only for the
// life of this browser tab (module-level Map) - not persisted server-side, so re-opening the
// view later re-geocodes. That's a deliberate scope boundary for an internal, low-traffic tool
// rather than adding a new table just to cache lat/lng.
//
// Nominatim's usage policy caps requests at 1/second and asks for a way to identify the calling
// application - browsers can't set a custom User-Agent from fetch(), but they do send Referer
// automatically, which the policy accepts as the alternative.
const geocodeCache = new Map();
let geocodeQueueTail = Promise.resolve();

export async function initialize(container, dotNetRef, markersJson) {
    if (!window.L) {
        container.textContent = 'Map library failed to load.';
        return;
    }

    const markers = JSON.parse(markersJson);
    let map = container.__wikiLeafletMap;
    if (!map) {
        map = window.L.map(container);
        window.L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        }).addTo(map);
        map.setView([39.8283, -98.5795], 4); // Contiguous-US center, a reasonable default before anything is geocoded
        container.__wikiLeafletMap = map;
        container.__wikiLeafletLayer = window.L.layerGroup().addTo(map);
    } else {
        container.__wikiLeafletLayer.clearLayers();
    }

    const layer = container.__wikiLeafletLayer;
    const points = [];
    for (const item of markers) {
        const coords = await geocode(item.place);
        if (!coords) continue;

        const marker = window.L.marker(coords).addTo(layer);
        const popup = document.createElement('div');
        const link = document.createElement('a');
        link.href = '#';
        link.textContent = item.title;
        link.addEventListener('click', event => {
            event.preventDefault();
            dotNetRef.invokeMethodAsync('OpenMapRowAsync', item.id).catch(() => { /* circuit may be gone */ });
        });
        popup.appendChild(link);
        popup.appendChild(document.createElement('br'));
        popup.appendChild(document.createTextNode(item.place));
        marker.bindPopup(popup);
        points.push(coords);
    }

    if (points.length > 0) {
        map.fitBounds(points, { padding: [24, 24], maxZoom: 12 });
    }
}

function geocode(place) {
    if (geocodeCache.has(place)) {
        return Promise.resolve(geocodeCache.get(place));
    }

    // Nominatim allows at most one request per second - a shared promise chain serializes every
    // geocode() call (across all markers in this view) onto the same 1.1s cadence rather than
    // firing them all in a burst.
    const result = geocodeQueueTail.then(async () => {
        try {
            const response = await fetch(
                `https://nominatim.openstreetmap.org/search?format=json&limit=1&q=${encodeURIComponent(place)}`);
            const body = await response.json();
            const coords = body && body[0] ? [Number(body[0].lat), Number(body[0].lon)] : null;
            geocodeCache.set(place, coords);
            return coords;
        } catch {
            geocodeCache.set(place, null);
            return null;
        }
    });
    geocodeQueueTail = result.then(() => new Promise(resolve => setTimeout(resolve, 1100)));
    return result;
}
