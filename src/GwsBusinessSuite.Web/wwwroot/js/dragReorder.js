// Chromium (and other browsers) only continue a drag past dragstart into dragover/drop if
// dataTransfer.setData was called during dragstart - Blazor's DragEventArgs doesn't expose
// a way to call that from C#, so a small global listener does it for every draggable
// element instead of wiring per-element JS interop. All the actual reorder logic still
// happens in Blazor's own @ondragstart/@ondrop handlers; this only satisfies the browser's
// "is this a real drag" check.
document.addEventListener('dragstart', (e) => {
    if (e.target instanceof Element && e.target.closest('[draggable="true"]')) {
        e.dataTransfer.setData('text/plain', '');
        e.dataTransfer.effectAllowed = 'move';
    }
});
