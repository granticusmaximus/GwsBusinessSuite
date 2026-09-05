// Canvas edit-mode behaviour for the CMS page editor's live preview: click-to-select,
// section handles and their toolbar, drag-and-drop, and inline text editing.
//
// This lives in a file rather than an inline <script> because the app's Content-Security-
// Policy is `script-src 'self' https://cdn.jsdelivr.net` - no 'unsafe-inline'. As an inline
// block it was silently blocked in every deployed environment, so the whole canvas was inert
// (clicking a section genuinely did nothing) while working perfectly in any test that served
// the markup without the real CSP header.
//
// Served to the preview iframe by CmsBlockHtmlRenderer.BuildEditModeScript().

(function () {
  var ORIGIN = window.location.origin;
  function send(msg) { window.parent.postMessage(msg, ORIGIN); }

  var drag = null;
  var paletteDragTarget = null;
  var paletteDragTargetKey = '';
  var html5Indicator = null;

  function createIndicator() {
    var indicator = document.createElement('div');
    indicator.style.cssText = 'position:fixed;left:0;top:0;width:0;height:3px;background:#2563eb;z-index:100000;pointer-events:none;display:none;border-radius:2px;box-shadow:0 0 0 1px rgba(255,255,255,0.8);';
    document.body.appendChild(indicator);
    return indicator;
  }

  function clearIndicator(indicator) {
    if (indicator) indicator.style.display = 'none';
  }

  function clearDropHighlights() {
    document.querySelectorAll('.is-drop-target').forEach(function (el) {
      el.classList.remove('is-drop-target');
    });
  }

  function clearDropVisuals(indicator) {
    clearIndicator(indicator);
    clearDropHighlights();
  }

  function getSectionId(el) {
    var sectionEl = el && el.closest ? el.closest('[data-gws-section-id]') : null;
    return sectionEl ? sectionEl.getAttribute('data-gws-section-id') : '';
  }

  function getColumnId(el) {
    var columnEl = el && el.closest ? el.closest('[data-gws-column-id]') : null;
    return columnEl ? columnEl.getAttribute('data-gws-column-id') : '';
  }

  function hasType(dataTransfer, type) {
    return !!(dataTransfer && dataTransfer.types && Array.prototype.indexOf.call(dataTransfer.types, type) >= 0);
  }

  function hasExternalDragType(dataTransfer) {
    return hasType(dataTransfer, 'application/x-gws-widget-type')
      || hasType(dataTransfer, 'application/x-gws-global-block-id');
  }

  function resolveDropTarget(clientX, clientY, draggedWidgetId) {
    var el = document.elementFromPoint(clientX, clientY);
    if (!el || !el.closest) return null;

    var emptyCanvas = el.closest('[data-gws-empty-canvas]');
    if (emptyCanvas) {
      return { mode: 'empty', emptyCanvas: emptyCanvas };
    }

    var widgetEl = el.closest('[data-gws-widget-id]');
    if (widgetEl) {
      var widgetId = widgetEl.getAttribute('data-gws-widget-id');
      if (draggedWidgetId && widgetId === draggedWidgetId) return null;
      var rect = widgetEl.getBoundingClientRect();
      return {
        mode: 'widget',
        widgetId: widgetId,
        sectionId: getSectionId(widgetEl),
        columnId: getColumnId(widgetEl),
        columnEl: widgetEl.closest('[data-gws-column-id]'),
        rect: rect,
        insertAfter: clientY > rect.top + rect.height / 2
      };
    }

    var columnEl = el.closest('[data-gws-column-id]');
    if (columnEl) {
      return {
        mode: 'column',
        sectionId: getSectionId(columnEl),
        columnId: getColumnId(columnEl),
        columnEl: columnEl,
        rect: columnEl.getBoundingClientRect()
      };
    }

    return null;
  }

  function drawDropIndicator(target, indicator) {
    clearDropVisuals(indicator);
    if (!target) return;

    if (target.mode === 'empty') {
      target.emptyCanvas.classList.add('is-drop-target');
      return;
    }

    if (target.mode === 'column' && target.columnEl) {
      target.columnEl.classList.add('is-drop-target');
    }

    if (!indicator || !target.rect) return;

    indicator.style.display = 'block';
    indicator.style.left = target.rect.left + 'px';
    indicator.style.width = target.rect.width + 'px';
    indicator.style.top = (target.mode === 'widget' && !target.insertAfter ? target.rect.top : target.rect.bottom) + 'px';
  }

  function reportExternalDragTarget(target) {
    var payload = {
      type: 'cms:external-drag-target',
      sectionId: target && target.sectionId ? target.sectionId : '',
      columnId: target && target.columnId ? target.columnId : '',
      targetWidgetId: target && target.widgetId ? target.widgetId : '',
      insertAfter: !!(target && target.insertAfter)
    };
    var key = [payload.sectionId, payload.columnId, payload.targetWidgetId, payload.insertAfter].join('|');
    if (key !== paletteDragTargetKey) {
      paletteDragTargetKey = key;
      send(payload);
    }
  }

  document.addEventListener('mousedown', function (e) {
    var handle = e.target.closest('[data-gws-drag-handle-for]');
    if (!handle) return;
    e.preventDefault();
    document.body.style.userSelect = 'none';
    drag = {
      widgetId: handle.getAttribute('data-gws-drag-handle-for'),
      indicator: createIndicator(),
      target: null,
      raf: null,
      pendingEvent: null
    };
  });

  function processDragMove() {
    if (!drag) return;
    drag.raf = null;
    var e = drag.pendingEvent;
    if (!e) return;
    drag.target = resolveDropTarget(e.clientX, e.clientY, drag.widgetId);
    drawDropIndicator(drag.target, drag.indicator);
  }

  document.addEventListener('mousemove', function (e) {
    if (!drag) return;
    drag.pendingEvent = e;
    if (drag.raf) return;
    drag.raf = requestAnimationFrame(processDragMove);
  });

  document.addEventListener('mouseup', function () {
    if (!drag) return;
    var draggedId = drag.widgetId;
    var target = drag.target;
    if (drag.raf) cancelAnimationFrame(drag.raf);
    clearDropVisuals(drag.indicator);
    drag.indicator.remove();
    document.body.style.userSelect = '';
    drag = null;

    if (target && target.mode !== 'empty') {
      send({
        type: 'cms:drop',
        widgetId: draggedId,
        sectionId: target.sectionId || '',
        columnId: target.columnId || '',
        targetWidgetId: target.widgetId || '',
        insertAfter: !!target.insertAfter
      });
    }
  });

  // Freeform Canvas Layout (Phase 4) - a completely separate move/resize gesture from
  // the reorder-drag system above, scoped to widgets inside a Freeform-mode section
  // (.gws-freeform-item never renders the reorder drag-handle, so the mousedown
  // listener above always bails for these via its own `if (!handle) return`, and this
  // one bails for ordinary flow items via `if (!item) return` - the two never fire for
  // the same element). Percentages are computed against the canvas's own bounding rect
  // so they stay meaningful regardless of viewport size.
  var freeformDrag = null;

  function clampPct(value, min, max) { return Math.max(min, Math.min(max, value)); }

  document.addEventListener('mousedown', function (e) {
    var item = e.target.closest('.gws-freeform-item');
    if (!item) return;
    var canvasEl = item.closest('[data-gws-freeform]');
    if (!canvasEl) return;
    if (!e.target.closest('[data-gws-freeform-resize-for]') && e.target.closest('[data-gws-inline-prop]')) return;

    e.preventDefault();
    document.body.style.userSelect = 'none';
    var canvasRect = canvasEl.getBoundingClientRect();
    var itemRect = item.getBoundingClientRect();
    freeformDrag = {
      widgetId: item.getAttribute('data-gws-widget-id'),
      sectionId: getSectionId(item),
      mode: e.target.closest('[data-gws-freeform-resize-for]') ? 'resize' : 'move',
      item: item,
      canvasRect: canvasRect,
      startClientX: e.clientX,
      startClientY: e.clientY,
      startLeftPct: ((itemRect.left - canvasRect.left) / canvasRect.width) * 100,
      startTopPct: ((itemRect.top - canvasRect.top) / canvasRect.height) * 100,
      startWidthPct: (itemRect.width / canvasRect.width) * 100,
      startHeightPct: (itemRect.height / canvasRect.height) * 100,
      moved: false
    };
  });

  document.addEventListener('mousemove', function (e) {
    if (!freeformDrag) return;
    var d = freeformDrag;
    var dxPct = ((e.clientX - d.startClientX) / d.canvasRect.width) * 100;
    var dyPct = ((e.clientY - d.startClientY) / d.canvasRect.height) * 100;
    if (Math.abs(dxPct) > 0.2 || Math.abs(dyPct) > 0.2) d.moved = true;

    if (d.mode === 'move') {
      d.resultLeft = clampPct(d.startLeftPct + dxPct, 0, 100 - d.startWidthPct);
      d.resultTop = clampPct(d.startTopPct + dyPct, 0, 100 - d.startHeightPct);
      d.item.style.left = d.resultLeft + '%';
      d.item.style.top = d.resultTop + '%';
    } else {
      d.resultWidth = clampPct(d.startWidthPct + dxPct, 5, 100 - d.startLeftPct);
      d.resultHeight = clampPct(d.startHeightPct + dyPct, 5, 100 - d.startTopPct);
      d.item.style.width = d.resultWidth + '%';
      d.item.style.height = d.resultHeight + '%';
    }
  });

  document.addEventListener('mouseup', function () {
    if (!freeformDrag) return;
    var d = freeformDrag;
    freeformDrag = null;
    document.body.style.userSelect = '';
    if (!d.moved) return;

    send({
      type: 'cms:freeform-update',
      sectionId: d.sectionId,
      widgetId: d.widgetId,
      x: d.resultLeft !== undefined ? d.resultLeft : d.startLeftPct,
      y: d.resultTop !== undefined ? d.resultTop : d.startTopPct,
      width: d.resultWidth !== undefined ? d.resultWidth : d.startWidthPct,
      height: d.resultHeight !== undefined ? d.resultHeight : d.startHeightPct
    });
  });

  document.addEventListener('dragover', function (e) {
    if (!hasExternalDragType(e.dataTransfer)) return;
    var target = resolveDropTarget(e.clientX, e.clientY, null);
    if (!target) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
    if (!html5Indicator) html5Indicator = createIndicator();
    paletteDragTarget = target;
    reportExternalDragTarget(target);
    drawDropIndicator(target, html5Indicator);
  }, true);

  document.addEventListener('drop', function (e) {
    if (!hasExternalDragType(e.dataTransfer)) return;
    e.preventDefault();
    var widgetType = e.dataTransfer.getData('application/x-gws-widget-type') || e.dataTransfer.getData('text/plain');
    var globalBlockId = e.dataTransfer.getData('application/x-gws-global-block-id');
    var target = resolveDropTarget(e.clientX, e.clientY, null) || paletteDragTarget;
    clearDropVisuals(html5Indicator);
    paletteDragTarget = null;
    paletteDragTargetKey = '';
    if (globalBlockId) {
      send({
        type: 'cms:insert-global',
        globalBlockId: globalBlockId,
        sectionId: target && target.sectionId ? target.sectionId : '',
        columnId: target && target.columnId ? target.columnId : '',
        targetWidgetId: target && target.widgetId ? target.widgetId : '',
        insertAfter: !!(target && target.insertAfter)
      });
      send({ type: 'cms:external-drag-committed' });
      return;
    }
    if (!widgetType) return;
    send({
      type: 'cms:insert-widget',
      widgetType: widgetType,
      sectionId: target && target.sectionId ? target.sectionId : '',
      columnId: target && target.columnId ? target.columnId : '',
      targetWidgetId: target && target.widgetId ? target.widgetId : '',
      insertAfter: !!(target && target.insertAfter)
    });
    send({ type: 'cms:external-drag-committed' });
  }, true);

  function findDirectStyleWrapper(container) {
    return Array.prototype.find.call(container.children, function (child) {
      return child.classList && child.classList.contains('gws-widget-style');
    }) || null;
  }

  function applyWidgetStyle(widgetId, inlineStyle, hasAnyOverride) {
    var container = document.querySelector('[data-gws-widget-id="' + widgetId + '"]');
    if (!container) return;

    var wrapper = findDirectStyleWrapper(container);
    if (hasAnyOverride) {
      if (!wrapper) {
        wrapper = document.createElement('div');
        wrapper.className = 'gws-widget-style';
        Array.prototype.slice.call(container.childNodes).forEach(function (node) {
          if (node.nodeType === 1 && node.hasAttribute && node.hasAttribute('data-gws-drag-handle-for')) return;
          wrapper.appendChild(node);
        });
        container.appendChild(wrapper);
      }
      wrapper.setAttribute('style', inlineStyle || '');
      return;
    }

    if (wrapper) {
      while (wrapper.firstChild) {
        container.insertBefore(wrapper.firstChild, wrapper);
      }
      wrapper.remove();
    }
  }

  function applySectionClass(sectionId, cssClass) {
    var section = document.querySelector('[data-gws-section-id="' + sectionId + '"]');
    if (section) {
      section.className = cssClass || 'gws-section';
    }
  }

  function highlight(widgetId) {
    var prev = document.querySelector('.gws-editor-selected');
    if (prev) prev.classList.remove('gws-editor-selected');
    if (widgetId) {
      var el = document.querySelector('[data-gws-widget-id="' + widgetId + '"]');
      if (el) el.classList.add('gws-editor-selected');
    }
  }

  var sectionToolbar = null;

  function removeSectionToolbar() {
    if (sectionToolbar && sectionToolbar.parentNode) {
      sectionToolbar.parentNode.removeChild(sectionToolbar);
    }
    sectionToolbar = null;
  }

  // Controls live on the canvas, anchored to the section you just clicked, rather than
  // only in a side panel - selecting something should offer its actions where you are
  // looking. Mirrors the Add Block control Squarespace anchors to a selected section.
  function buildSectionToolbar(sectionEl, sectionId) {
    removeSectionToolbar();
    var bar = document.createElement('div');
    bar.className = 'gws-section-toolbar';
    bar.setAttribute('data-gws-toolbar', '1');

    [
      { command: 'add',       label: '+ Add block', cls: 'is-primary' },
      { command: 'duplicate', label: 'Duplicate',   cls: '' },
      { command: 'move-up',   label: '\u2191',      cls: '' },
      { command: 'move-down', label: '\u2193',      cls: '' },
      { command: 'delete',    label: 'Delete',      cls: 'is-danger' }
    ].forEach(function (action) {
      var button = document.createElement('button');
      button.type = 'button';
      button.className = action.cls;
      button.textContent = action.label;
      button.addEventListener('click', function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        send({ type: 'cms:section-command', sectionId: sectionId, command: action.command });
      }, true);
      bar.appendChild(button);
    });

    // The section is the offset parent so the bar tracks it through scrolling and
    // reflow without any position bookkeeping.
    var previousPosition = window.getComputedStyle(sectionEl).position;
    if (previousPosition === 'static') sectionEl.style.position = 'relative';
    bar.style.top = '0px';
    bar.style.right = '0px';
    sectionEl.appendChild(bar);
    sectionToolbar = bar;
  }

  function highlightSection(sectionId) {
    var prev = document.querySelector('.gws-section-selected');
    if (prev) prev.classList.remove('gws-section-selected');
    removeSectionToolbar();
    if (!sectionId) return;
    var el = document.querySelector('[data-gws-section-id="' + sectionId + '"]');
    if (!el) return;
    el.classList.add('gws-section-selected');
    buildSectionToolbar(el, sectionId);
  }

  var formatBar = null;

  function ensureFormatBar() {
    if (formatBar) return formatBar;
    formatBar = document.createElement('div');
    formatBar.className = 'gws-format-bar';
    formatBar.setAttribute('data-gws-toolbar', '1');
    [
      { cmd: 'bold',   label: 'B', style: 'font-weight:700' },
      { cmd: 'italic', label: 'I', style: 'font-style:italic' },
      { cmd: 'link',   label: '\u{1F517}', style: '' }
    ].forEach(function (action) {
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = action.label;
      if (action.style) b.setAttribute('style', action.style);
      b.setAttribute('data-gws-format', action.cmd);
      // mousedown, not click: click would land after the contenteditable had already
      // lost focus and the selection with it.
      b.addEventListener('mousedown', function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        applyFormat(action.cmd);
      }, true);
      formatBar.appendChild(b);
    });
    document.body.appendChild(formatBar);
    return formatBar;
  }

  function applyFormat(cmd) {
    if (cmd === 'link') {
      var url = window.prompt('Link URL');
      if (url === null) return;
      document.execCommand(url ? 'createLink' : 'unlink', false, url || undefined);
    } else {
      document.execCommand(cmd, false);
    }
    syncFormatBarState();
  }

  function syncFormatBarState() {
    if (!formatBar) return;
    formatBar.querySelectorAll('[data-gws-format]').forEach(function (b) {
      var cmd = b.getAttribute('data-gws-format');
      var on = cmd !== 'link' && document.queryCommandState && document.queryCommandState(cmd);
      b.classList.toggle('is-active', !!on);
    });
  }

  function hideFormatBar() {
    if (formatBar) formatBar.classList.remove('is-open');
  }

  function updateFormatBar() {
    var sel = window.getSelection();
    if (!sel || sel.isCollapsed || sel.rangeCount === 0) { hideFormatBar(); return; }
    var node = sel.anchorNode;
    var host = node && (node.nodeType === 1 ? node : node.parentElement);
    host = host && host.closest ? host.closest('[data-gws-inline-rich]') : null;
    if (!host) { hideFormatBar(); return; }

    var rect = sel.getRangeAt(0).getBoundingClientRect();
    if (!rect || (!rect.width && !rect.height)) { hideFormatBar(); return; }
    var bar = ensureFormatBar();
    bar.classList.add('is-open');
    // Measured after it is visible, otherwise offsetWidth is 0 and it sits off-centre.
    var top = rect.top + window.scrollY - bar.offsetHeight - 8;
    var left = rect.left + window.scrollX + (rect.width / 2) - (bar.offsetWidth / 2);
    bar.style.top = Math.max(window.scrollY + 4, top) + 'px';
    bar.style.left = Math.max(4, left) + 'px';
    syncFormatBarState();
  }

  document.addEventListener('selectionchange', updateFormatBar);
  window.addEventListener('scroll', hideFormatBar, true);

  document.addEventListener('click', function (e) {
    // The toolbar lives inside the section it controls, so without this its own
    // buttons would re-trigger a section select underneath them.
    if (e.target.closest('[data-gws-toolbar]')) return;
    var handleEl = e.target.closest('[data-gws-section-handle]');
    var widgetEl = handleEl ? null : e.target.closest('[data-gws-widget-id]');
    var sectionEl = e.target.closest('[data-gws-section-id]');
    if (widgetEl) {
      // Focus/cursor placement for a contenteditable target already happened on
      // mousedown, before this capture-phase click listener runs - preventDefault
      // here only stops a real <a>/<form>'s own default action, it can't undo the
      // focus that's already landed.
      e.preventDefault();
      highlight(widgetEl.getAttribute('data-gws-widget-id'));
      highlightSection(null);
      send({ type: 'cms:select', sectionId: sectionEl ? sectionEl.getAttribute('data-gws-section-id') : '', widgetId: widgetEl.getAttribute('data-gws-widget-id') });
    } else if (sectionEl) {
      e.preventDefault();
      highlight(null);
      highlightSection(sectionEl.getAttribute('data-gws-section-id'));
      send({ type: 'cms:select-section', sectionId: sectionEl.getAttribute('data-gws-section-id') });
    }
  }, true);

  // Single-line fields (heading/paragraph/button label/hero headline+CTAs) commit
  // on Enter instead of inserting a line break, matching how a normal text input
  // behaves - blur() below is what actually sends the edit (see the blur listener).
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      var active = e.target.closest && e.target.closest('[data-gws-inline-prop]');
      if (active) { e.preventDefault(); active.blur(); }
      return;
    }
    if (e.key !== 'Enter') return;
    var el = e.target.closest('[data-gws-inline-prop]');
    // Rich props are multi-paragraph prose - Enter makes a new line there, the way it
    // does in any editor. Only single-line fields commit on Enter.
    if (el && !el.hasAttribute('data-gws-inline-rich')) { e.preventDefault(); el.blur(); }
  }, true);

  // blur doesn't bubble, so this must be a capture-phase listener to observe it via
  // delegation rather than one listener per editable element.
  document.addEventListener('blur', function (e) {
    var el = e.target;
    if (!(el instanceof Element) || !el.hasAttribute('data-gws-inline-prop')) return;
    var widgetEl = el.closest('[data-gws-widget-id]');
    var sectionEl = el.closest('[data-gws-section-id]');
    if (!widgetEl) return;
    var isRich = el.hasAttribute('data-gws-inline-rich');
    send({
      type: 'cms:edit',
      sectionId: sectionEl ? sectionEl.getAttribute('data-gws-section-id') : '',
      widgetId: widgetEl.getAttribute('data-gws-widget-id'),
      prop: el.getAttribute('data-gws-inline-prop'),
      // Rich props round-trip as HTML and are converted to Markdown by the parent;
      // plain props stay plain text.
      value: isRich ? '' : el.innerText,
      html: isRich ? el.innerHTML : '',
      rich: isRich
    });
    hideFormatBar();
  }, true);

  window.addEventListener('message', function (e) {
    if (e.origin !== ORIGIN || !e.data || typeof e.data !== 'object') return;
    if (e.data.type === 'cms:sync-selection') {
      highlight(e.data.widgetId || null);
      highlightSection(e.data.widgetId ? null : (e.data.sectionId || null));
    } else if (e.data.type === 'cms:prop-changed') {
      var el = document.querySelector('[data-gws-widget-id="' + e.data.widgetId + '"] [data-gws-inline-prop="' + e.data.prop + '"], [data-gws-widget-id="' + e.data.widgetId + '"][data-gws-inline-prop="' + e.data.prop + '"]');
      // Don't clobber an in-progress edit - only patch elements the user isn't
      // actively typing in (e.g. the same prop edited from the Inspector instead).
      if (el && document.activeElement !== el) {
        el.innerText = e.data.value;
      }
    } else if (e.data.type === 'cms:style-changed') {
      applyWidgetStyle(e.data.widgetId, e.data.inlineStyle || '', !!e.data.hasAnyOverride);
    } else if (e.data.type === 'cms:section-changed') {
      applySectionClass(e.data.sectionId, e.data.cssClass || '');
    } else if (e.data.type === 'cms:palette-drag-end') {
      clearDropVisuals(html5Indicator);
      paletteDragTarget = null;
      paletteDragTargetKey = '';
    }
  });

  // Readiness flag: this file is loaded with `defer`, so anything waiting on the canvas being
  // wired up (the browser tests, and any future parent-side coordination) needs a signal that
  // does not depend on guessing at timing.
  window.__gwsCanvasReady = true;
  send({ type: 'cms:ready' });
})();
