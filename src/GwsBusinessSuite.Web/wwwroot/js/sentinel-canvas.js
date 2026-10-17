// Sentinel whiteboard (item 10): an infinite canvas of sticky notes, text, shapes, arrows, pen
// strokes, frames and cards for real Sentinel pages, drawn in SVG with no library. Mounted by
// wiki-block-editor.js for `canvas` blocks (editable) and by wikiRichContentHydrate.js for
// read-only views. The board is a plain JSON doc - { v, items, view } - that the server cleans
// and checks on every save (SentinelCanvas.cs). Text is always set with textContent, never HTML.

const SVG_NS = 'http://www.w3.org/2000/svg';
const XHTML_NS = 'http://www.w3.org/1999/xhtml';
const MAX_ITEMS = 2000;
const MAX_TEXT = 5000;
const MAX_UNDO = 100;
const CULL_ABOVE = 500;
const MIN_ZOOM = 0.1;
const MAX_ZOOM = 4;

export const COLORS = ['#fff3a3', '#ffd6a5', '#caffbf', '#a0e7ff', '#d7c8ff', '#ffc8ec', '#ffffff', '#e5e7eb'];

const TOOLS = [
    { key: 'select', label: 'Select', shortcut: 'v', icon: '↖' },
    { key: 'hand', label: 'Pan', shortcut: 'h', icon: '✋' },
    { key: 'sticky', label: 'Sticky note', shortcut: 's', icon: '🗒' },
    { key: 'text', label: 'Text', shortcut: 't', icon: 'T' },
    { key: 'rect', label: 'Rectangle', shortcut: 'r', icon: '▭' },
    { key: 'ellipse', label: 'Ellipse', shortcut: 'o', icon: '◯' },
    { key: 'diamond', label: 'Diamond', shortcut: 'd', icon: '◇' },
    { key: 'connector', label: 'Arrow', shortcut: 'a', icon: '→' },
    { key: 'pen', label: 'Pen', shortcut: 'p', icon: '✎' },
    { key: 'frame', label: 'Frame', shortcut: 'f', icon: '⬚' }
];

const DEFAULT_SIZE = {
    sticky: [200, 160], text: [220, 40], rect: [180, 110], ellipse: [180, 110], diamond: [170, 130],
    frame: [640, 420], card: [260, 170]
};

const TEXT_TYPES = new Set(['sticky', 'text', 'rect', 'ellipse', 'diamond', 'frame']);

export function emptyDoc() {
    return { v: 1, items: [], view: { x: 0, y: 0, zoom: 1 } };
}

export function parseDoc(value) {
    let doc = value;
    if (typeof value === 'string') {
        try { doc = JSON.parse(value || '{}'); } catch { doc = null; }
    }
    if (!doc || typeof doc !== 'object' || !Array.isArray(doc.items)) return emptyDoc();
    const view = doc.view && typeof doc.view === 'object' ? doc.view : {};
    return {
        v: 1,
        items: doc.items.filter(item => item && typeof item === 'object' && typeof item.id === 'string'),
        view: { x: num(view.x, 0), y: num(view.y, 0), zoom: clamp(num(view.zoom, 1), MIN_ZOOM, MAX_ZOOM) }
    };
}

// The board's words in reading order, the same as SentinelCanvas.RichText on the server (which
// rebuilds it on save; this keeps the editor's own copy right between saves).
export function canvasRichText(doc) {
    const spans = [];
    const ordered = [...doc.items].sort((a, b) =>
        (a.type === 'frame' ? 0 : 1) - (b.type === 'frame' ? 0 : 1) || a.y - b.y || a.x - b.x);
    for (const item of ordered) {
        let span = null;
        if (item.type === 'card' && item.pageId) span = { text: item.title || 'Untitled', link: `wikilink:${item.pageId}` };
        else if (typeof item.text === 'string' && item.text.trim()) span = { text: item.text.trim() };
        if (!span) continue;
        if (spans.length) spans.push({ text: '\n' });
        spans.push(span);
    }
    return spans;
}

// options: { doc, readOnly, pageMode, onChange(docJson, richText), pagePreview(id), searchPages(q), openPage(id) }
export function mountCanvas(host, options = {}) {
    const state = {
        host,
        options,
        doc: parseDoc(options.doc),
        readOnly: !!options.readOnly,
        tool: 'select',
        selection: new Set(),
        undo: [],
        redo: [],
        gesture: null,
        editing: null,
        spaceDown: false,
        active: !!options.pageMode,
        previews: new Map(),
        clipboard: null,
        listeners: []
    };
    state.view = { ...state.doc.view };
    build(state);
    render(state);
    if (!state.doc.items.length && !state.readOnly) centerView(state);
    else if (state.readOnly) fit(state);
    return {
        getDoc: () => serialize(state),
        fit: () => fit(state),
        destroy: () => {
            for (const [target, type, handler, opts] of state.listeners) target.removeEventListener(type, handler, opts);
            host.replaceChildren();
        }
    };
}

// ---- DOM ------------------------------------------------------------------------------------

