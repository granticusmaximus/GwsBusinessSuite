// Renders KaTeX/highlight.js/Mermaid output into the markers WikiBlockHtmlRenderer emits
// (wiki-katex-target / wiki-code-hydrate / wiki-mermaid-target) for read-only Sentinel views (currently just
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

    if (window.wikiMermaid) {
        scope.querySelectorAll('.wiki-mermaid-target').forEach(function (el) {
            window.wikiMermaid.renderInto(el, el.getAttribute('data-mermaid') || '');
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

// Diagram blocks (Sentinel item 17): Mermaid, vendored at lib/mermaid (see VERSION.txt) and only
// downloaded the first time a page actually has a diagram - it's a large library. Shared by the
// block editor's live preview and read-only views (hydrated from WikiBlockHtmlRenderer's
// wiki-mermaid-target markers). securityLevel 'strict' has Mermaid sanitise its own SVG output.
window.wikiMermaid = (function () {
    var loading = null;
    var counter = 0;

    function isDarkPage() {
        var match = (getComputedStyle(document.body).backgroundColor || '').match(/\d+(\.\d+)?/g);
        if (!match || match.length < 3) return false;
        var luminance = 0.2126 * match[0] + 0.7152 * match[1] + 0.0722 * match[2];
        return luminance < 128 && (match.length < 4 || Number(match[3]) > 0);
    }

    function load() {
        if (window.mermaid) return Promise.resolve(window.mermaid);
        if (loading) return loading;
        loading = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = (document.baseURI ? new URL('lib/mermaid/mermaid.min.js', document.baseURI).href : '/lib/mermaid/mermaid.min.js');
            script.onload = function () {
                if (!window.mermaid) { reject(new Error('Diagrams are unavailable.')); return; }
                window.mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: isDarkPage() ? 'dark' : 'default' });
                resolve(window.mermaid);
            };
            script.onerror = function () {
                loading = null;
                reject(new Error("The diagram library couldn't load."));
            };
            document.head.appendChild(script);
        });
        return loading;
    }

    function showError(el, message) {
        var box = document.createElement('div');
        box.className = 'wiki-mermaid-error';
        box.textContent = message;
        el.replaceChildren(box);
        el.classList.add('has-error');
    }

    // Draws source into el. A newer call for the same element wins over a slower older one.
    function renderInto(el, source) {
        var token = (el.__wikiMermaidToken || 0) + 1;
        el.__wikiMermaidToken = token;
        source = (source || '').trim();
        if (!source) {
            el.replaceChildren();
            el.classList.remove('has-error');
            return Promise.resolve(false);
        }
        return load().then(function (mermaid) {
            var id = 'wiki-mermaid-' + (++counter);
            return mermaid.render(id, source).then(function (result) {
                if (el.__wikiMermaidToken !== token) return false;
                el.innerHTML = result.svg;
                el.classList.remove('has-error');
                return true;
            }, function (error) {
                // A failed render can leave its scratch element behind in the body.
                var leftover = document.getElementById('d' + id) || document.getElementById(id);
                if (leftover && leftover.parentNode) leftover.parentNode.removeChild(leftover);
                if (el.__wikiMermaidToken !== token) return false;
                var message = String((error && (error.str || error.message)) || error || 'unknown error').split('\n')[0];
                showError(el, 'Diagram error: ' + message);
                return false;
            });
        }, function (error) {
            if (el.__wikiMermaidToken === token) showError(el, error.message);
            return false;
        });
    }

    return { load: load, renderInto: renderInto };
})();
