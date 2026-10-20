// This is one of only two global keydown listeners in the app - the other is
// cms-builder-bridge.js's handleKeydown (registered on window, so it runs after this one,
// which is on document). They're only ever mounted on different pages today (Wiki vs. Canvas
// Studio) so there's no live collision, and their key combos don't currently overlap (this
// one: Cmd/Ctrl+Shift+F, Cmd/Ctrl+Shift+D, Cmd/Ctrl+Shift+., Cmd/Ctrl+\, Escape) - but if the two features are ever mounted
// together, re-check both for overlapping bindings before assuming they'll coexist cleanly.
let shortcutHandler = null;
let searchInput = null;
let treePointerDownHandler = null;
let treePointerMoveHandler = null;
let treePointerUpHandler = null;
let suppressTreeClickHandler = null;
let treeDrag = null;
let workspaceDotNetRef = null;
let suppressNextTreeClick = false;

export function initialize(input, dotNetRef = null) {
    dispose();
    searchInput = input;
    workspaceDotNetRef = dotNetRef;
    shortcutHandler = event => {
        if ((event.metaKey || event.ctrlKey) && event.shiftKey && event.key.toLowerCase() === 'f') {
            event.preventDefault();
            // Let Blazor open the responsive workspace browser before focusing. Calling
            // focus() directly would be ignored when the sidebar is collapsed on mobile.
            document.querySelector('.sentinel-global-search')?.click();
            return;
        }

        // Today's daily note (the sidebar header's Today button).
        if ((event.metaKey || event.ctrlKey) && event.shiftKey && !event.altKey && event.key.toLowerCase() === 'd') {
            event.preventDefault();
            document.querySelector('.sentinel-daily-today')?.click();
            return;
        }

        // Focus mode (the toolbar's focus button). event.code, because Shift turns "." into ">"
        // on most layouts.
        if ((event.metaKey || event.ctrlKey) && event.shiftKey && !event.altKey && event.code === 'Period') {
            event.preventDefault();
            document.querySelector('.sentinel-focus-toggle')?.click();
            return;
        }

        if ((event.metaKey || event.ctrlKey) && event.key === '\\') {
            event.preventDefault();
            document.querySelector('.sentinel-sidebar-shortcut')?.click();
            return;
        }

        if (event.key === 'Escape') {
            // Leave focus mode, unless Escape is closing a menu, popover or dialog first.
            const exitFocus = document.querySelector('.sentinel-focus-exit');
            const somethingOpen = document.querySelector('.wiki-editor-suggestion-menu, .dropdown-menu.show, [role="dialog"], .gws-wiki-preview');
            if (exitFocus && !somethingOpen && !event.defaultPrevented) {
                exitFocus.click();
                return;
            }
            const mobileBrowser = document.querySelector('.sentinel-local-sidebar.is-mobile-open');
            if (mobileBrowser && window.matchMedia('(max-width: 991.98px)').matches) {
                document.querySelector('.sentinel-mobile-nav-toggle')?.click();
            }
        }
    };
    document.addEventListener('keydown', shortcutHandler);
    initializeEmbeddedWebKitTreeDrag();
}

function isEmbeddedWebKit() {
    const ua = navigator.userAgent || '';
    return /AppleWebKit\//.test(ua) && /Macintosh|iPhone|iPad/.test(ua) && !/Safari\/|Chrome\/|Edg\//.test(ua);
}