function build(state) {
    const { host } = state;
    host.replaceChildren();
    host.classList.add('sentinel-canvas');
    host.classList.toggle('is-read-only', state.readOnly);
    host.classList.toggle('is-page-mode', !!state.options.pageMode);
    if (!host.hasAttribute('tabindex')) host.tabIndex = 0;
    host.setAttribute('role', 'application');
    host.setAttribute('aria-label', state.readOnly ? 'Whiteboard (read only)' : 'Whiteboard');

    if (!state.readOnly) {
        const toolbar = el('div', 'sentinel-canvas-toolbar');
        toolbar.setAttribute('role', 'toolbar');
        toolbar.setAttribute('aria-label', 'Whiteboard tools');
        for (const tool of TOOLS) {
            const button = el('button', 'sentinel-canvas-tool');
            button.type = 'button';
            button.dataset.tool = tool.key;
            button.title = `${tool.label} (${tool.shortcut.toUpperCase()})`;
            button.setAttribute('aria-label', tool.label);
            button.textContent = tool.icon;
            button.addEventListener('click', () => setTool(state, tool.key));
            toolbar.appendChild(button);
        }
        const card = el('button', 'sentinel-canvas-tool');
        card.type = 'button';
        card.dataset.action = 'card';
        card.title = 'Add a page card';
        card.setAttribute('aria-label', 'Add a page card');
        card.textContent = '📄';
        card.addEventListener('click', () => openPagePicker(state));
        toolbar.appendChild(card);
        host.appendChild(toolbar);
        state.toolbar = toolbar;

        const palette = el('div', 'sentinel-canvas-palette');
        palette.setAttribute('aria-label', 'Colour and order');
        for (const color of COLORS) {
            const swatch = el('button', 'sentinel-canvas-swatch');
            swatch.type = 'button';
            swatch.style.background = color;
            swatch.title = 'Colour';
            swatch.setAttribute('aria-label', `Colour ${color}`);
            swatch.addEventListener('click', () => recolor(state, color));
            palette.appendChild(swatch);
        }
        for (const [label, title, action] of [['⤒', 'Bring to front', () => reorder(state, 1)], ['⤓', 'Send to back', () => reorder(state, -1)], ['🗑', 'Delete', () => removeSelection(state)]]) {
            const button = el('button', 'sentinel-canvas-tool');
            button.type = 'button';
            button.title = title;
            button.setAttribute('aria-label', title);
            button.textContent = label;
            button.addEventListener('click', action);
            palette.appendChild(button);
        }
        host.appendChild(palette);
        state.palette = palette;
    }

    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.classList.add('sentinel-canvas-svg');
    const defs = document.createElementNS(SVG_NS, 'defs');
    const marker = document.createElementNS(SVG_NS, 'marker');
    marker.setAttribute('id', state.markerId = `sc-arrow-${Math.random().toString(36).slice(2, 9)}`);
    for (const [name, value] of [['viewBox', '0 0 10 10'], ['refX', '9'], ['refY', '5'], ['markerWidth', '7'], ['markerHeight', '7'], ['orient', 'auto-start-reverse']]) marker.setAttribute(name, value);
    const tip = document.createElementNS(SVG_NS, 'path');
    tip.setAttribute('d', 'M0,0 L10,5 L0,10 z');
    tip.setAttribute('class', 'sc-arrowhead');
    marker.appendChild(tip);
    defs.appendChild(marker);
    svg.appendChild(defs);
    state.world = document.createElementNS(SVG_NS, 'g');
    svg.appendChild(state.world);
    state.overlay = document.createElementNS(SVG_NS, 'g');
    svg.appendChild(state.overlay);
    host.appendChild(svg);
    state.svg = svg;

    const zoom = el('div', 'sentinel-canvas-zoom');
    for (const [label, title, action] of [['−', 'Zoom out', () => zoomBy(state, 1 / 1.2)], ['100%', 'Reset zoom', () => setZoom(state, 1)], ['+', 'Zoom in', () => zoomBy(state, 1.2)], ['⤢', 'Zoom to fit', () => fit(state)]]) {
        const button = el('button', 'sentinel-canvas-tool');
        button.type = 'button';
        button.title = title;
        button.setAttribute('aria-label', title);
        button.textContent = label;
        button.addEventListener('click', action);
        if (label === '100%') state.zoomLabel = button;
        zoom.appendChild(button);
    }
    host.appendChild(zoom);

    if (!state.readOnly && !state.doc.items.length) {
        const hint = el('div', 'sentinel-canvas-hint');
        hint.textContent = 'Pick a tool and click to add it · double-click to write · drag with Space or the hand to pan · Ctrl/⌘ + scroll to zoom';
        host.appendChild(hint);
        state.hint = hint;
    }

    listen(state, svg, 'pointerdown', event => onPointerDown(state, event));
    listen(state, window, 'pointermove', event => onPointerMove(state, event));
    listen(state, window, 'pointerup', event => onPointerUp(state, event));
    listen(state, svg, 'dblclick', event => onDoubleClick(state, event));
    listen(state, svg, 'wheel', event => onWheel(state, event), { passive: false });
    listen(state, host, 'keydown', event => onKeyDown(state, event));
    listen(state, host, 'keyup', event => {
        if (event.key === ' ') { state.spaceDown = false; host.classList.remove('is-panning'); }
        event.stopPropagation();
    });
    listen(state, document, 'pointerdown', event => { if (!host.contains(event.target)) state.active = !!state.options.pageMode; }, true);
    // The page editor around the board must not treat its keys, clicks or clipboard as text editing.
    for (const type of ['input', 'paste', 'copy', 'cut', 'beforeinput', 'mousedown', 'click', 'focusin']) {
        listen(state, host, type, event => event.stopPropagation());
    }
}

function listen(state, target, type, handler, opts) {
    target.addEventListener(type, handler, opts);
    state.listeners.push([target, type, handler, opts]);
}

function el(tag, className) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
}

function svgEl(tag, attrs = {}, className) {
    const node = document.createElementNS(SVG_NS, tag);
    for (const [name, value] of Object.entries(attrs)) node.setAttribute(name, value);
    if (className) node.setAttribute('class', className);
    return node;
}

// ---- Rendering ------------------------------------------------------------------------------

