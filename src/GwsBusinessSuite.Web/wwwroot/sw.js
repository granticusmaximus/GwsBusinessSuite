// GWS Business Suite's service worker (PWA, 07-11-26), registered by js/pwa.js for /admin.
//
// The admin app runs on the server (Blazor Server), so it can't work without a connection. This
// makes the app installable and, when a page can't be reached, shows offline.html instead of the
// browser's error. It caches nothing else, so a deploy is never hidden behind stale files.
// It also shows the bell's notifications pushed to this device (Web Push, phase 2: js/push.js
// turns them on, WebPushDispatcher sends them).
'use strict';

const CACHE = 'gws-offline-v1';
const OFFLINE_URL = '/offline.html';
const OFFLINE_FILES = [OFFLINE_URL, '/js/offline-retry.js', '/img/app-icons/icon-192.png'];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE)
            .then((cache) => cache.addAll(OFFLINE_FILES))
            .then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names.filter((name) => name.startsWith('gws-') && name !== CACHE).map((name) => caches.delete(name)));
        // The page's own request starts while the worker wakes up, so pages load no slower.
        if (self.registration.navigationPreload) await self.registration.navigationPreload.enable();
        await self.clients.claim();
    })());
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    if (request.method !== 'GET') return;
    // The offline page's own files: from the network when there is one, else from the cache.
    const url = new URL(request.url);
    if (request.mode !== 'navigate' && url.origin === self.location.origin && OFFLINE_FILES.includes(url.pathname)) {
        event.respondWith(fetch(request).catch(async () => (await caches.match(url.pathname)) || Response.error()));
        return;
    }
    // Otherwise only page loads. Everything else (the Blazor connection, API calls, files,
    // downloads) goes to the network untouched.
    if (request.mode !== 'navigate') return;
    event.respondWith((async () => {
        try {
            const preloaded = await event.preloadResponse;
            if (preloaded) return preloaded;
            return await fetch(request);
        } catch {
            const offline = await caches.match(OFFLINE_URL);
            return offline || Response.error();
        }
    })());
});

// A push from the server (WebPushService.Payload): { title, body, url, tag }. Every push shows a
// notification: browsers (and iOS especially) take away push from sites whose pushes show nothing.
self.addEventListener('push', (event) => {
    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch {
        data = { body: event.data ? event.data.text() : '' };
    }
    const options = {
        body: data.body || '',
        icon: '/img/app-icons/icon-192.png',
        badge: '/img/app-icons/icon-maskable-192.png',
        data: { url: typeof data.url === 'string' && data.url.startsWith('/admin') ? data.url : '/admin' }
    };
    if (data.tag) {
        // A newer push about the same thing replaces the old one, and still alerts.
        options.tag = data.tag;
        options.renotify = true;
    }
    event.waitUntil(self.registration.showNotification(data.title || 'GWS Business Suite', options));
});

// Tapping a notification: the app's window if it's open (on that page), else a new one.
self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const target = new URL(event.notification.data && event.notification.data.url || '/admin', self.location.origin).href;
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const open = windows.find((client) => new URL(client.url).pathname.toLowerCase().startsWith('/admin'));
        if (open) {
            await open.focus();
            if (open.url !== target && 'navigate' in open) await open.navigate(target).catch(() => undefined);
            return;
        }
        await self.clients.openWindow(target);
    })());
});

// The browser replaced this device's subscription: tell the server the new one (signed in, the
// sign-in cookie goes with it).
self.addEventListener('pushsubscriptionchange', (event) => {
    event.waitUntil((async () => {
        const subscription = event.newSubscription || await self.registration.pushManager.subscribe(event.oldSubscription.options);
        const json = subscription.toJSON();
        await fetch('/admin/api/push/subscriptions', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json', 'X-GWS-Push': '1' },
            body: JSON.stringify({ endpoint: json.endpoint, keys: json.keys })
        });
    })().catch(() => undefined));
});
