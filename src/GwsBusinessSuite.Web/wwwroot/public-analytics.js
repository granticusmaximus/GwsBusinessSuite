(() => {
    "use strict";

    if (window.location.pathname.startsWith("/admin")
        || navigator.doNotTrack === "1"
        || navigator.globalPrivacyControl === true) {
        return;
    }

    const key = name => {
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
    const visitorKey = key("gws_analytics_visitor");
    const sessionKey = key("gws_analytics_session");
    const startedAt = performance.now();
    let engagementSent = false;
    const query = new URLSearchParams(window.location.search);

    const payload = (eventName, engagementSeconds = 0) => JSON.stringify({
        eventName,
        visitorKey,
        sessionKey,
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
        fetch("/api/analytics/events", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body,
            credentials: "omit",
            keepalive: true
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
