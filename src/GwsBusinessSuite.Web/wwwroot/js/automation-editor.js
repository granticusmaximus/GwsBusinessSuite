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
    const state = { dotNetRef, drag: null, wire: null };

    // ── Geometry ────────────────────────────────────────────────────────────
    // Ports are positioned by CSS relative to their node, so their canvas-space centre is read
    // from the live layout rather than recomputed from the node's stored position - that way it
    // stays correct while the node is mid-drag and if the CSS ever changes.
    const portCentre = (portEl, canvasRect) => {
        const r = portEl.getBoundingClientRect();
        return {
            x: r.left + r.width / 2 - canvasRect.left + canvas.scrollLeft,
            y: r.top + r.height / 2 - canvasRect.top + canvas.scrollTop
        };
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
        const canvasRect = canvas.getBoundingClientRect();
        // For an endpoint handle the wire has to stay pinned to the opposite end while the
        // grabbed end follows the cursor - anchoring at the handle itself would rubber-band from
        // the wrong place.
        const anchorEl = portEl.dataset.rewire
            ? canvas.querySelector(`[data-port-node="${portEl.dataset.portNode}"][data-port="${portEl.dataset.port}"]`)
            : portEl;
        const origin = portCentre(anchorEl ?? portEl, canvasRect);
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
            origin, path, canvasRect, hovered: null
        };
        portEl.classList.add('is-wiring');
        canvas.classList.add('is-wiring');
        canvas.setPointerCapture(event.pointerId);
        event.preventDefault();
        event.stopPropagation();
    };

    const moveWire = event => {
        const wire = state.wire;
        const point = {
            x: event.clientX - wire.canvasRect.left + canvas.scrollLeft,
            y: event.clientY - wire.canvasRect.top + canvas.scrollTop
        };
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

    // ── Node drag ───────────────────────────────────────────────────────────
    const onPointerDown = event => {
        if (event.button !== 0) return;

        // [data-rewire] handles also carry [data-port], so this one check covers both.
        const port = event.target.closest('[data-port]');
        if (port && canvas.contains(port)) { beginWire(event, port); return; }

        if (event.target.closest('button,input,textarea,select,a')) return;
        const node = event.target.closest('[data-automation-node]');
        if (!node || !canvas.contains(node)) return;
        const canvasRect = canvas.getBoundingClientRect();
        const nodeRect = node.getBoundingClientRect();
        state.drag = {
            node,
            pointerId: event.pointerId,
            offsetX: event.clientX - nodeRect.left,
            offsetY: event.clientY - nodeRect.top,
            canvasLeft: canvasRect.left,
            canvasTop: canvasRect.top
        };
        node.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        if (state.wire && state.wire.pointerId === event.pointerId) { moveWire(event); return; }
        if (!state.drag || state.drag.pointerId !== event.pointerId) return;
        const x = Math.max(0, Math.min(2200, event.clientX - state.drag.canvasLeft - state.drag.offsetX));
        const y = Math.max(0, Math.min(1250, event.clientY - state.drag.canvasTop - state.drag.offsetY));
        state.drag.node.style.left = `${Math.round(x)}px`;
        state.drag.node.style.top = `${Math.round(y)}px`;
    };

    const onPointerUp = async event => {
        if (state.wire && state.wire.pointerId === event.pointerId) { await endWire(event); return; }
        if (!state.drag || state.drag.pointerId !== event.pointerId) return;
        const drag = state.drag;
        state.drag = null;
        const x = Number.parseFloat(drag.node.style.left) || 0;
        const y = Number.parseFloat(drag.node.style.top) || 0;
        try { await state.dotNetRef.invokeMethodAsync('UpdateNodePosition', drag.node.dataset.automationNode, x, y); }
        catch { /* The Blazor circuit may have disconnected while dragging. */ }
    };

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    state.dispose = () => {
        canvas.removeEventListener('pointerdown', onPointerDown);
        canvas.removeEventListener('pointermove', onPointerMove);
        canvas.removeEventListener('pointerup', onPointerUp);
        canvas.removeEventListener('pointercancel', onPointerUp);
    };
    states.set(canvas, state);
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) return;
    state.dispose?.();
    states.delete(canvas);
}
