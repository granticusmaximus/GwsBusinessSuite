// Click-through navigation for the Wiki preview pane's resolved [[Page Title]] links
// (see WikiMarkdownHelper.ResolveWikiLinks) - Wiki.razor has no per-page URL route, so
// resolved links render as <a href="wikilink:{id}">, and this delegates clicks on that
// href scheme back into Blazor instead of letting the browser try to navigate there.
window.gwsWikiLinks = (function () {
    let _dotNetRef = null;
    let _boundHandler = null;

    function handleClick(e) {
        const mention = e.target.closest('a[href^="usermention:"], a[href^="datemention:"]');
        if (mention) {
            e.preventDefault();
            return;
        }
        const anchor = e.target.closest('a[href^="wikilink:"]');
        if (!anchor || !_dotNetRef) {
            return;
        }

        e.preventDefault();
        hidePreview();
        const pageId = anchor.getAttribute("href").substring("wikilink:".length);
        _dotNetRef.invokeMethodAsync("NavigateToWikiPageId", pageId);
    }

    // ---- Hover previews (Sentinel item 14) ----
    // Resting on a page link (a [[link]], a page mention or a linked page card) for ~400 ms shows
    // a read-only popover with the page's first blocks. The page asks the server
    // (GetWikiPagePreview), which applies the same View check as opening the page and answers
    // null otherwise - so nothing shows for a page the user can't open. Every string goes in via
    // textContent, never innerHTML.
    const SHOW_DELAY_MS = 400;
    const HIDE_DELAY_MS = 200;
    const CACHE_MS = 30000;
    const _cache = new Map();
    let _popover = null;
    let _showTimer = null;
    let _hideTimer = null;
    let _currentAnchor = null;
    let _requestId = 0;

    function previewTarget(element) {
        const anchor = element && element.closest ? element.closest('a[href^="wikilink:"]') : null;
        if (!anchor || anchor.closest(".gws-wiki-preview")) return null;
        return anchor;
    }

    function onPointerOver(e) {
        if (e.pointerType && e.pointerType !== "mouse") return;
        if (e.buttons) return; // selecting text or dragging
        if (_popover && _popover.contains(e.target)) {
            clearTimeout(_hideTimer);
            return;
        }
        const anchor = previewTarget(e.target);
        if (!anchor) return;
        if (anchor === _currentAnchor) {
            clearTimeout(_hideTimer);
            return;
        }
        scheduleShow(anchor);
    }

    function onPointerOut(e) {
        const leaving = previewTarget(e.target) || (_popover && _popover.contains(e.target) ? _popover : null);
        if (!leaving) return;
        const to = e.relatedTarget;
        if (to && ((_popover && _popover.contains(to)) || (_currentAnchor && _currentAnchor.contains(to)))) return;
        if (leaving !== _popover && leaving !== _currentAnchor) {
            clearTimeout(_showTimer);
            return;
        }
        scheduleHide();
    }

    function onFocusIn(e) {
        const anchor = previewTarget(e.target);
        if (anchor) scheduleShow(anchor);
    }

    function onFocusOut(e) {
        if (previewTarget(e.target)) scheduleHide();
    }

    function onKeyDown(e) {
        if (e.key === "Escape" && _popover) hidePreview();
    }

    function scheduleShow(anchor) {
        clearTimeout(_showTimer);
        clearTimeout(_hideTimer);
        _showTimer = setTimeout(function () { showPreview(anchor); }, SHOW_DELAY_MS);
    }

    function scheduleHide() {
        clearTimeout(_showTimer);
        clearTimeout(_hideTimer);
        _hideTimer = setTimeout(hidePreview, HIDE_DELAY_MS);
    }

    async function showPreview(anchor) {
        if (!_dotNetRef || !anchor.isConnected) return;
        const pageId = anchor.getAttribute("href").substring("wikilink:".length);
        const requestId = ++_requestId;
        let preview;
        const cached = _cache.get(pageId);
        if (cached && Date.now() - cached.at < CACHE_MS) {
            preview = cached.preview;
        } else {
            try {
                preview = await _dotNetRef.invokeMethodAsync("GetWikiPagePreview", pageId);
            } catch (error) {
                return; // circuit gone or page closed
            }
            _cache.set(pageId, { at: Date.now(), preview: preview });
        }
        if (requestId !== _requestId || !anchor.isConnected || !preview) return;
        hidePreview();
        _currentAnchor = anchor;
        _popover = buildPopover(preview);
        document.body.appendChild(_popover);
        position(_popover, anchor);
        anchor.setAttribute("aria-describedby", _popover.id);
    }

    function hidePreview() {
        clearTimeout(_showTimer);
        clearTimeout(_hideTimer);
        if (_currentAnchor) _currentAnchor.removeAttribute("aria-describedby");
        if (_popover && _popover.parentNode) _popover.parentNode.removeChild(_popover);
        _popover = null;
        _currentAnchor = null;
    }

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function buildPopover(preview) {
        const pop = el("div", "gws-wiki-preview");
        pop.id = "gws-wiki-preview";
        pop.setAttribute("role", "tooltip");
        const header = el("div", "gws-wiki-preview-header");
        if (preview.icon) header.appendChild(el("span", "gws-wiki-preview-icon", preview.icon));
        const title = el("a", "gws-wiki-preview-title", preview.title);
        title.href = "wikilink:" + preview.id;
        header.appendChild(title);
        if (preview.verified) header.appendChild(el("span", "gws-wiki-preview-verified", "Verified"));
        pop.appendChild(header);

        const body = el("div", "gws-wiki-preview-body");
        let number = 0;
        (preview.lines || []).forEach(function (line) {
            number = line.kind === "number" ? number + 1 : 0;
            let row;
            if (line.kind === "divider") {
                row = el("hr", "gws-wiki-preview-divider");
            } else {
                row = el("div", "gws-wiki-preview-line is-" + line.kind);
                if (line.kind === "bullet") row.appendChild(el("span", "gws-wiki-preview-marker", "\u2022"));
                if (line.kind === "number") row.appendChild(el("span", "gws-wiki-preview-marker", number + "."));
                if (line.kind === "todo") row.appendChild(el("span", "gws-wiki-preview-marker", line.checked ? "\u2611" : "\u2610"));
                row.appendChild(el("span", "", line.text));
                if (line.indent) row.style.paddingLeft = (line.indent * 1.1) + "rem";
            }
            body.appendChild(row);
        });
        if ((preview.lines || []).length === 0) body.appendChild(el("div", "gws-wiki-preview-empty", "This page is empty."));
        pop.appendChild(body);

        const footer = el("div", "gws-wiki-preview-footer",
            (preview.more > 0 ? preview.more + " more block" + (preview.more === 1 ? "" : "s") + " \u00b7 " : "") + "Updated " + preview.updated);
        pop.appendChild(footer);
        return pop;
    }

    // Below the link, or above when there isn't room; kept inside the window.
    function position(pop, anchor) {
        const rect = anchor.getBoundingClientRect();
        const margin = 8;
        const width = pop.offsetWidth;
        const height = pop.offsetHeight;
        let left = Math.min(Math.max(margin, rect.left), window.innerWidth - width - margin);
        let top = rect.bottom + 6;
        if (top + height > window.innerHeight - margin && rect.top - height - 6 > margin) top = rect.top - height - 6;
        pop.style.left = Math.max(margin, left) + "px";
        pop.style.top = Math.max(margin, top) + "px";
    }

    function init(dotNetRef) {
        _dotNetRef = dotNetRef;
        _boundHandler = handleClick;
        document.addEventListener("click", _boundHandler);
        document.addEventListener("pointerover", onPointerOver);
        document.addEventListener("pointerout", onPointerOut);
        document.addEventListener("focusin", onFocusIn);
        document.addEventListener("focusout", onFocusOut);
        document.addEventListener("keydown", onKeyDown);
        window.addEventListener("scroll", hidePreview, true);
    }

    function dispose() {
        if (_boundHandler) {
            document.removeEventListener("click", _boundHandler);
            _boundHandler = null;
        }
        document.removeEventListener("pointerover", onPointerOver);
        document.removeEventListener("pointerout", onPointerOut);
        document.removeEventListener("focusin", onFocusIn);
        document.removeEventListener("focusout", onFocusOut);
        document.removeEventListener("keydown", onKeyDown);
        window.removeEventListener("scroll", hidePreview, true);
        hidePreview();
        _cache.clear();
        _dotNetRef = null;
    }

    function scrollIntoView(element) {
        if (element) {
            element.scrollIntoView({ behavior: "smooth", block: "start" });
        }
    }

    return { init, dispose, scrollIntoView };
})();
