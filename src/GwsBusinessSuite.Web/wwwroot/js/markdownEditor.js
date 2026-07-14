// Wraps EasyMDE so Blazor Server components can host a WYSIWYG markdown editor
// (with a built-in preview/side-by-side toggle) over a plain textarea, without each
// component needing to know about CodeMirror/EasyMDE internals directly.
window.gwsMarkdownEditor = (function () {
    const instances = {};
    const suggestionBoxes = {};

    function init(elementId, dotNetHelper, options) {
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

        if (options && options.enableWikiLinks) {
            attachWikiLinkAutocomplete(elementId, editor, dotNetHelper);
        }

        instances[elementId] = editor;
    }

    // Watches for the user typing "[[partial-title" (no closing "]]" yet on the current
    // line) and asks the host component for matching wiki page titles, showing them in a
    // small absolutely-positioned dropdown anchored to the CodeMirror cursor. Chosen via
    // mousedown (not click) so the editor doesn't lose focus/blur the suggestion box away
    // before the selection is applied.
    function attachWikiLinkAutocomplete(elementId, editor, dotNetHelper) {
        const cm = editor.codemirror;

        function closeBox() {
            const box = suggestionBoxes[elementId];
            if (box) {
                box.remove();
                delete suggestionBoxes[elementId];
            }
        }

        cm.on("cursorActivity", async () => {
            const cursor = cm.getCursor();
            const lineToCursor = cm.getLine(cursor.line).slice(0, cursor.ch);
            const match = lineToCursor.match(/\[\[([^[\]]*)$/);
            if (!match) {
                closeBox();
                return;
            }

            const query = match[1];
            let titles;
            try {
                titles = await dotNetHelper.invokeMethodAsync("SearchWikiLinkSuggestions", query);
            } catch {
                return;
            }

            closeBox();
            if (!titles || titles.length === 0) {
                return;
            }

            const coords = cm.cursorCoords(cursor, "page");
            const box = document.createElement("div");
            box.className = "gws-wikilink-suggestions list-group shadow-sm";
            box.style.position = "absolute";
            box.style.left = coords.left + "px";
            box.style.top = coords.bottom + "px";
            box.style.zIndex = "2000";

            titles.forEach((title) => {
                const item = document.createElement("button");
                item.type = "button";
                item.className = "list-group-item list-group-item-action py-1 px-2 small";
                item.textContent = title;
                item.addEventListener("mousedown", (e) => {
                    e.preventDefault();
                    const from = { line: cursor.line, ch: cursor.ch - query.length };
                    cm.replaceRange(title + "]]", from, cursor);
                    closeBox();
                    cm.focus();
                });
                box.appendChild(item);
            });

            document.body.appendChild(box);
            suggestionBoxes[elementId] = box;
        });

        cm.on("blur", () => setTimeout(closeBox, 150));
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

        const box = suggestionBoxes[elementId];
        if (box) {
            box.remove();
            delete suggestionBoxes[elementId];
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