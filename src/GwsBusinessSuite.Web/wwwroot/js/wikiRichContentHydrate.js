// Renders KaTeX/highlight.js output into the markers WikiBlockHtmlRenderer emits
// (wiki-katex-target / wiki-code-hydrate) for read-only Sentinel views (currently just
// SentinelPublicShare.razor - the live block editor renders its own equivalent previews
// directly in wiki-block-editor.js). A plain global function rather than an ES module since
// it's invoked straight from Blazor via IJSRuntime.InvokeVoidAsync, matching the other
// non-module scripts already loaded from App.razor (civicWatchVideo.js, dragReorder.js, ...).
window.wikiRichContentHydrate = function (root) {
    var scope = (typeof root === 'string' ? document.querySelector(root) : root) || document;

    if (window.katex) {
        scope.querySelectorAll('.wiki-katex-target').forEach(function (el) {
            var latex = el.getAttribute('data-latex') || '';
            if (!latex.trim()) return;
            try {
                window.katex.render(latex, el, { throwOnError: false, displayMode: true });
            } catch {
                // Leave the HTML-encoded raw LaTeX already in the element as the fallback.
            }
        });
    }

    if (window.hljs) {
        scope.querySelectorAll('code.wiki-code-hydrate').forEach(function (el) {
            try {
                window.hljs.highlightElement(el);
            } catch {
                // Leave the HTML-encoded raw text already in the element as the fallback.
            }
        });
    }
};
