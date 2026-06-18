// CMS Builder — postMessage bridge between Blazor and the React iframe.
// Loaded as a JS module by CmsBuilderEditor.razor.

let _dotNetRef = null;
let _boundHandler = null;

export function init(dotNetRef) {
    _dotNetRef = dotNetRef;
    _boundHandler = handleMessage.bind(null, dotNetRef);
    window.addEventListener('message', _boundHandler);
}

export function dispose() {
    if (_boundHandler) {
        window.removeEventListener('message', _boundHandler);
        _boundHandler = null;
    }
    _dotNetRef = null;
}

export function sendToIframe(message) {
    const iframe = document.getElementById('cms-builder-iframe');
    if (iframe?.contentWindow) {
        iframe.contentWindow.postMessage(message, '*');
    }
}

export function reloadIframe() {
    const iframe = document.getElementById('cms-builder-iframe');
    if (iframe) {
        iframe.src = iframe.src;
    }
}

function handleMessage(dotNetRef, event) {
    const data = event.data;
    if (!data || typeof data !== 'object') return;

    switch (data.type) {
        case 'cms:select':
            dotNetRef.invokeMethodAsync('OnWidgetSelectedFromIframe', data.sectionId ?? '', data.widgetId ?? '');
            break;
        case 'cms:select-section':
            dotNetRef.invokeMethodAsync('OnSectionSelectedFromIframe', data.sectionId ?? '');
            break;
        case 'cms:ready':
            // iframe signals it is in edit mode and ready; send current selection if any
            dotNetRef.invokeMethodAsync('OnIframeReady');
            break;
    }
}