function render(state) {
    const { world, overlay, view } = state;
    world.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.zoom})`);
    overlay.setAttribute('transform', world.getAttribute('transform'));
    if (state.zoomLabel) state.zoomLabel.textContent = `${Math.round(view.zoom * 100)}%`;
    world.replaceChildren();
    overlay.replaceChildren();

    const byId = new Map(state.doc.items.map(item => [item.id, item]));
    const visible = state.doc.items.length > CULL_ABOVE ? visibleRect(state) : null;
    const ordered = [...state.doc.items].sort((a, b) =>
        layer(a) - layer(b) || (a.z || 0) - (b.z || 0));
    for (const item of ordered) {
        if (visible && item.type !== 'connector' && !intersects(bounds(item), visible)) continue;
        const node = item.type === 'connector' ? renderConnector(state, item, byId) : renderItem(state, item);
        if (node) world.appendChild(node);
    }
    renderSelection(state);
    if (state.gesture?.kind === 'marquee') {
        const r = normalizeRect(state.gesture.start, state.gesture.current);
        overlay.appendChild(svgEl('rect', { x: r.x, y: r.y, width: r.w, height: r.h }, 'sc-marquee'));
    }
    if (state.toolbar) {
        for (const button of state.toolbar.querySelectorAll('[data-tool]')) {
            button.classList.toggle('is-active', button.dataset.tool === state.tool);
            button.setAttribute('aria-pressed', button.dataset.tool === state.tool ? 'true' : 'false');
        }
    }
    if (state.palette) state.palette.classList.toggle('is-visible', state.selection.size > 0 && !state.editing);
    state.host.classList.toggle('is-hand', state.tool === 'hand');
    if (state.hint && state.doc.items.length) { state.hint.remove(); state.hint = null; }
}

function layer(item) {
    return item.type === 'frame' ? 0 : item.type === 'connector' ? 1 : 2;
}

function renderItem(state, item) {
    const group = svgEl('g', { 'data-id': item.id }, `sc-item sc-${item.type}`);
    const { x, y } = item;
    const w = Math.max(item.w || 0, 1);
    const h = Math.max(item.h || 0, 1);
    switch (item.type) {
        case 'sticky':
            group.appendChild(svgEl('rect', { x, y, width: w, height: h, rx: 4, fill: item.color || COLORS[0] }, 'sc-shape'));
            group.appendChild(label(item, x, y, w, h, 'sc-label sc-label-sticky'));
            break;
        case 'text':
            group.appendChild(svgEl('rect', { x, y, width: w, height: h, fill: 'transparent' }, 'sc-hit'));
            group.appendChild(label(item, x, y, w, h, 'sc-label sc-label-text'));
            break;
        case 'rect':
            group.appendChild(svgEl('rect', { x, y, width: w, height: h, rx: 8, fill: item.color || '#ffffff' }, 'sc-shape'));
            group.appendChild(label(item, x, y, w, h, 'sc-label sc-label-center'));
            break;
        case 'ellipse':
            group.appendChild(svgEl('ellipse', { cx: x + w / 2, cy: y + h / 2, rx: w / 2, ry: h / 2, fill: item.color || '#ffffff' }, 'sc-shape'));
            group.appendChild(label(item, x + w * 0.15, y, w * 0.7, h, 'sc-label sc-label-center'));
            break;
        case 'diamond':
            group.appendChild(svgEl('polygon', { points: `${x + w / 2},${y} ${x + w},${y + h / 2} ${x + w / 2},${y + h} ${x},${y + h / 2}`, fill: item.color || '#ffffff' }, 'sc-shape'));
            group.appendChild(label(item, x + w * 0.2, y + h * 0.2, w * 0.6, h * 0.6, 'sc-label sc-label-center'));
            break;
        case 'frame': {
            group.appendChild(svgEl('rect', { x, y, width: w, height: h, rx: 6, fill: item.color ? `${item.color}55` : 'transparent' }, 'sc-frame-box'));
            const title = svgEl('text', { x: x + 4, y: y - 8 }, 'sc-frame-title');
            title.textContent = item.text || 'Frame';
            group.appendChild(title);
            break;
        }
        case 'pen': {
            const points = Array.isArray(item.points) ? item.points : [];
            const d = points.map((p, i) => `${i ? 'L' : 'M'}${x + p[0]},${y + p[1]}`).join(' ');
            group.appendChild(svgEl('path', { d, fill: 'none', stroke: 'transparent', 'stroke-width': Math.max(12, (item.stroke || 2) + 10) }, 'sc-hit'));
            group.appendChild(svgEl('path', { d, fill: 'none', stroke: item.color && item.color !== '#ffffff' ? item.color : 'currentColor', 'stroke-width': item.stroke || 2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, 'sc-pen-path'));
            break;
        }
        case 'card':
            renderCard(state, group, item, x, y, w, h);
            break;
        default:
            return null;
    }
    return group;
}

function label(item, x, y, w, h, className) {
    const foreign = svgEl('foreignObject', { x, y, width: w, height: h });
    const div = document.createElementNS(XHTML_NS, 'div');
    div.setAttribute('class', className);
    div.textContent = item.text || '';
    foreign.appendChild(div);
    return foreign;
}

function renderCard(state, group, item, x, y, w, h) {
    const preview = state.previews.get(item.pageId);
    if (preview === undefined && state.options.pagePreview) loadPreview(state, item.pageId);
    const restricted = preview === null;
    group.classList.toggle('is-restricted', restricted);
    group.appendChild(svgEl('rect', { x, y, width: w, height: h, rx: 10 }, 'sc-card-box'));
    const foreign = svgEl('foreignObject', { x, y, width: w, height: h });
    const body = document.createElementNS(XHTML_NS, 'div');
    body.setAttribute('class', 'sc-card');
    const head = document.createElementNS(XHTML_NS, 'div');
    head.setAttribute('class', 'sc-card-title');
    head.textContent = restricted
        ? '🔒 No access or deleted page'
        : `${preview?.icon || item.icon || '📄'} ${preview?.title || item.title || 'Linked page'}`;
    body.appendChild(head);
    if (preview && Array.isArray(preview.lines)) {
        for (const line of preview.lines.slice(0, 5)) {
            const row = document.createElementNS(XHTML_NS, 'div');
            row.setAttribute('class', `sc-card-line sc-card-${line.kind || 'text'}`);
            row.textContent = line.text || '';
            body.appendChild(row);
        }
    }
    if (state.options.openPage && !restricted) {
        const open = document.createElementNS(XHTML_NS, 'button');
        open.setAttribute('type', 'button');
        open.setAttribute('class', 'sc-card-open');
        open.textContent = 'Open';
        open.addEventListener('pointerdown', event => event.stopPropagation());
        open.addEventListener('click', event => {
            event.stopPropagation();
            state.options.openPage(item.pageId);
        });
        body.appendChild(open);
    }
    foreign.appendChild(body);
    group.appendChild(foreign);
}

async function loadPreview(state, pageId) {
    state.previews.set(pageId, 'loading');
    let preview = null;
    try { preview = await state.options.pagePreview(pageId); }
    catch { preview = undefined; }
    // undefined = couldn't ask (e.g. the side pane has no preview service): show the stored title.
    state.previews.set(pageId, preview === undefined ? { title: null, lines: [] } : preview);
    render(state);
}

function renderConnector(state, item, byId) {
    const ends = connectorEnds(item, byId);
    if (!ends) return null;
    const group = svgEl('g', { 'data-id': item.id }, 'sc-item sc-connector');
    const d = `M${ends.x1},${ends.y1} L${ends.x2},${ends.y2}`;
    group.appendChild(svgEl('path', { d, stroke: 'transparent', 'stroke-width': 14, fill: 'none' }, 'sc-hit'));
    const line = svgEl('path', { d, fill: 'none', 'stroke-width': 2 }, 'sc-connector-line');
    if (item.color) line.style.stroke = item.color;
    if (item.arrow !== false) line.setAttribute('marker-end', `url(#${state.markerId})`);
    group.appendChild(line);
    if (item.text) {
        const text = svgEl('text', { x: (ends.x1 + ends.x2) / 2, y: (ends.y1 + ends.y2) / 2 - 6, 'text-anchor': 'middle' }, 'sc-connector-label');
        text.textContent = item.text;
        group.appendChild(text);
    }
    return group;
}

