// CMS Builder — postMessage bridge between Canvas Studio (CmsBuilderEditor.razor) and the
// live-preview iframe (a real, same-origin /cms/{siteSlug}/{path}?edit=1 page, rendered by
// CmsBlockHtmlRenderer.BuildEditModeScript on the iframe side). Global-object convention,
// matching gwsMarkdownEditor/dragReorder.js rather than an ES module — this app has no
// import-map precedent for JS modules.
window.gwsCmsBuilderBridge = (function () {
    let _dotNetRef = null;
    let _boundHandler = null;

    function iframe() {
        return document.getElementById('cms-builder-iframe');
    }

    function init(dotNetRef) {
        _dotNetRef = dotNetRef;
        _boundHandler = handleMessage;
        window.addEventListener('message', _boundHandler);
        window.addEventListener('keydown', handleKeydown);
    }

    function dispose() {
        if (_boundHandler) {
            window.removeEventListener('message', _boundHandler);
            _boundHandler = null;
        }
        window.removeEventListener('keydown', handleKeydown);
        _dotNetRef = null;
    }

    // Undo/redo shortcuts, listened for on the parent document only (not inside the
    // iframe) - deliberately scoped out while a native text field or contenteditable has
    // focus, so this never fights with the browser's own per-field text-undo while someone
    // is mid-edit; Ctrl+Z there does what it always does in a text field.
    function isEditableFocus() {
        var el = document.activeElement;
        if (!el) return false;
        var tag = el.tagName;
        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
    }

    function handleKeydown(e) {
        if (!_dotNetRef || isEditableFocus()) return;
        var mod = e.ctrlKey || e.metaKey;
        if (!mod) return;
        if (e.key === 'z' && !e.shiftKey) {
            e.preventDefault();
            _dotNetRef.invokeMethodAsync('Undo');
        } else if ((e.key === 'z' && e.shiftKey) || e.key === 'y') {
            e.preventDefault();
            _dotNetRef.invokeMethodAsync('Redo');
        }
    }

    function sendToIframe(message) {
        const el = iframe();
        if (el && el.contentWindow) {
            el.contentWindow.postMessage(message, window.location.origin);
        }
    }

    function reloadIframe() {
        const el = iframe();
        if (el) {
            // eslint-disable-next-line no-self-assign
            el.src = el.src;
        }
    }

    function handleMessage(event) {
        if (event.origin !== window.location.origin) return;
        const data = event.data;
        if (!data || typeof data !== 'object' || !_dotNetRef) return;

        switch (data.type) {
            case 'cms:select':
                _dotNetRef.invokeMethodAsync('OnWidgetSelectedFromIframe', data.sectionId || '', data.widgetId || '');
                break;
            case 'cms:select-section':
                _dotNetRef.invokeMethodAsync('OnSectionSelectedFromIframe', data.sectionId || '');
                break;
            case 'cms:edit':
                _dotNetRef.invokeMethodAsync('OnWidgetPropEditedFromIframe', data.sectionId || '', data.widgetId || '', data.prop || '', data.value || '');
                break;
            case 'cms:drop':
                if (data.widgetId && data.targetWidgetId && _dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnCanvasDropAsync', data.widgetId, data.targetWidgetId, !!data.insertAfter);
                }
                break;
            case 'cms:ready':
                _dotNetRef.invokeMethodAsync('OnIframeReady');
                break;
        }
    }

    return { init, dispose, sendToIframe, reloadIframe };
})();