function initializeEmbeddedWebKitTreeDrag() {
    if (!isEmbeddedWebKit() || !workspaceDotNetRef) return;

    // Mac Catalyst's WKWebView often never produces drop after starting a native drag from a
    // button-like sidebar row. Keep ordinary clicks intact, but use pointer tracking once the
    // mouse moves far enough to be an intentional drag.
    document.querySelectorAll('[data-sentinel-drag-id]').forEach(element => element.draggable = false);
    treePointerDownHandler = event => {
        if (event.button !== 0) return;
        const source = event.target.closest?.('[data-sentinel-drag-id]');
        if (!source || event.target.closest?.('.wiki-tree-actions, .wiki-tree-select, .wiki-tree-toggle')) return;
        treeDrag = {
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            source,
            active: false,
            target: null
        };
    };
    treePointerMoveHandler = event => {
        if (!treeDrag || treeDrag.pointerId !== event.pointerId) return;
        if (!treeDrag.active && Math.hypot(event.clientX - treeDrag.startX, event.clientY - treeDrag.startY) < 6) return;
        treeDrag.active = true;
        event.preventDefault();
        document.querySelector('.sentinel-workspace')?.classList.add('is-tree-dragging');
        const target = document.elementFromPoint(event.clientX, event.clientY)?.closest?.('[data-sentinel-drop-id]') || null;
        if (treeDrag.target !== target) {
            treeDrag.target?.classList.remove('wiki-tree-drag-over');
            target?.classList.add('wiki-tree-drag-over');
            treeDrag.target = target;
        }
    };
    treePointerUpHandler = event => {
        if (!treeDrag || treeDrag.pointerId !== event.pointerId) return;
        const completed = treeDrag.active;
        const source = treeDrag.source;
        const target = treeDrag.target;
        cleanupTreePointerDrag();
        if (!completed || !target) return;
        suppressNextTreeClick = true;
        event.preventDefault();
        try {
            workspaceDotNetRef.invokeMethodAsync(
                'ReparentTreeNodeFromPointer',
                source.dataset.sentinelDragId,
                source.dataset.sentinelDragDatabase === 'true',
                target.dataset.sentinelDropId);
        } catch { /* the Blazor circuit may have disconnected */ }
    };
    suppressTreeClickHandler = event => {
        if (!suppressNextTreeClick) return;
        suppressNextTreeClick = false;
        event.preventDefault();
        event.stopPropagation();
    };
    document.addEventListener('pointerdown', treePointerDownHandler, true);
    document.addEventListener('pointermove', treePointerMoveHandler, { capture: true, passive: false });
    document.addEventListener('pointerup', treePointerUpHandler, true);
    document.addEventListener('pointercancel', treePointerUpHandler, true);
    document.addEventListener('click', suppressTreeClickHandler, true);
}

function cleanupTreePointerDrag() {
    treeDrag?.target?.classList.remove('wiki-tree-drag-over');
    document.querySelector('.sentinel-workspace')?.classList.remove('is-tree-dragging');
    treeDrag = null;
}

export function focusSearch(input) {
    searchInput = input || searchInput;
    searchInput?.focus();
    searchInput?.select();
}

export function dispose() {
    if (shortcutHandler) {
        document.removeEventListener('keydown', shortcutHandler);
    }
    if (treePointerDownHandler) document.removeEventListener('pointerdown', treePointerDownHandler, true);
    if (treePointerMoveHandler) document.removeEventListener('pointermove', treePointerMoveHandler, true);
    if (treePointerUpHandler) {
        document.removeEventListener('pointerup', treePointerUpHandler, true);
        document.removeEventListener('pointercancel', treePointerUpHandler, true);
    }
    if (suppressTreeClickHandler) document.removeEventListener('click', suppressTreeClickHandler, true);
    cleanupTreePointerDrag();
    shortcutHandler = null;
    searchInput = null;
    treePointerDownHandler = null;
    treePointerMoveHandler = null;
    treePointerUpHandler = null;
    suppressTreeClickHandler = null;
    workspaceDotNetRef = null;
    suppressNextTreeClick = false;
}

// The page title box has no Blazor-rendered value (see Wiki.razor's title textarea): Blazor
// re-applying a value while the user types drops keystrokes over a server round trip. The
// component calls this only when a different page is loaded into the editor.
export function setTitleValue(element, value) {
    if (element && element.value !== value) {
        element.value = value ?? '';
    }
}
