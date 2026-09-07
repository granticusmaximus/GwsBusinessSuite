// Canvas interaction for the workflow editor. Two independent drag modes share one set of
// pointer listeners:
//
//   node drag  - pointer-down on a node body moves it (positions persist on release)
//   wire drag  - pointer-down on a port draws a connection to another port
//
// The wire mode is what makes the ports mean anything. They have always been rendered in the
// right places and styled like n8n's, but nothing read them, so they looked draggable and were
// not - an affordance that lies costs more than a missing one.

const states = new WeakMap();
const SVG_NS = 'http://www.w3.org/2000/svg';

export function initialize(canvas, dotNetRef) {
    dispose(canvas);
    const viewport = canvas.parentElement;
    const state = {
        dotNetRef, drag: null, wire: null, palette: null, pan: null, marquee: null,
        // Panning by transform rather than by scrolling the container: scrolling cannot zoom,
        // and mixing the two makes every coordinate conversion ambiguous about which space it
        // is in. transform-origin is 0 0 so the maths below stays a plain translate-then-scale.
        view: { x: 0, y: 0, zoom: 1 },
        spaceHeld: false
    };

    const MIN_ZOOM = 0.25, MAX_ZOOM = 2.5;

    const applyView = () => {
        canvas.style.transformOrigin = '0 0';
        canvas.style.transform = `translate(${state.view.x}px, ${state.view.y}px) scale(${state.view.zoom})`;
        canvas.dataset.zoom = state.view.zoom.toFixed(2);
    };

    // Screen point -> canvas coordinates. Everything that positions something in the graph goes
    // through here, so zoom and pan are handled in exactly one place.
    const toCanvas = (clientX, clientY) => {
        const r = viewport.getBoundingClientRect();
        return {
            x: (clientX - r.left - state.view.x) / state.view.zoom,
            y: (clientY - r.top - state.view.y) / state.view.zoom
        };
    };

    const zoomTo = (nextZoom, anchorClientX, anchorClientY) => {
        const clamped = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, nextZoom));
        if (clamped === state.view.zoom) return;
        const r = viewport.getBoundingClientRect();
        const ax = anchorClientX ?? r.left + r.width / 2;
        const ay = anchorClientY ?? r.top + r.height / 2;
        // Keep the point under the cursor fixed while the scale changes, which is what makes
        // wheel-zoom feel like it is zooming where you are looking.
        const before = toCanvas(ax, ay);
        state.view.zoom = clamped;
        state.view.x = ax - r.left - before.x * clamped;
        state.view.y = ay - r.top - before.y * clamped;
        applyView();
    };

    applyView();

    // ── Geometry ────────────────────────────────────────────────────────────
    // Ports are positioned by CSS relative to their node, so their canvas-space centre is read
    // from the live layout rather than recomputed from the node's stored position - that way it
    // stays correct while the node is mid-drag and if the CSS ever changes.
    // getBoundingClientRect is screen space and already includes the view transform, so it is
    // converted back rather than offset by the canvas rect - the latter silently breaks at any
    // zoom other than 1.
    const portCentre = portEl => {
        const r = portEl.getBoundingClientRect();
        return toCanvas(r.left + r.width / 2, r.top + r.height / 2);
    };

    // Matches the cubic used to render committed connections, so the wire you drag looks like
    // the wire you get.
    const curve = (from, to) => {
        const dx = Math.max(40, Math.abs(to.x - from.x) * 0.5);
        return `M ${from.x} ${from.y} C ${from.x + dx} ${from.y}, ${to.x - dx} ${to.y}, ${to.x} ${to.y}`;
    };

    const connectionLayer = () => canvas.querySelector('svg.automation-connections');

    // ── Validity ────────────────────────────────────────────────────────────
    // Checked during the drag so the answer shows in the cursor and the port highlight, rather
    // than arriving as an error after the user has already committed to the gesture.
    const isValidTarget = (wire, targetEl) => {
        if (!targetEl) return false;
        if (targetEl.dataset.rewire) return false;                           // a handle, not a port
        if (targetEl.dataset.port === wire.fromDirection) return false;      // output->output
        if (targetEl.dataset.portNode === wire.fromNode) return false;       // self-connection
        return true;
    };

    const clearTargetHighlight = wire => {
        if (wire.hovered) { wire.hovered.classList.remove('is-drop-target', 'is-invalid-target'); wire.hovered = null; }
    };

    // ── Wire drag ───────────────────────────────────────────────────────────
    const beginWire = (event, portEl) => {
        // For an endpoint handle the wire has to stay pinned to the opposite end while the
        // grabbed end follows the cursor - anchoring at the handle itself would rubber-band from
        // the wrong place.
        const anchorEl = portEl.dataset.rewire
            ? canvas.querySelector(`[data-port-node="${portEl.dataset.portNode}"][data-port="${portEl.dataset.port}"]`)
            : portEl;
        const origin = portCentre(anchorEl ?? portEl);
        const layer = connectionLayer();
        if (!layer) return;

        const path = document.createElementNS(SVG_NS, 'path');
        path.setAttribute('class', 'automation-connection is-pending');
        path.setAttribute('d', curve(origin, origin));
        layer.appendChild(path);

        state.wire = {
            pointerId: event.pointerId,
            fromNode: portEl.dataset.portNode,
            fromName: portEl.dataset.portName,
            fromDirection: portEl.dataset.port,
            // Present only when the drag started on a connection's endpoint handle. The handle
            // advertises the identity of the end that is NOT moving, so everything above is
            // already the anchor - only the commit differs.
            rewireConnection: portEl.dataset.rewireConnection ?? null,
            rewireEnd: portEl.dataset.rewire ?? null,
            origin, path, hovered: null
        };
        portEl.classList.add('is-wiring');
        canvas.classList.add('is-wiring');
        canvas.setPointerCapture(event.pointerId);
        event.preventDefault();
        event.stopPropagation();
    };

    const moveWire = event => {
        const wire = state.wire;
        const point = toCanvas(event.clientX, event.clientY);
        // Outputs curve right and inputs curve left, so a wire started from an input is drawn
        // backwards to keep the shape reading correctly.
        wire.path.setAttribute('d', wire.fromDirection === 'output'
            ? curve(wire.origin, point)
            : curve(point, wire.origin));

        // elementFromPoint rather than the event target: the pointer is captured by the canvas
        // for the whole drag, so the event never reports the port underneath.
        const under = document.elementFromPoint(event.clientX, event.clientY);
        const port = under && under.closest ? under.closest('[data-port]') : null;
        if (port === wire.hovered) return;

        clearTargetHighlight(wire);
        if (port) {
            wire.hovered = port;
            port.classList.add(isValidTarget(wire, port) ? 'is-drop-target' : 'is-invalid-target');
        }
    };

    const endWire = async event => {
        const wire = state.wire;
        state.wire = null;
        canvas.classList.remove('is-wiring');
        canvas.querySelectorAll('.is-wiring').forEach(el => el.classList.remove('is-wiring'));
        clearTargetHighlight(wire);
        wire.path.remove();

        const under = document.elementFromPoint(event.clientX, event.clientY);
        const target = under && under.closest ? under.closest('[data-port]') : null;

        if (!target) {
            // Released over nothing. Rather than throwing the gesture away, offer to create a
            // node here already wired up - dragging into space is how you extend a flow in n8n.
            const overCanvas = under && canvas.contains(under);
            if (overCanvas && wire.fromDirection === 'output' && !wire.rewireConnection) {
                const { x, y } = toCanvas(event.clientX, event.clientY);
                try { await state.dotNetRef.invokeMethodAsync('BeginConnectedInsert', wire.fromNode, wire.fromName, x, y); }
                catch { /* circuit dropped */ }
            }
            return;
        }

        if (!isValidTarget(wire, target)) return;

        // Normalise direction: whichever end is the output is the source, so dragging
        // input->output works exactly as well as output->input.
        const fromOutput = wire.fromDirection === 'output';
        const sourceNodeId = fromOutput ? wire.fromNode : target.dataset.portNode;
        const sourceOutput = fromOutput ? wire.fromName : target.dataset.portName;
        const targetNodeId = fromOutput ? target.dataset.portNode : wire.fromNode;
        const targetInput = fromOutput ? target.dataset.portName : wire.fromName;

        try {
            if (wire.rewireConnection) {
                await state.dotNetRef.invokeMethodAsync(
                    'RewireConnection', wire.rewireConnection, wire.rewireEnd,
                    target.dataset.portNode, target.dataset.portName);
            } else {
                await state.dotNetRef.invokeMethodAsync('ConnectPorts', sourceNodeId, sourceOutput, targetNodeId, targetInput);
            }
        }
        catch { /* The Blazor circuit may have dropped mid-drag; the graph reloads on reconnect. */ }
    };

    // ── Pan and marquee ─────────────────────────────────────────────────────
    // Empty-canvas drag is a rubber-band selection; space-drag or middle-drag pans. That split
    // is the Figma/n8n convention, and it keeps the most common gesture (select) on plain drag.
    const beginPan = event => {
        state.pan = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, originX: state.view.x, originY: state.view.y };
        viewport.classList.add('is-panning');
        viewport.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const beginMarquee = event => {
        const box = document.createElement('div');
        box.className = 'canvas-marquee';
        canvas.appendChild(box);
        const at = toCanvas(event.clientX, event.clientY);
        state.marquee = { pointerId: event.pointerId, box, startX: at.x, startY: at.y, captured: false, additive: event.ctrlKey || event.metaKey || event.shiftKey };
        // Capture is taken on the first move, not here, and preventDefault is not called at all.
        // Both suppress the browser's follow-up click - capture by retargeting it to the
        // viewport, preventDefault by cancelling it - and the canvas needs that click to clear
        // selection. Taking capture only once this is genuinely a drag keeps a plain click intact
        // while still following the pointer if it leaves the viewport mid-band.
        viewport.classList.add('is-marqueeing');
    };

    const moveMarquee = event => {
        const m = state.marquee;
        if (!m.captured) { m.captured = true; viewport.setPointerCapture(event.pointerId); }
        const at = toCanvas(event.clientX, event.clientY);
        const left = Math.min(m.startX, at.x), top = Math.min(m.startY, at.y);
        const width = Math.abs(at.x - m.startX), height = Math.abs(at.y - m.startY);
        Object.assign(m.box.style, { left: `${left}px`, top: `${top}px`, width: `${width}px`, height: `${height}px` });
        m.rect = { left, top, right: left + width, bottom: top + height };
    };

    const endMarquee = async () => {
        const m = state.marquee;
        state.marquee = null;
        m.box.remove();
        viewport.classList.remove('is-marqueeing');
        // A plain click on empty canvas also opens (and immediately closes) a zero-size band, so
        // the guard below has to come after this check - swallowing every click would break
        // click-to-clear-selection entirely.
        if (!m.rect || (m.rect.right - m.rect.left < 4 && m.rect.bottom - m.rect.top < 4)) return;

        // A real drag still ends in a click, and the canvas clears selection on click - so that
        // one click is swallowed, or the selection is wiped the instant it is made.
        state.swallowNextClick = true;

        // Intersection, not containment: half-covering a node selects it, which is what people
        // expect from a rubber band and avoids having to enclose big nodes exactly.
        const ids = [...canvas.querySelectorAll('[data-automation-node]')].filter(el => {
            const x = Number.parseFloat(el.style.left) || 0;
            const y = Number.parseFloat(el.style.top) || 0;
            return x < m.rect.right && x + el.offsetWidth > m.rect.left
                && y < m.rect.bottom && y + el.offsetHeight > m.rect.top;
        }).map(el => el.dataset.automationNode);

        try { await state.dotNetRef.invokeMethodAsync('SelectNodesInRegion', ids, m.additive); }
        catch { /* circuit dropped */ }
    };

    const onSwallowClick = event => {
        if (!state.swallowNextClick) return;
        state.swallowNextClick = false;
        event.stopPropagation();
        event.preventDefault();
    };

    const onWheel = event => {
        // Claimed from the browser's own page zoom: otherwise zooming the graph would zoom the
        // entire admin UI instead.
        event.preventDefault();
        const factor = event.deltaY < 0 ? 1.12 : 1 / 1.12;
        zoomTo(state.view.zoom * factor, event.clientX, event.clientY);
    };

    const isTyping = target =>
        target instanceof Element
        && (target.closest('input,textarea,select,[contenteditable]') !== null);

    const onKeyDown = event => {
        if (event.code !== 'Space' || state.spaceHeld) return;
        // Space is a character when a field has focus, not a pan modifier.
        if (isTyping(event.target)) return;
        state.spaceHeld = true;
        viewport.classList.add('is-pannable');
    };
    const onKeyUp = event => { if (event.code === 'Space') { state.spaceHeld = false; viewport.classList.remove('is-pannable'); } };

    // ── Node drag ───────────────────────────────────────────────────────────
    const onPointerDown = event => {
        // Middle button, or space held: pan regardless of what is underneath.
        if (event.button === 1 || (event.button === 0 && state.spaceHeld)) { beginPan(event); return; }
        if (event.button !== 0) return;

        // [data-rewire] handles also carry [data-port], so this one check covers both.
        const port = event.target.closest('[data-port]');
        if (port && canvas.contains(port)) { beginWire(event, port); return; }

        if (event.target.closest('button,input,textarea,select,a')) return;
        const node = event.target.closest('[data-automation-node]');
        if (!node || !canvas.contains(node)) {
            // Empty canvas: rubber-band select.
            if (canvas.contains(event.target) || event.target === canvas || event.target === viewport) beginMarquee(event);
            return;
        }
        // Deltas are tracked in canvas space so a drag moves the node the same graph distance
        // regardless of zoom - screen-pixel offsets drift badly once scaled.
        const pointerAt = toCanvas(event.clientX, event.clientY);
        state.drag = {
            node,
            pointerId: event.pointerId,
            grabX: pointerAt.x - (Number.parseFloat(node.style.left) || 0),
            grabY: pointerAt.y - (Number.parseFloat(node.style.top) || 0)
        };
        node.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        if (state.pan && state.pan.pointerId === event.pointerId) {
            state.view.x = state.pan.originX + (event.clientX - state.pan.startX);
            state.view.y = state.pan.originY + (event.clientY - state.pan.startY);
            applyView();
            return;
        }
        if (state.marquee && state.marquee.pointerId === event.pointerId) { moveMarquee(event); return; }
        if (state.wire && state.wire.pointerId === event.pointerId) { moveWire(event); return; }
        if (!state.drag || state.drag.pointerId !== event.pointerId) return;
        const at = toCanvas(event.clientX, event.clientY);
        state.drag.node.style.left = `${Math.round(Math.max(0, at.x - state.drag.grabX))}px`;
        state.drag.node.style.top = `${Math.round(Math.max(0, at.y - state.drag.grabY))}px`;
    };

    const onPointerUp = async event => {
        if (state.pan && state.pan.pointerId === event.pointerId) { state.pan = null; viewport.classList.remove('is-panning'); return; }
        if (state.marquee && state.marquee.pointerId === event.pointerId) { await endMarquee(); return; }
        if (state.wire && state.wire.pointerId === event.pointerId) { await endWire(event); return; }
        if (!state.drag || state.drag.pointerId !== event.pointerId) return;
        const drag = state.drag;
        state.drag = null;
        const x = Number.parseFloat(drag.node.style.left) || 0;
        const y = Number.parseFloat(drag.node.style.top) || 0;
        try { await state.dotNetRef.invokeMethodAsync('UpdateNodePosition', drag.node.dataset.automationNode, x, y); }
        catch { /* The Blazor circuit may have disconnected while dragging. */ }
    };

    // ── Palette drag ────────────────────────────────────────────────────────
    // The palette is outside the canvas element, so these listen on the document and filter to
    // palette entries rather than hanging off the canvas like the other two drag modes.
    const onPaletteDown = event => {
        if (event.button !== 0) return;
        const entry = event.target.closest('[data-palette-node]');
        if (!entry) return;

        state.palette = {
            pointerId: event.pointerId,
            typeKey: entry.dataset.paletteNode,
            version: Number.parseInt(entry.dataset.paletteVersion, 10) || 1,
            startX: event.clientX,
            startY: event.clientY,
            ghost: null,
            label: entry.dataset.paletteLabel ?? 'Node'
        };
        entry.setPointerCapture(event.pointerId);
        state.palette.capturedBy = entry;
    };

    const onPaletteMove = event => {
        const drag = state.palette;
        if (!drag || drag.pointerId !== event.pointerId) return;

        // Only start dragging past a small threshold, so an ordinary click still adds a node the
        // way it always did rather than being swallowed by a 2px pointer wobble.
        if (!drag.ghost) {
            if (Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) < 5) return;
            const ghost = document.createElement('div');
            ghost.className = 'palette-drag-ghost';
            ghost.textContent = drag.label;
            document.body.appendChild(ghost);
            drag.ghost = ghost;
            canvas.classList.add('is-drop-ready');
        }
        drag.ghost.style.left = `${event.clientX + 12}px`;
        drag.ghost.style.top = `${event.clientY + 12}px`;
    };

    const onPaletteUp = async event => {
        const drag = state.palette;
        if (!drag || drag.pointerId !== event.pointerId) return;
        state.palette = null;
        canvas.classList.remove('is-drop-ready');
        drag.ghost?.remove();
        if (!drag.ghost) return;   // never passed the threshold: the click handler adds it

        const rect = viewport.getBoundingClientRect();
        const inside = event.clientX >= rect.left && event.clientX <= rect.right
            && event.clientY >= rect.top && event.clientY <= rect.bottom;
        if (!inside) return;

        // Offset by half the node so it lands centred under the pointer, not starting at it.
        const at = toCanvas(event.clientX, event.clientY);
        const x = at.x - 98, y = at.y - 55;
        try { await state.dotNetRef.invokeMethodAsync('AddNodeAt', drag.typeKey, drag.version, Math.max(0, x), Math.max(0, y)); }
        catch { /* circuit dropped */ }
    };

    document.addEventListener('pointerdown', onPaletteDown, true);
    document.addEventListener('pointermove', onPaletteMove, true);
    document.addEventListener('pointerup', onPaletteUp, true);
    document.addEventListener('pointercancel', onPaletteUp, true);

    // Bound on the viewport, not the canvas: the canvas is transformed and can be smaller than
    // its container at low zoom, so gestures in the surrounding space would otherwise be lost.
    viewport.addEventListener('pointerdown', onPointerDown);
    viewport.addEventListener('pointermove', onPointerMove);
    viewport.addEventListener('pointerup', onPointerUp);
    viewport.addEventListener('pointercancel', onPointerUp);
    viewport.addEventListener('click', onSwallowClick, true);
    viewport.addEventListener('wheel', onWheel, { passive: false });
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('keyup', onKeyUp);
    state.dispose = () => {
        document.removeEventListener('pointerdown', onPaletteDown, true);
        document.removeEventListener('pointermove', onPaletteMove, true);
        document.removeEventListener('pointerup', onPaletteUp, true);
        document.removeEventListener('pointercancel', onPaletteUp, true);
        viewport.removeEventListener('pointerdown', onPointerDown);
        viewport.removeEventListener('pointermove', onPointerMove);
        viewport.removeEventListener('pointerup', onPointerUp);
        viewport.removeEventListener('pointercancel', onPointerUp);
        viewport.removeEventListener('click', onSwallowClick, true);
        viewport.removeEventListener('wheel', onWheel);
        document.removeEventListener('keydown', onKeyDown);
        document.removeEventListener('keyup', onKeyUp);
    };
    state.zoomBy = factor => zoomTo(state.view.zoom * factor);
    state.resetView = () => { state.view = { x: 0, y: 0, zoom: 1 }; applyView(); };
    state.fitToContent = () => {
        const nodes = [...canvas.querySelectorAll('[data-automation-node]')];
        if (nodes.length === 0) { state.resetView(); return; }
        const b = nodes.reduce((acc, el) => {
            const x = Number.parseFloat(el.style.left) || 0, y = Number.parseFloat(el.style.top) || 0;
            return {
                minX: Math.min(acc.minX, x), minY: Math.min(acc.minY, y),
                maxX: Math.max(acc.maxX, x + el.offsetWidth), maxY: Math.max(acc.maxY, y + el.offsetHeight)
            };
        }, { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity });

        const pad = 60;
        const r = viewport.getBoundingClientRect();
        const zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM,
            Math.min(r.width / (b.maxX - b.minX + pad * 2), r.height / (b.maxY - b.minY + pad * 2))));
        state.view.zoom = zoom;
        state.view.x = (r.width - (b.maxX - b.minX) * zoom) / 2 - b.minX * zoom;
        state.view.y = (r.height - (b.maxY - b.minY) * zoom) / 2 - b.minY * zoom;
        applyView();
    };
    states.set(canvas, state);
}

export function zoomIn(canvas) { states.get(canvas)?.zoomBy?.(1.2); }
export function zoomOut(canvas) { states.get(canvas)?.zoomBy?.(1 / 1.2); }
export function resetView(canvas) { states.get(canvas)?.resetView?.(); }
export function fitToContent(canvas) { states.get(canvas)?.fitToContent?.(); }

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) return;
    state.dispose?.();
    states.delete(canvas);
}
