// Click-through navigation for the Wiki preview pane's resolved [[Page Title]] links
// (see WikiMarkdownHelper.ResolveWikiLinks) - Wiki.razor has no per-page URL route, so
// resolved links render as <a href="wikilink:{id}">, and this delegates clicks on that
// href scheme back into Blazor instead of letting the browser try to navigate there.
window.gwsWikiLinks = (function () {
    let _dotNetRef = null;
    let _boundHandler = null;

    function handleClick(e) {
        const anchor = e.target.closest('a[href^="wikilink:"]');
        if (!anchor || !_dotNetRef) {
            return;
        }

        e.preventDefault();
        const pageId = anchor.getAttribute("href").substring("wikilink:".length);
        _dotNetRef.invokeMethodAsync("NavigateToWikiPageId", pageId);
    }

    function init(dotNetRef) {
        _dotNetRef = dotNetRef;
        _boundHandler = handleClick;
        document.addEventListener("click", _boundHandler);
    }

    function dispose() {
        if (_boundHandler) {
            document.removeEventListener("click", _boundHandler);
            _boundHandler = null;
        }
        _dotNetRef = null;
    }

    return { init, dispose };
})();
