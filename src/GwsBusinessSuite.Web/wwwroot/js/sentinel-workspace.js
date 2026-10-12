// This is one of only two global keydown listeners in the app - the other is
// cms-builder-bridge.js's handleKeydown (registered on window, so it runs after this one,
// which is on document). They're only ever mounted on different pages today (Wiki vs. Canvas
// Studio) so there's no live collision, and their key combos don't currently overlap (this
// one: Cmd/Ctrl+Shift+F, Cmd/Ctrl+Shift+D, Cmd/Ctrl+Shift+., Cmd/Ctrl+\, Escape) - but if the two features are ever mounted
// together, re-check both for overlapping bindings before assuming they'll coexist cleanly.
let shortcutHandler = null;
let searchInput = null;

export function initialize(input) {
    dispose();
    searchInput = input;
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
    shortcutHandler = null;
    searchInput = null;
}

// The page title box has no Blazor-rendered value (see Wiki.razor's title textarea): Blazor
// re-applying a value while the user types drops keystrokes over a server round trip. The
// component calls this only when a different page is loaded into the editor.
export function setTitleValue(element, value) {
    if (element && element.value !== value) {
        element.value = value ?? '';
    }
}
