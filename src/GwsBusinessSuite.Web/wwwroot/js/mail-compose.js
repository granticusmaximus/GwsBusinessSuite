// Mail's compose toolbar (31-10-26): wraps or prefixes the selection in the message box with
// Markdown (what Mail sends as formatted email), and Cmd/Ctrl+B, +I and +K do the same for bold,
// italic and a link. Blazor's @bind listens for "input", so each change is announced as one.
const actions = {
    bold: { wrap: ['**', '**'], placeholder: 'bold text' },
    italic: { wrap: ['_', '_'], placeholder: 'italic text' },
    link: { wrap: ['[', '](https://)'], placeholder: 'link text' },
    bullets: { prefix: () => '- ' },
    numbers: { prefix: index => (index + 1) + '. ' },
    quote: { prefix: () => '> ' }
};

export function apply(textarea, name) {
    const action = actions[name];
    if (!textarea || !action) return;
    const value = textarea.value;
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    let next;
    let selectFrom;
    let selectTo;
    if (action.wrap) {
        // Spaces at the selection's edges (a double-clicked word brings one) stay outside the
        // markers: "**the **" isn't bold in Markdown.
        const raw = value.slice(start, end);
        const lead = raw.length - raw.trimStart().length;
        const trail = raw.trim().length === 0 ? 0 : raw.length - raw.trimEnd().length;
        const from = raw.trim().length === 0 ? start : start + lead;
        const to = raw.trim().length === 0 ? end : end - trail;
        const selected = value.slice(from, to) || action.placeholder;
        const [before, after] = action.wrap;
        next = value.slice(0, from) + before + selected + after + value.slice(to);
        selectFrom = from + before.length;
        selectTo = selectFrom + selected.length;
    } else {
        // Every line the selection touches gets the prefix.
        const lineStart = value.lastIndexOf('\n', start - 1) + 1;
        const lineEnd = value.indexOf('\n', end);
        const stop = lineEnd === -1 ? value.length : lineEnd;
        const prefixed = value.slice(lineStart, stop).split('\n').map((line, index) => action.prefix(index) + line).join('\n');
        next = value.slice(0, lineStart) + prefixed + value.slice(stop);
        selectFrom = lineStart;
        selectTo = lineStart + prefixed.length;
    }
    textarea.value = next;
    textarea.focus();
    textarea.setSelectionRange(selectFrom, selectTo);
    textarea.dispatchEvent(new Event('input', { bubbles: true }));
}

export function listen(textarea) {
    if (!textarea || textarea.dataset.mailCompose) return;
    textarea.dataset.mailCompose = '1';
    textarea.addEventListener('keydown', event => {
        if (!(event.metaKey || event.ctrlKey) || event.altKey || event.shiftKey) return;
        const name = { b: 'bold', i: 'italic', k: 'link' }[event.key.toLowerCase()];
        if (!name) return;
        event.preventDefault();
        apply(textarea, name);
    });
}
