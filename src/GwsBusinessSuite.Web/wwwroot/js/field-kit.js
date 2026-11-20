// The offline field kit (19-11-26; FieldKitService builds the page). Keeps a copy of this page in
// the browser's cache so the service worker can show it with no signal, and queues notes written
// offline, sending them to Overwatch map notes when there's signal again.
(function () {
    'use strict';
    const body = document.body;
    const area = body.dataset.area;
    const lat = Number(body.dataset.lat);
    const lon = Number(body.dataset.lon);
    const status = document.getElementById('kit-status');
    const list = document.getElementById('kit-queue');
    const form = document.getElementById('kit-note');
    const QUEUE_KEY = 'gws-field-kit-notes';
    // Not "gws-": sw.js clears its own old "gws-" caches when it updates.
    const KIT_CACHE = 'field-kits-v1';

    function say(text) { if (status) status.textContent = text; }
    function queue() {
        try { return JSON.parse(localStorage.getItem(QUEUE_KEY) || '[]'); } catch { return []; }
    }
    function store(items) {
        try { localStorage.setItem(QUEUE_KEY, JSON.stringify(items)); } catch { say('This phone won’t keep notes offline.'); }
    }
    function show() {
        if (!list) return;
        list.textContent = '';
        queue().filter(function (note) { return note.area === area; }).forEach(function (note) {
            const item = document.createElement('li');
            item.textContent = note.title + ' · waiting for signal';
            list.appendChild(item);
        });
    }

    async function keep() {
        if (!('caches' in window) || !navigator.onLine) return;
        try {
            const cache = await caches.open(KIT_CACHE);
            await cache.add(new Request(location.pathname, { credentials: 'same-origin' }));
            await cache.add('/js/field-kit.js');
            say('Kept on this phone for use without signal.');
        } catch {
            say('Couldn’t keep a copy for use without signal.');
        }
    }

    async function send() {
        const waiting = queue();
        if (!waiting.length || !navigator.onLine) return;
        const left = [];
        for (const note of waiting) {
            try {
                const response = await fetch('/admin/api/field-kit/' + encodeURIComponent(note.area) + '/notes', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json', 'X-Field-Kit': '1' },
                    body: JSON.stringify({ title: note.title, body: note.body, latitude: note.lat, longitude: note.lon })
                });
                if (!response.ok) left.push(note);
            } catch {
                left.push(note);
            }
        }
        store(left);
        show();
        const sent = waiting.length - left.length;
        if (sent > 0) say(sent + (sent === 1 ? ' note' : ' notes') + ' added to your Overwatch map notes.');
    }

    if (form) {
        form.addEventListener('submit', function (event) {
            event.preventDefault();
            const title = document.getElementById('kit-title').value.trim();
            const text = document.getElementById('kit-body').value.trim();
            if (!title) return;
            const items = queue();
            items.push({ area: area, title: title, body: text, lat: lat, lon: lon, at: new Date().toISOString() });
            store(items);
            form.reset();
            show();
            say(navigator.onLine ? 'Sending…' : 'Saved on this phone; it’s sent when there’s signal.');
            send();
        });
    }
    window.addEventListener('online', send);
    show();
    keep();
    send();
})();