// Start/end points: an attached end sits on its item's edge, facing the other end.
export function connectorEnds(item, byId) {
    const from = item.from ? byId.get(item.from) : null;
    const to = item.to ? byId.get(item.to) : null;
    const start = from ? center(from) : { x: item.x, y: item.y };
    const end = to ? center(to) : { x: item.x2, y: item.y2 };
    if (![start.x, start.y, end.x, end.y].every(Number.isFinite)) return null;
    const a = from ? edgePoint(from, end) : start;
    const b = to ? edgePoint(to, start) : end;
    return { x1: a.x, y1: a.y, x2: b.x, y2: b.y };
}

function center(item) {
    return { x: item.x + (item.w || 0) / 2, y: item.y + (item.h || 0) / 2 };
}

function edgePoint(item, toward) {
    const c = center(item);
    const dx = toward.x - c.x;
    const dy = toward.y - c.y;
    if (!dx && !dy) return c;
    const hw = (item.w || 0) / 2 + 4;
    const hh = (item.h || 0) / 2 + 4;
    const scale = 1 / Math.max(Math.abs(dx) / hw, Math.abs(dy) / hh);
    return { x: c.x + dx * scale, y: c.y + dy * scale };
}

function renderSelection(state) {
    if (!state.selection.size || state.readOnly) return;
    const items = selectedItems(state);
    for (const item of items) {
        const b = item.type === 'connector' ? connectorBounds(state, item) : bounds(item);
        if (!b) continue;
        state.overlay.appendChild(svgEl('rect', { x: b.x - 4, y: b.y - 4, width: b.w + 8, height: b.h + 8 }, 'sc-selection'));
    }
    if (items.length === 1 && items[0].type !== 'connector' && items[0].type !== 'pen') {
        const b = bounds(items[0]);
        const size = 10 / state.view.zoom;
        state.overlay.appendChild(svgEl('rect', { x: b.x + b.w - size / 2, y: b.y + b.h - size / 2, width: size, height: size, 'data-handle': 'resize' }, 'sc-handle'));
    }
}

// ---- Geometry -------------------------------------------------------------------------------

function bounds(item) {
    if (item.type === 'pen' && Array.isArray(item.points)) {
        const xs = item.points.map(p => p[0]);
        const ys = item.points.map(p => p[1]);
        const minX = Math.min(...xs), minY = Math.min(...ys);
        return { x: item.x + minX, y: item.y + minY, w: Math.max(...xs) - minX, h: Math.max(...ys) - minY };
    }
    return { x: item.x, y: item.y, w: item.w || 0, h: item.h || 0 };
}

function connectorBounds(state, item) {
    const ends = connectorEnds(item, new Map(state.doc.items.map(i => [i.id, i])));
    return ends ? normalizeRect({ x: ends.x1, y: ends.y1 }, { x: ends.x2, y: ends.y2 }) : null;
}

function normalizeRect(a, b) {
    return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(a.x - b.x), h: Math.abs(a.y - b.y) };
}

function intersects(a, b) {
    return a.x <= b.x + b.w && a.x + a.w >= b.x && a.y <= b.y + b.h && a.y + a.h >= b.y;
}

function contains(outer, inner) {
    return inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;
}

function visibleRect(state) {
    const rect = state.svg.getBoundingClientRect();
    const margin = 200 / state.view.zoom;
    return {
        x: -state.view.x / state.view.zoom - margin,
        y: -state.view.y / state.view.zoom - margin,
        w: rect.width / state.view.zoom + margin * 2,
        h: rect.height / state.view.zoom + margin * 2
    };
}

function toWorld(state, event) {
    const rect = state.svg.getBoundingClientRect();
    return {
        x: (event.clientX - rect.left - state.view.x) / state.view.zoom,
        y: (event.clientY - rect.top - state.view.y) / state.view.zoom
    };
}

