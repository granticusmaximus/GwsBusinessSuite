// Wraps EasyMDE so Blazor Server components can host a WYSIWYG markdown editor
// (with a built-in preview/side-by-side toggle) over a plain textarea, without each
// component needing to know about CodeMirror/EasyMDE internals directly.
window.gwsMarkdownEditor = (function () {
    const instances = {};

    function init(elementId, dotNetHelper) {
        if (instances[elementId]) {
            return;
        }

        const el = document.getElementById(elementId);
        if (!el) {
            return;
        }

        const editor = new EasyMDE({
            element: el,
            spellChecker: false,
            status: ["lines", "words"],
            minHeight: "420px",
            toolbar: [
                "bold", "italic", "heading", "|",
                "quote", "unordered-list", "ordered-list", "|",
                "link", "image", "code", "table", "horizontal-rule", "|",
                "preview", "side-by-side", "fullscreen", "|",
                "guide"
            ]
        });

        editor.codemirror.on("change", () => {
            dotNetHelper.invokeMethodAsync("OnMarkdownChanged", editor.value());
        });

        instances[elementId] = editor;
    }

    function setValue(elementId, value) {
        const editor = instances[elementId];
        if (editor && editor.value() !== value) {
            editor.value(value ?? "");
        }
    }

    function destroy(elementId) {
        const editor = instances[elementId];
        if (!editor) {
            return;
        }

        editor.toTextArea();
        delete instances[elementId];
    }

    // CodeMirror-native equivalent of the old raw-textarea insertAtCursor - once EasyMDE
    // is active the underlying textarea is hidden and no longer reflects keystrokes, so
    // inserts have to go through the CodeMirror instance directly.
    function insertAtCursor(elementId, text) {
        const editor = instances[elementId];
        if (!editor) {
            return;
        }

        editor.codemirror.getDoc().replaceSelection(text);
        editor.codemirror.focus();
    }

    return { init, setValue, destroy, insertAtCursor };
})();