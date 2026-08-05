// Chromium (and other browsers) only continue a drag past dragstart into dragover/drop if
// dataTransfer.setData was called during dragstart - Blazor's DragEventArgs doesn't expose
// a way to call that from C#, so a small global listener does it for every draggable
// element instead of wiring per-element JS interop. Most callers (Blazor's own
// @ondragstart/@ondrop handlers) never read dataTransfer back, so the placeholder value is
// harmless to them. But a component *can* legitimately read it back in its own dragstart/drop
// listeners to carry a real payload (see wiki-block-editor.js's inline Kanban board) - and
// getData() only works during dragover/drop per spec, so there's no way to detect "was real
// data already set" and skip overwriting it. Registered with useCapture=true instead: capture
// listeners on ancestors run *before* the target's own bubble-phase listeners, so this always
// sets the placeholder first and lets the element's own dragstart handler overwrite it with
// the real payload afterward (setData for the same format keeps only the last value written).
document.addEventListener('dragstart', (e) => {
    if (e.target instanceof Element && e.target.closest('[draggable="true"]')) {
        e.dataTransfer.setData('text/plain', '');
        e.dataTransfer.effectAllowed = 'move';
    }
}, true);