function num(value, fallback) {
    return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

// Ramer-Douglas-Peucker: fewer points for the same stroke.
export function simplify(points, tolerance) {
    if (points.length < 3) return points;
    const sq = tolerance * tolerance;
    const keep = new Array(points.length).fill(false);
    keep[0] = keep[points.length - 1] = true;
    const stack = [[0, points.length - 1]];
    while (stack.length) {
        const [first, last] = stack.pop();
        let index = -1, max = sq;
        for (let i = first + 1; i < last; i++) {
            const d = segmentDistanceSq(points[i], points[first], points[last]);
            if (d > max) { index = i; max = d; }
        }
        if (index > 0) {
            keep[index] = true;
            stack.push([first, index], [index, last]);
        }
    }
    return points.filter((_, i) => keep[i]);
}

function segmentDistanceSq(p, a, b) {
    let x = a[0], y = a[1];
    let dx = b[0] - x, dy = b[1] - y;
    if (dx || dy) {
        const t = clamp(((p[0] - x) * dx + (p[1] - y) * dy) / (dx * dx + dy * dy), 0, 1);
        x += dx * t; y += dy * t;
    }
    dx = p[0] - x; dy = p[1] - y;
    return dx * dx + dy * dy;
}

// ---- View -----------------------------------------------------------------------------------

function setZoom(state, zoom, anchor) {
    const rect = state.svg.getBoundingClientRect();
    const point = anchor || { x: rect.width / 2, y: rect.height / 2 };
    const next = clamp(zoom, MIN_ZOOM, MAX_ZOOM);
    const worldX = (point.x - state.view.x) / state.view.zoom;
    const worldY = (point.y - state.view.y) / state.view.zoom;
    state.view.zoom = next;
    state.view.x = point.x - worldX * next;
    state.view.y = point.y - worldY * next;
    render(state);
}

function zoomBy(state, factor, anchor) {
    setZoom(state, state.view.zoom * factor, anchor);
}

function centerView(state) {
    const rect = state.svg.getBoundingClientRect();
    if (!state.view.x && !state.view.y && rect.width) {
        state.view.x = rect.width / 2;
        state.view.y = rect.height / 2;
        render(state);
    }
}

function fit(state) {
    const rect = state.svg.getBoundingClientRect();
    const items = state.doc.items.filter(item => item.type !== 'connector');
    if (!items.length || !rect.width || !rect.height) { render(state); return; }
    const all = items.map(bounds);
    const minX = Math.min(...all.map(b => b.x)), minY = Math.min(...all.map(b => b.y));
    const maxX = Math.max(...all.map(b => b.x + b.w)), maxY = Math.max(...all.map(b => b.y + b.h));
    const pad = 40;
    const zoom = clamp(Math.min((rect.width - pad * 2) / Math.max(maxX - minX, 1), (rect.height - pad * 2) / Math.max(maxY - minY, 1)), MIN_ZOOM, 1.5);
    state.view.zoom = zoom;
    state.view.x = (rect.width - (maxX - minX) * zoom) / 2 - minX * zoom;
    state.view.y = (rect.height - (maxY - minY) * zoom) / 2 - minY * zoom;
    render(state);
}

function onWheel(state, event) {
    // In a page of text the board only takes the wheel once it's been clicked, so scrolling past
    // it still scrolls the page; a full-page whiteboard always takes it.
    if (!state.active && !event.ctrlKey && !event.metaKey) return;
    event.preventDefault();
    const rect = state.svg.getBoundingClientRect();
    if (event.ctrlKey || event.metaKey) {
        zoomBy(state, Math.exp(-event.deltaY * 0.01), { x: event.clientX - rect.left, y: event.clientY - rect.top });
    } else {
        state.view.x -= event.deltaX;
        state.view.y -= event.deltaY;
        render(state);
    }
}

// ---- Pointer --------------------------------------------------------------------------------

function hitItem(state, target) {
    const node = target?.closest?.('[data-id]');
    if (!node || !state.svg.contains(node)) return null;
    return state.doc.items.find(item => item.id === node.getAttribute('data-id')) || null;
}

function onPointerDown(state, event) {
    state.active = true;
    if (state.editing) finishEditing(state);
    state.host.focus({ preventScroll: true });
    event.stopPropagation();
    const point = toWorld(state, event);
    const panning = event.button === 1 || state.spaceDown || state.tool === 'hand' || (state.readOnly && event.button === 0);
    if (panning) {
        state.gesture = { kind: 'pan', startX: event.clientX, startY: event.clientY, viewX: state.view.x, viewY: state.view.y };
        state.host.classList.add('is-panning');
        event.preventDefault();
        return;
    }
    if (event.button !== 0 || state.readOnly) return;
    event.preventDefault();
    const hit = hitItem(state, event.target);

    if (event.target.getAttribute?.('data-handle') === 'resize') {
        const item = selectedItems(state)[0];
        state.gesture = { kind: 'resize', item, before: snapshot(state), start: point, w: item.w, h: item.h };
        return;
    }

    switch (state.tool) {
        case 'select':
            if (hit) {
                if (event.shiftKey) {
                    if (state.selection.has(hit.id)) state.selection.delete(hit.id);
                    else state.selection.add(hit.id);
                } else if (!state.selection.has(hit.id)) {
                    state.selection = new Set([hit.id]);
                }
                const moving = selectedItems(state);
                // Moving a frame takes everything inside it along.
                for (const frame of moving.filter(item => item.type === 'frame')) {
                    for (const inner of state.doc.items) {
                        if (inner !== frame && inner.type !== 'connector' && contains(bounds(frame), bounds(inner)) && !moving.includes(inner)) moving.push(inner);
                    }
                }
                state.gesture = {
                    kind: 'move', start: point, before: snapshot(state), moved: false,
                    items: moving.map(item => ({ item, x: item.x, y: item.y, x2: item.x2, y2: item.y2 }))
                };
            } else {
                if (!event.shiftKey) state.selection.clear();
                state.gesture = { kind: 'marquee', start: point, current: point, base: new Set(state.selection) };
            }
            render(state);
            return;
        case 'connector': {
            const from = hit && hit.type !== 'connector' ? hit : null;
            const item = { id: newId(), type: 'connector', x: point.x, y: point.y, x2: point.x, y2: point.y, w: 0, h: 0, z: nextZ(state), arrow: true };
            if (from) item.from = from.id;
            state.gesture = { kind: 'connect', item, before: snapshot(state) };
            state.doc.items.push(item);
            render(state);
            return;
        }
        case 'pen': {
            const item = { id: newId(), type: 'pen', x: point.x, y: point.y, w: 0, h: 0, z: nextZ(state), points: [[0, 0]], stroke: 2 };
            state.gesture = { kind: 'draw', item, before: snapshot(state) };
            state.doc.items.push(item);
            render(state);
            return;
        }
        default:
            if (DEFAULT_SIZE[state.tool]) {
                const [w, h] = DEFAULT_SIZE[state.tool];
                const item = { id: newId(), type: state.tool, x: point.x, y: point.y, w, h, z: state.tool === 'frame' ? minZ(state) - 1 : nextZ(state), text: '' };
                if (state.tool === 'sticky') item.color = COLORS[0];
                if (state.tool === 'frame') item.text = 'Frame';
                state.gesture = { kind: 'create', item, start: point, before: snapshot(state), dragged: false };
                state.doc.items.push(item);
                render(state);
            }
    }
}

function onPointerMove(state, event) {
    const gesture = state.gesture;
    if (!gesture) return;
    if (gesture.kind === 'pan') {
        state.view.x = gesture.viewX + (event.clientX - gesture.startX);
        state.view.y = gesture.viewY + (event.clientY - gesture.startY);
        render(state);
        return;
    }
    const point = toWorld(state, event);
    switch (gesture.kind) {
        case 'move': {
            const dx = point.x - gesture.start.x, dy = point.y - gesture.start.y;
            if (Math.abs(dx) + Math.abs(dy) > 0) gesture.moved = true;
            for (const entry of gesture.items) {
                entry.item.x = round(entry.x + dx);
                entry.item.y = round(entry.y + dy);
                if (entry.item.type === 'connector') { entry.item.x2 = round(entry.x2 + dx); entry.item.y2 = round(entry.y2 + dy); }
            }
            break;
        }
        case 'resize':
            gesture.item.w = round(Math.max(20, gesture.w + point.x - gesture.start.x));
            gesture.item.h = round(Math.max(20, gesture.h + point.y - gesture.start.y));
            break;
        case 'marquee': {
            gesture.current = point;
            const box = normalizeRect(gesture.start, point);
            state.selection = new Set(gesture.base);
            for (const item of state.doc.items) {
                const b = item.type === 'connector' ? connectorBounds(state, item) : bounds(item);
                if (b && contains(box, b)) state.selection.add(item.id);
            }
            break;
        }
        case 'create': {
            const box = normalizeRect(gesture.start, point);
            if (box.w > 8 || box.h > 8) {
                gesture.dragged = true;
                Object.assign(gesture.item, { x: round(box.x), y: round(box.y), w: round(Math.max(box.w, 20)), h: round(Math.max(box.h, 20)) });
            }
            break;
        }
        case 'connect':
            gesture.item.x2 = round(point.x);
            gesture.item.y2 = round(point.y);
            if (!gesture.item.from) { /* free start stays where it was pressed */ }
            break;
        case 'draw': {
            const item = gesture.item;
            const last = item.points[item.points.length - 1];
            const p = [round(point.x - item.x), round(point.y - item.y)];
            if (Math.hypot(p[0] - last[0], p[1] - last[1]) * state.view.zoom >= 2 && item.points.length < 5000) item.points.push(p);
            break;
        }
    }
    render(state);
}

function onPointerUp(state, event) {
    const gesture = state.gesture;
    if (!gesture) return;
    state.gesture = null;
    state.host.classList.remove('is-panning');
    switch (gesture.kind) {
        case 'pan':
            persistView(state);
            return;
        case 'move':
            if (gesture.moved) commit(state, gesture.before);
            break;
        case 'resize':
            commit(state, gesture.before);
            break;
        case 'create': {
            const item = gesture.item;
            if (!gesture.dragged) { item.x = round(item.x - item.w / 2); item.y = round(item.y - item.h / 2); }
            state.selection = new Set([item.id]);
            setTool(state, 'select', false);
            commit(state, gesture.before);
            if (TEXT_TYPES.has(item.type) && item.type !== 'frame') startEditing(state, item);
            break;
        }
        case 'connect': {
            const item = gesture.item;
            const target = hitItem(state, document.elementFromPoint(event.clientX, event.clientY));
            if (target && target.type !== 'connector' && target.id !== item.from) item.to = target.id;
            const length = Math.hypot(item.x2 - item.x, item.y2 - item.y);
            if (!item.to && length < 10) {
                state.doc.items = state.doc.items.filter(other => other !== item);
                render(state);
                return;
            }
            if (item.from) {
                // An attached start keeps a sensible free position for when its item is deleted.
                const from = state.doc.items.find(other => other.id === item.from);
                if (from) { const c = center(from); item.x = round(c.x); item.y = round(c.y); }
            }
            if (item.to) {
                const to = state.doc.items.find(other => other.id === item.to);
                if (to) { const c = center(to); item.x2 = round(c.x); item.y2 = round(c.y); }
            }
            state.selection = new Set([item.id]);
            commit(state, gesture.before);
            break;
        }
        case 'draw': {
            const item = gesture.item;
            item.points = simplify(item.points, 1.5 / state.view.zoom);
            const b = bounds(item);
            item.w = round(b.w);
            item.h = round(b.h);
            commit(state, gesture.before);
            break;
        }
    }
    render(state);
}

function onDoubleClick(state, event) {
    if (state.readOnly) return;
    event.stopPropagation();
    const hit = hitItem(state, event.target);
    if (hit?.type === 'card') {
        state.options.openPage?.(hit.pageId);
        return;
    }
    if (hit && (TEXT_TYPES.has(hit.type) || hit.type === 'connector')) {
        startEditing(state, hit);
        return;
    }
    if (!hit && state.tool === 'select') {
        const point = toWorld(state, event);
        const before = snapshot(state);
        const item = { id: newId(), type: 'text', x: round(point.x - 110), y: round(point.y - 20), w: 220, h: 40, z: nextZ(state), text: '' };
        state.doc.items.push(item);
        state.selection = new Set([item.id]);
        commit(state, before);
        render(state);
        startEditing(state, item);
    }
}

// ---- Text editing ---------------------------------------------------------------------------

function startEditing(state, item) {
    if (state.readOnly || state.editing) return;
    const rect = state.svg.getBoundingClientRect();
    const hostRect = state.host.getBoundingClientRect();
    const b = item.type === 'connector' ? connectorBounds(state, item) : bounds(item);
    const area = el('textarea', 'sentinel-canvas-editor');
    area.value = item.text || '';
    area.maxLength = MAX_TEXT;
    area.setAttribute('aria-label', 'Text');
    const zoom = state.view.zoom;
    const width = item.type === 'connector' ? 160 : Math.max(b.w * zoom, 80);
    const height = item.type === 'connector' ? 36 : Math.max(b.h * zoom, 32);
    area.style.left = `${rect.left - hostRect.left + state.view.x + (item.type === 'connector' ? (b.x + b.w / 2) * zoom - 80 : b.x * zoom)}px`;
    area.style.top = `${rect.top - hostRect.top + state.view.y + (item.type === 'connector' ? (b.y + b.h / 2) * zoom - 18 : b.y * zoom)}px`;
    area.style.width = `${width}px`;
    area.style.height = `${height}px`;
    area.style.fontSize = `${Math.max(10, 15 * zoom)}px`;
    state.editing = { item, area, before: snapshot(state) };
    area.addEventListener('keydown', event => {
        event.stopPropagation();
        if (event.key === 'Escape' || ((event.metaKey || event.ctrlKey) && event.key === 'Enter')) {
            event.preventDefault();
            finishEditing(state);
            state.host.focus({ preventScroll: true });
        }
    });
    area.addEventListener('blur', () => finishEditing(state));
    state.host.appendChild(area);
    render(state);
    area.focus();
    area.select();
}

function finishEditing(state) {
    const editing = state.editing;
    if (!editing) return;
    state.editing = null;
    const text = editing.area.value.slice(0, MAX_TEXT);
    editing.area.remove();
    if (text !== (editing.item.text || '')) {
        editing.item.text = text;
        // A text item left empty is removed rather than kept as an invisible box.
        if (editing.item.type === 'text' && !text.trim()) {
            state.doc.items = state.doc.items.filter(item => item !== editing.item);
            state.selection.delete(editing.item.id);
        }
        commit(state, editing.before);
    } else if (editing.item.type === 'text' && !text.trim()) {
        state.doc.items = state.doc.items.filter(item => item !== editing.item);
        state.selection.delete(editing.item.id);
        commit(state, editing.before);
    }
    render(state);
}

// ---- Keyboard -------------------------------------------------------------------------------

function onKeyDown(state, event) {
    event.stopPropagation();
    if (state.editing || event.target !== state.host && event.target.tagName === 'INPUT') return;
    const mod = event.metaKey || event.ctrlKey;
    const key = event.key.toLowerCase();
    if (event.key === ' ') {
        state.spaceDown = true;
        state.host.classList.add('is-panning');
        event.preventDefault();
        return;
    }
    if (state.readOnly) return;
    if (mod && key === 'z') { event.preventDefault(); event.shiftKey ? redo(state) : undo(state); return; }
    if (mod && key === 'y') { event.preventDefault(); redo(state); return; }
    if (mod && key === 'a') { event.preventDefault(); state.selection = new Set(state.doc.items.map(item => item.id)); render(state); return; }
    if (mod && key === 'd') { event.preventDefault(); duplicate(state); return; }
    if (mod && key === 'c') { event.preventDefault(); copy(state); return; }
    if (mod && key === 'x') { event.preventDefault(); copy(state); removeSelection(state); return; }
    if (mod && key === 'v') { event.preventDefault(); paste(state); return; }
    if (mod) return;
    if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); removeSelection(state); return; }
    if (event.key === 'Escape') { state.selection.clear(); setTool(state, 'select'); return; }
    if (event.key === 'Enter') {
        const [only] = selectedItems(state);
        if (only && state.selection.size === 1 && (TEXT_TYPES.has(only.type) || only.type === 'connector')) { event.preventDefault(); startEditing(state, only); }
        return;
    }
    if (event.key.startsWith('Arrow') && state.selection.size) {
        event.preventDefault();
        const step = event.shiftKey ? 10 : 1;
        const dx = event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0;
        const dy = event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0;
        const before = snapshot(state);
        for (const item of selectedItems(state)) {
            item.x += dx; item.y += dy;
            if (item.type === 'connector') { item.x2 += dx; item.y2 += dy; }
        }
        commit(state, before);
        render(state);
        return;
    }
    if (event.key === ']') { reorder(state, 1); return; }
    if (event.key === '[') { reorder(state, -1); return; }
    const tool = TOOLS.find(t => t.shortcut === key);
    if (tool) { event.preventDefault(); setTool(state, tool.key); }
}

