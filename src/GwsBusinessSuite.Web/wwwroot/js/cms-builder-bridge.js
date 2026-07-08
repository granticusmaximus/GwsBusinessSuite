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
    }

    function dispose() {
        if (_boundHandler) {
            window.removeEventListener('message', _boundHandler);
            _boundHandler = null;
        }
        _dotNetRef = null;
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
            case 'cms:ready':
                _dotNetRef.invokeMethodAsync('OnIframeReady');
                break;
        }
    }

    return { init, dispose, sendToIframe, reloadIframe };
})();
