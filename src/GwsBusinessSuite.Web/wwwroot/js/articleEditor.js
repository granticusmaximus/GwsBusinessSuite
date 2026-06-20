// Inserts text at the current cursor position (or replaces the selection) inside a
// textarea, then fires an 'input' event so Blazor's @bind:event="oninput" picks up the
// change. Used by the Article Editor's "Insert at Cursor" ad-placement button.
window.insertAtCursor = function (elementId, text) {
    const el = document.getElementById(elementId);
    if (!el) {
        return;
    }

    const start = el.selectionStart ?? el.value.length;
    const end = el.selectionEnd ?? el.value.length;
    const before = el.value.substring(0, start);
    const after = el.value.substring(end);

    el.value = before + text + after;
    el.dispatchEvent(new Event('input', { bubbles: true }));

    const cursorPosition = start + text.length;
    el.focus();
    el.setSelectionRange(cursorPosition, cursorPosition);
};