function setTool(state, tool, rerender = true) {
    state.tool = tool;
    if (tool !== 'select') state.selection.clear();
    state.host.focus({ preventScroll: true });
    if (rerender) render(state);
}

// ---- Edits ----------------------------------------------------------------------------------

function selectedItems(state) {
    return state.doc.items.filter(item => state.selection.has(item.id));
}

function removeSelection(state) {
    if (!state.selection.size) return;
    const before = snapshot(state);
    const removed = new Set(state.selection);
    state.doc.items = state.doc.items.filter(item => !removed.has(item.id));
    // Arrows attached to a removed item keep their last position as a free end.
    for (const item of state.doc.items.filter(i => i.type === 'connector')) {
        if (removed.has(item.from)) delete item.from;
        if (removed.has(item.to)) delete item.to;
    }
    state.selection.clear();
    commit(state, before);
    render(state);
}

function recolor(state, color) {
    if (!state.selection.size) return;
    const before = snapshot(state);
    for (const item of selectedItems(state)) item.color = color;
    commit(state, before);
    render(state);
}

function reorder(state, direction) {
    if (!state.selection.size) return;
    const before = snapshot(state);
    const z = direction > 0 ? nextZ(state) : minZ(state) - 1;
    for (const item of selectedItems(state)) item.z = z;
    commit(state, before);
    render(state);
}

