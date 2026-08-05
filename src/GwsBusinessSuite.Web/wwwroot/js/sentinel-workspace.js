// This is one of only two global keydown listeners in the app - the other is
// cms-builder-bridge.js's handleKeydown (registered on window, so it runs after this one,
// which is on document). They're only ever mounted on different pages today (Wiki vs. Canvas
// Studio) so there's no live collision, and their key combos don't currently overlap (this
// one: Cmd/Ctrl+Shift+F, Cmd/Ctrl+\, Escape) - but if the two features are ever mounted
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

        if ((event.metaKey || event.ctrlKey) && event.key === '\\') {
            event.preventDefault();
            document.querySelector('.sentinel-sidebar-shortcut')?.click();
            return;
        }

        if (event.key === 'Escape') {
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
