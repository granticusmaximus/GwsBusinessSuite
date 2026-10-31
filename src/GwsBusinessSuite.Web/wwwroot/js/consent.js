// Privacy choices on the public site (31-10-26). Two things here need a visitor's OK where the
// law asks for one: the visit statistics (public-analytics.js keeps a visitor id in this browser)
// and CJ's affiliate script (a third party's cookies). Everything else the site stores is needed
// for what the visitor is doing (form protection, a share's password, a roadmap vote, the theme).
//
// The choice is kept in this browser. Until it's made, neither runs. Visitors in places that
// need an opt-in (EU/EEA, UK, Switzerland - or anywhere the server can't tell) see the banner;
// elsewhere both are on, as before, and "Privacy choices" (any [data-gws-consent-open]) changes it.
// window.gwsConsent and a "gws-consent" event tell public-analytics.js what it may do.
(() => {
    "use strict";
    const storageKey = "gws_consent_v1";
    const affiliateScript = document.body && document.body.dataset.gwsAffiliateScript;
    let banner = null;

    const read = () => {
        try {
            const stored = JSON.parse(localStorage.getItem(storageKey) || "null");
            return stored && typeof stored.analytics === "boolean" && typeof stored.affiliate === "boolean" ? stored : null;
        } catch {
            return null;
        }
    };

    const loadAffiliate = () => {
        if (!affiliateScript || document.querySelector("script[data-gws-affiliate]")) return;
        const script = document.createElement("script");
        script.src = affiliateScript;
        script.dataset.gwsAffiliate = "1";
        document.body.appendChild(script);
    };

    const apply = (choice, remember) => {
        if (remember) {
            try {
                localStorage.setItem(storageKey, JSON.stringify({ analytics: choice.analytics, affiliate: choice.affiliate, at: Date.now() }));
            } catch { /* private mode: it holds for this page */ }
        }
        window.gwsConsent = { analytics: choice.analytics, affiliate: choice.affiliate, decided: true };
        if (choice.affiliate) loadAffiliate();
        window.dispatchEvent(new CustomEvent("gws-consent", { detail: window.gwsConsent }));
    };

    const close = () => {
        if (banner) banner.remove();
        banner = null;
    };

    const open = (current) => {
        close();
        banner = document.createElement("section");
        banner.className = "gws-consent";
        banner.setAttribute("role", "region");
        banner.setAttribute("aria-label", "Privacy choices");
        banner.innerHTML =
            '<p class="gws-consent-text"><strong>Privacy choices.</strong> This site keeps what forms and your settings need. ' +
            'With your OK it also counts visits (in this browser, for this site only) and lets affiliate partners see when a link here sends you to them.</p>' +
            '<div class="gws-consent-options" hidden>' +
            '<label><input type="checkbox" data-choice="analytics"> Visit statistics</label>' +
            '<label><input type="checkbox" data-choice="affiliate"> Affiliate partners (CJ)</label>' +
            '</div>' +
            '<div class="gws-consent-actions">' +
            '<button type="button" data-action="all">Accept all</button>' +
            '<button type="button" data-action="necessary">Necessary only</button>' +
            '<button type="button" data-action="choose">Choose</button>' +
            '</div>';
        const options = banner.querySelector(".gws-consent-options");
        const box = name => banner.querySelector('input[data-choice="' + name + '"]');
        box("analytics").checked = !!(current && current.analytics);
        box("affiliate").checked = !!(current && current.affiliate);
        banner.addEventListener("click", event => {
            const action = event.target instanceof Element ? event.target.getAttribute("data-action") : null;
            if (action === "all") { apply({ analytics: true, affiliate: true }, true); close(); }
            else if (action === "necessary") { apply({ analytics: false, affiliate: false }, true); close(); }
            else if (action === "choose") { options.hidden = false; event.target.setAttribute("data-action", "save"); event.target.textContent = "Save choices"; }
            else if (action === "save") { apply({ analytics: box("analytics").checked, affiliate: box("affiliate").checked }, true); close(); }
        });
        document.body.appendChild(banner);
        const first = banner.querySelector("button");
        if (first) first.focus({ preventScroll: true });
    };

    document.addEventListener("click", event => {
        const link = event.target instanceof Element ? event.target.closest("[data-gws-consent-open], a[href='#privacy-choices']") : null;
        if (!link) return;
        event.preventDefault();
        open(read() || window.gwsConsent);
    });

    const stored = read();
    if (stored) {
        apply(stored, false);
        return;
    }
    // No choice yet: ask only where an opt-in is needed (the server can't tell = ask).
    fetch("/api/consent/region", { credentials: "omit" })
        .then(response => response.ok ? response.json() : { required: true })
        .catch(() => ({ required: true }))
        .then(region => {
            // Global Privacy Control still opts out of the affiliate partner's cookies (US "sharing").
            if (region && region.required === false) apply({ analytics: true, affiliate: navigator.globalPrivacyControl !== true }, false);
            else open(null);
        });
})();
