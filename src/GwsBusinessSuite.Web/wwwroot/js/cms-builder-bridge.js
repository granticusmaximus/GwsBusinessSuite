// CMS Builder — postMessage bridge between Canvas Studio (CmsBuilderEditor.razor) and the
// live-preview iframe (a real, same-origin /cms/{siteSlug}/{path}?edit=1 page, rendered by
// CmsBlockHtmlRenderer.BuildEditModeScript on the iframe side). Global-object convention,
// matching gwsMarkdownEditor/dragReorder.js rather than an ES module — this app has no
// import-map precedent for JS modules.
window.gwsCmsBuilderBridge = (function () {
    let _dotNetRef = null;
    let _boundHandler = null;
    let _boundParentDragOver = null;
    let _externalDrag = null;

    function iframe() {
        return document.getElementById('cms-builder-iframe');
    }

    function init(dotNetRef) {
        _dotNetRef = dotNetRef;
        _boundHandler = handleMessage;
        window.addEventListener('message', _boundHandler);
        window.addEventListener('keydown', handleKeydown);
        _boundParentDragOver = function (event) {
            // Events inside the iframe belong to its document. Receiving dragover here
            // means the pointer left the iframe, so its last target is no longer valid.
            if (_externalDrag && event.target !== iframe()) {
                _externalDrag.target = null;
            }
        };
        document.addEventListener('dragover', _boundParentDragOver, true);
    }

    function dispose() {
        if (_boundHandler) {
            window.removeEventListener('message', _boundHandler);
            _boundHandler = null;
        }
        window.removeEventListener('keydown', handleKeydown);
        if (_boundParentDragOver) {
            document.removeEventListener('dragover', _boundParentDragOver, true);
            _boundParentDragOver = null;
        }
        _externalDrag = null;
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

    function beginPaletteDrag(event) {
        const source = event.currentTarget || event.target;
        const widgetType = source && source.getAttribute
            ? source.getAttribute('data-gws-palette-widget')
            : null;
        if (!widgetType || !event.dataTransfer) {
            return;
        }

        event.dataTransfer.effectAllowed = 'copy';
        event.dataTransfer.dropEffect = 'copy';
        event.dataTransfer.setData('application/x-gws-widget-type', widgetType);
        event.dataTransfer.setData('text/plain', widgetType);
        _externalDrag = { kind: 'widget', id: widgetType, target: null, committed: false };
    }

    function beginGlobalBlockDrag(event) {
        const source = event.currentTarget || event.target;
        const globalBlockId = source && source.getAttribute
            ? source.getAttribute('data-gws-global-block-id')
            : null;
        const globalBlockKind = source && source.getAttribute
            ? source.getAttribute('data-gws-global-block-kind')
            : null;
        if (!globalBlockId || !globalBlockKind || !event.dataTransfer) {
            return;
        }

        event.dataTransfer.effectAllowed = 'copy';
        event.dataTransfer.dropEffect = 'copy';
        event.dataTransfer.setData('application/x-gws-global-block-id', globalBlockId);
        event.dataTransfer.setData('application/x-gws-global-block-kind', globalBlockKind);
        event.dataTransfer.setData('text/plain', globalBlockId);
        _externalDrag = { kind: 'global', id: globalBlockId, target: null, committed: false };
    }

    function endExternalDrag() {
        // Chromium can deliver dragenter/dragover into a same-origin iframe and still omit
        // its drop event when the source is in the parent. Let a normal iframe drop win
        // first; otherwise commit the last iframe-reported target exactly once.
        const completedDrag = _externalDrag;
        window.setTimeout(function () {
            if (completedDrag && !completedDrag.committed && completedDrag.target && _dotNetRef) {
                const target = completedDrag.target;
                if (completedDrag.kind === 'global') {
                    _dotNetRef.invokeMethodAsync('OnCanvasInsertGlobalAsync',
                        completedDrag.id,
                        target.sectionId || '',
                        target.columnId || '',
                        target.targetWidgetId || '',
                        !!target.insertAfter);
                } else {
                    _dotNetRef.invokeMethodAsync('OnCanvasInsertWidgetAsync',
                        completedDrag.id,
                        target.sectionId || '',
                        target.columnId || '',
                        target.targetWidgetId || '',
                        !!target.insertAfter);
                }
                completedDrag.committed = true;
            }
            if (_externalDrag === completedDrag) {
                _externalDrag = null;
            }
            sendToIframe({ type: 'cms:palette-drag-end' });
        }, 50);
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
                if (data.widgetId && _dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnCanvasDropAsync',
                        data.widgetId,
                        data.sectionId || '',
                        data.columnId || '',
                        data.targetWidgetId || '',
                        !!data.insertAfter);
                }
                break;
            case 'cms:insert-widget':
                if (data.widgetType && _dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnCanvasInsertWidgetAsync',
                        data.widgetType,
                        data.sectionId || '',
                        data.columnId || '',
                        data.targetWidgetId || '',
                        !!data.insertAfter);
                }
                break;
            case 'cms:insert-global':
                if (data.globalBlockId && _dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnCanvasInsertGlobalAsync',
                        data.globalBlockId,
                        data.sectionId || '',
                        data.columnId || '',
                        data.targetWidgetId || '',
                        !!data.insertAfter);
                }
                break;
            case 'cms:external-drag-target':
                if (_externalDrag) {
                    _externalDrag.target = {
                        sectionId: data.sectionId || '',
                        columnId: data.columnId || '',
                        targetWidgetId: data.targetWidgetId || '',
                        insertAfter: !!data.insertAfter
                    };
                }
                break;
            case 'cms:external-drag-committed':
                if (_externalDrag) {
                    _externalDrag.committed = true;
                }
                break;
            case 'cms:ready':
                _dotNetRef.invokeMethodAsync('OnIframeReady');
                break;
        }
    }

    return { init, dispose, sendToIframe, reloadIframe, beginPaletteDrag, beginGlobalBlockDrag, endExternalDrag };
})();
