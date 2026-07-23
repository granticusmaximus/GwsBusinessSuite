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