function copy(state) {
    const items = selectedItems(state);
    if (items.length) state.clipboard = JSON.parse(JSON.stringify(items));
}

function paste(state) {
    if (!state.clipboard?.length) return;
    insertCopies(state, state.clipboard, 24);
}

function duplicate(state) {
    const items = selectedItems(state);
    if (items.length) insertCopies(state, JSON.parse(JSON.stringify(items)), 24);
}

function insertCopies(state, items, offset) {
    if (state.doc.items.length + items.length > MAX_ITEMS) return;
    const before = snapshot(state);
    const ids = new Map(items.map(item => [item.id, newId()]));
    const z = nextZ(state);
    const copies = items.map((item, index) => {
        const copyItem = JSON.parse(JSON.stringify(item));
        copyItem.id = ids.get(item.id);
        copyItem.x += offset; copyItem.y += offset;
        if (copyItem.type === 'connector') {
            copyItem.x2 += offset; copyItem.y2 += offset;
            // Ends attached to copied items follow the copies; others stay as they were.
            copyItem.from = ids.get(item.from) ?? item.from;
            copyItem.to = ids.get(item.to) ?? item.to;
            if (!copyItem.from) delete copyItem.from;
            if (!copyItem.to) delete copyItem.to;
        }
        copyItem.z = item.type === 'frame' ? item.z : z + index;
        return copyItem;
    });
    state.doc.items.push(...copies);
    state.selection = new Set(copies.map(item => item.id));
    state.clipboard = JSON.parse(JSON.stringify(copies));
    commit(state, before);
    render(state);
}

