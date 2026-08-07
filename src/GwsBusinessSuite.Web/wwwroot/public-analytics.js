(() => {
    "use strict";

    if (window.location.pathname.startsWith("/admin")
        || navigator.doNotTrack === "1"
        || navigator.globalPrivacyControl === true) {
        return;
    }

    const sessionKey = name => {
        try {
            const existing = sessionStorage.getItem(name);
            if (existing) return existing;
            const created = crypto.randomUUID();
            sessionStorage.setItem(name, created);
            return created;
        } catch {
            return crypto.randomUUID();
        }
    };
    const visitorKey = (() => {
        const storageKey = "gws_analytics_visitor_v2";
        const now = Date.now();
        const expiresAt = now + (90 * 24 * 60 * 60 * 1000);
        try {
            const stored = JSON.parse(localStorage.getItem(storageKey) || "null");
            if (stored
                && typeof stored.id === "string"
                && stored.id.length <= 64
                && Number.isFinite(stored.expiresAt)
                && stored.expiresAt > now) {
                localStorage.setItem(storageKey, JSON.stringify({ id: stored.id, expiresAt }));
                return stored.id;
            }
            const id = crypto.randomUUID();
            localStorage.setItem(storageKey, JSON.stringify({ id, expiresAt }));
            return id;
        } catch {
            return sessionKey("gws_analytics_visitor");
        }
    })();
    const visitKey = sessionKey("gws_analytics_session");
    const startedAt = performance.now();
    let engagementSent = false;
    const query = new URLSearchParams(window.location.search);

    const payload = (eventName, engagementSeconds = 0) => JSON.stringify({
        eventName,
        visitorKey,
        sessionKey: visitKey,
        path: window.location.pathname,
        pageTitle: document.title,
        referrer: document.referrer,
        source: query.get("utm_source") || "",
        medium: query.get("utm_medium") || "",
        campaign: query.get("utm_campaign") || "",
        engagementSeconds
    });

    const send = (eventName, engagementSeconds = 0, beacon = false) => {
        const body = payload(eventName, engagementSeconds);
        if (beacon && navigator.sendBeacon) {
            navigator.sendBeacon("/api/analytics/events", new Blob([body], { type: "application/json" }));
            return;
        }
        // fetch() only rejects on a network-level failure, not a non-2xx response - a 429
        // (rate limited) or 5xx would otherwise disappear silently with zero signal, same as
        // an actual network error. console.warn is deliberately the ceiling here: this is a
        // best-effort visitor-facing beacon, not a page feature worth surfacing a UI error for.
        fetch("/api/analytics/events", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body,
            credentials: "omit",
            keepalive: true
        }).then((response) => {
            if (!response.ok) {
                console.warn(`gwsAnalytics: event "${eventName}" was not recorded (HTTP ${response.status}).`);
            }
        }).catch(() => {});
    };

    send("pageview");
    const finish = () => {
        if (engagementSent) return;
        engagementSent = true;
        const seconds = Math.max(0, Math.round((performance.now() - startedAt) / 1000));
        if (seconds > 0) send("engagement", seconds, true);
    };
    addEventListener("pagehide", finish, { once: true });

    window.gwsAnalytics = {
        track(name) {
            if (typeof name === "string" && name.length <= 64) send(name);
        }
    };
})();
