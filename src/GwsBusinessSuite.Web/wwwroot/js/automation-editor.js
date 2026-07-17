const states = new WeakMap();

export function initialize(canvas, dotNetRef) {
    dispose(canvas);
    const state = { dotNetRef, drag: null };

    const onPointerDown = event => {
        if (event.button !== 0 || event.target.closest('button,input,textarea,select,a')) return;
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
        if (!state.drag || state.drag.pointerId !== event.pointerId) return;
        const x = Math.max(0, Math.min(2200, event.clientX - state.drag.canvasLeft - state.drag.offsetX));
        const y = Math.max(0, Math.min(1250, event.clientY - state.drag.canvasTop - state.drag.offsetY));
        state.drag.node.style.left = `${Math.round(x)}px`;
        state.drag.node.style.top = `${Math.round(y)}px`;
    };

    const onPointerUp = async event => {
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