function nextZ(state) {
    return state.doc.items.reduce((max, item) => Math.max(max, item.z || 0), 0) + 1;
}

function minZ(state) {
    return state.doc.items.reduce((min, item) => Math.min(min, item.z || 0), 0);
}

function newId() {
    return Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4);
}

function round(value) {
    return Math.round(value * 100) / 100;
}

// ---- Page cards -----------------------------------------------------------------------------

function openPagePicker(state) {
    state.picker?.remove();
    const picker = el('div', 'sentinel-canvas-picker');
    const input = el('input', 'form-control form-control-sm');
    input.placeholder = 'Search pages to add as a card';
    input.setAttribute('aria-label', 'Search pages');
    const list = el('div', 'sentinel-canvas-picker-list');
    picker.append(input, list);
    state.host.appendChild(picker);
    state.picker = picker;
    let timer = null;
    const search = async () => {
        let results = [];
        try { results = state.options.searchPages ? await state.options.searchPages(input.value.trim()) : []; }
        catch { results = []; }
        list.replaceChildren();
        if (!results?.length) {
            const empty = el('div', 'sentinel-canvas-picker-empty');
            empty.textContent = input.value.trim() ? 'No pages found.' : 'Type to search.';
            list.appendChild(empty);
            return;
        }
        for (const page of results.slice(0, 8)) {
            const button = el('button', 'sentinel-canvas-picker-item');
            button.type = 'button';
            button.textContent = `${page.icon || '📄'} ${page.title || 'Untitled'}`;
            button.addEventListener('click', () => {
                addCard(state, page);
                closePicker(state);
            });
            list.appendChild(button);
        }
    };
    input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(search, 150); });
    input.addEventListener('keydown', event => {
        event.stopPropagation();
        if (event.key === 'Escape') { closePicker(state); state.host.focus({ preventScroll: true }); }
        if (event.key === 'Enter') { event.preventDefault(); list.querySelector('button')?.click(); }
    });
    input.focus();
    search();
}

function closePicker(state) {
    state.picker?.remove();
    state.picker = null;
}

function addCard(state, page) {
    if (state.doc.items.length >= MAX_ITEMS) return;
    const rect = state.svg.getBoundingClientRect();
    const [w, h] = DEFAULT_SIZE.card;
    const before = snapshot(state);
    const item = {
        id: newId(), type: 'card', pageId: String(page.id), title: page.title || 'Untitled', icon: page.icon || '📄',
        x: round((rect.width / 2 - state.view.x) / state.view.zoom - w / 2),
        y: round((rect.height / 2 - state.view.y) / state.view.zoom - h / 2), w, h, z: nextZ(state)
    };
    state.doc.items.push(item);
    state.selection = new Set([item.id]);
    commit(state, before);
    render(state);
}

// ---- History and saving ---------------------------------------------------------------------

function snapshot(state) {
    return JSON.stringify(state.doc.items);
}

function commit(state, before) {
    if (before !== undefined && before !== snapshot(state)) {
        state.undo.push(before);
        if (state.undo.length > MAX_UNDO) state.undo.shift();
        state.redo = [];
    }
    emit(state);
}

function undo(state) {
    if (!state.undo.length) return;
    state.redo.push(snapshot(state));
    state.doc.items = JSON.parse(state.undo.pop());
    state.selection.clear();
    emit(state);
    render(state);
}

function redo(state) {
    if (!state.redo.length) return;
    state.undo.push(snapshot(state));
    state.doc.items = JSON.parse(state.redo.pop());
    state.selection.clear();
    emit(state);
    render(state);
}

function serialize(state) {
    return JSON.stringify({ v: 1, items: state.doc.items, view: { x: round(state.view.x), y: round(state.view.y), zoom: round(state.view.zoom) } });
}

function emit(state) {
    if (state.readOnly || !state.options.onChange) return;
    state.options.onChange(serialize(state), canvasRichText(state.doc));
}

// Panning and zooming aren't edits: the view is saved with the next real change.
function persistView(state) {
    state.doc.view = { ...state.view };
}
