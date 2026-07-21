// Notion-style block editor for the Wiki. Follows automation-editor.js's shape (ES module,
// DotNetObjectReference, Pointer Events for drag) rather than the CMS Builder's iframe +
// postMessage bridge - that one exists specifically because the CMS canvas previews the live
// public-render route in an iframe; the Wiki editor has no such constraint, so the simpler
// same-document pattern applies.
//
// The DOM here is the source of truth while a page is being edited (JS owns it); Blazor only
// receives a serialized snapshot via OnBlocksChanged (mirroring the existing
// OnMarkdownChanged callback shape) and persists it on explicit Save, same as before.

const states = new WeakMap();

const BLOCK_TYPES = [
    { type: 'paragraph', label: 'Text', icon: '¶' },
    { type: 'heading_1', label: 'Heading 1', icon: 'H1' },
    { type: 'heading_2', label: 'Heading 2', icon: 'H2' },
    { type: 'heading_3', label: 'Heading 3', icon: 'H3' },
    { type: 'bulleted_list_item', label: 'Bulleted list', icon: '•' },
    { type: 'numbered_list_item', label: 'Numbered list', icon: '1.' },
    { type: 'to_do', label: 'To-do', icon: '☑' },
    { type: 'toggle', label: 'Toggle', icon: '▸' },
    { type: 'quote', label: 'Quote', icon: '❝' },
    { type: 'callout', label: 'Callout', icon: '💡' },
    { type: 'code', label: 'Code', icon: '</>' },
    { type: 'divider', label: 'Divider', icon: '—' },
    { type: 'image', label: 'Image', icon: '🖼' },
    { type: 'embed', label: 'Embed link', icon: '🔗' }
];
const TEXTLESS_TYPES = new Set(['divider', 'image', 'embed']);

export function initialize(container, dotNetRef, initialBlocksJson) {
    dispose(container);
    const state = {
        container,
        dotNetRef,
        drag: null,
        notifyTimer: null,
        slashMenu: null,
        wikiLinkMenu: null,
        mentionMenu: null
    };
    states.set(container, state);
    setBlocks(container, initialBlocksJson);

    container.addEventListener('pointerdown', event => onHandlePointerDown(state, event));
    container.addEventListener('pointermove', event => onHandlePointerMove(state, event));
    container.addEventListener('pointerup', event => onHandlePointerUp(state, event));
    container.addEventListener('pointercancel', event => onHandlePointerUp(state, event));
    document.addEventListener('mousedown', state.outsideClickHandler = event => closeFloatingMenus(state, event));
}

export function setBlocks(container, blocksJson) {
    const state = states.get(container);
    if (!state) return;

    let blocks;
    try { blocks = JSON.parse(blocksJson || '[]'); } catch { blocks = []; }
    if (!Array.isArray(blocks) || blocks.length === 0) {
        blocks = [emptyBlock('paragraph')];
    }

    container.innerHTML = '';
    for (const block of blocks) {
        container.appendChild(createBlockElement(block, state));
    }
}

export function dispose(container) {
    const state = states.get(container);
    if (!state) return;
    if (state.notifyTimer) clearTimeout(state.notifyTimer);
    closeFloatingMenus(state);
    if (state.outsideClickHandler) document.removeEventListener('mousedown', state.outsideClickHandler);
    states.delete(container);
}

// ---- Block creation ----------------------------------------------------

function emptyBlock(type) {
    return { id: crypto.randomUUID(), type, indentLevel: 0, richText: [], props: {} };
}

function createBlockElement(block, state) {
    const el = document.createElement('div');
    el.className = 'wiki-block';
    el.dataset.blockId = block.id;
    el.dataset.blockType = block.type;
    el.dataset.indent = String(block.indentLevel || 0);
    if (block.type === 'to_do' && block.props && block.props.checked === 'true') el.dataset.checked = 'true';
    applyIndentStyle(el);

    const gutter = document.createElement('div');
    gutter.className = 'wiki-block-gutter';

    const addBtn = document.createElement('button');
    addBtn.type = 'button';
    addBtn.className = 'wiki-block-add';
    addBtn.title = 'Insert block below';
    addBtn.textContent = '+';
    addBtn.addEventListener('mousedown', event => {
        event.preventDefault();
        const created = createBlockElement(emptyBlock('paragraph'), state);
        el.after(created);
        focusBlock(created);
        notifyChanged(state);
    });

    const handle = document.createElement('span');
    handle.className = 'wiki-block-handle';
    handle.title = 'Drag to reorder';
    handle.textContent = '⠿';

    gutter.append(addBtn, handle);
    el.appendChild(gutter);
    el.appendChild(createBlockBody(block, state));
    return el;
}

function applyIndentStyle(el) {
    const indent = Number(el.dataset.indent || '0');
    el.style.marginLeft = indent > 0 ? `${indent * 1.5}rem` : '';
}

function createBlockBody(block, state) {
    const body = document.createElement('div');
    body.className = 'wiki-block-body';

    if (block.type === 'divider') {
        body.appendChild(document.createElement('hr'));
        return body;
    }

    if (block.type === 'image' || block.type === 'embed') {
        body.appendChild(createMediaBody(block, state));
        return body;
    }

    if (block.type === 'to_do') {
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'wiki-todo-checkbox';
        checkbox.checked = block.props && block.props.checked === 'true';
        checkbox.addEventListener('change', () => {
            checkbox.closest('.wiki-block').dataset.checked = checkbox.checked ? 'true' : 'false';
            notifyChanged(state);
        });
        body.appendChild(checkbox);
    }

    if (block.type === 'bulleted_list_item') {
        const bullet = document.createElement('span');
        bullet.className = 'wiki-list-marker';
        bullet.textContent = '•';
        body.appendChild(bullet);
    }

    body.appendChild(createContentEditable(block, state));
    return body;
}

function createMediaBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-media-block';
    const url = (block.props && block.props.url) || '';

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'form-control form-control-sm';
    input.placeholder = block.type === 'image' ? 'Paste an image URL and press Enter' : 'Paste a link and press Enter';
    input.value = url;

    const preview = document.createElement('div');
    preview.className = 'wiki-media-preview';
    renderMediaPreview(preview, block.type, url);

    const commit = () => {
        const el = wrapper.closest('.wiki-block');
        el.dataset.url = input.value.trim();
        renderMediaPreview(preview, block.type, input.value.trim());
        notifyChanged(state);
    };
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') { event.preventDefault(); commit(); }
    });
    input.addEventListener('blur', commit);

    wrapper.append(input, preview);
    return wrapper;
}

function renderMediaPreview(preview, type, url) {
    preview.innerHTML = '';
    if (!url) return;
    if (type === 'image') {
        const img = document.createElement('img');
        img.src = url;
        img.loading = 'lazy';
        img.style.maxWidth = '100%';
        preview.appendChild(img);
    } else {
        const link = document.createElement('a');
        link.href = url;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.textContent = url;
        preview.appendChild(link);
    }
}

function createContentEditable(block, state) {
    const content = document.createElement('div');
    content.className = 'wiki-block-content';
    content.contentEditable = 'plaintext-only' in document.body ? 'plaintext-only' : 'true';
    content.dataset.placeholder = placeholderFor(block.type);
    content.innerHTML = htmlFromRichText(block.richText || []);

    content.addEventListener('keydown', event => onContentKeyDown(state, content, event));
    content.addEventListener('input', () => {
        checkSlashTrigger(state, content);
        checkWikiLinkTrigger(state, content);
        checkMentionTrigger(state, content);
        scheduleNotify(state);
    });
    content.addEventListener('paste', event => {
        event.preventDefault();
        const text = (event.clipboardData || window.clipboardData).getData('text/plain');
        document.execCommand('insertText', false, text);
    });

    return content;
}

function placeholderFor(type) {
    switch (type) {
        case 'heading_1': return 'Heading 1';
        case 'heading_2': return 'Heading 2';
        case 'heading_3': return 'Heading 3';
        case 'to_do': return 'To-do';
        case 'toggle': return 'Toggle';
        case 'quote': return 'Quote';
        case 'callout': return 'Callout';
        case 'code': return 'Code';
        default: return "Type '/' for commands";
    }
}

// ---- Keyboard model ------------------------------------------------------

function onContentKeyDown(state, content, event) {
    const blockEl = content.closest('.wiki-block');

    if (event.key === 'Enter' && !event.shiftKey) {
        if (state.slashMenu) return; // Enter/selection is handled by the menu itself.
        event.preventDefault();
        splitBlock(state, blockEl, content);
        return;
    }

    if (event.key === 'Backspace' && isCaretAtStart(content) && !hasSelection()) {
        const previous = blockEl.previousElementSibling;
        if (previous) {
            event.preventDefault();
            mergeIntoPrevious(state, blockEl, previous);
        }
        return;
    }

    if (event.key === 'Tab') {
        event.preventDefault();
        if (event.shiftKey) outdentBlock(state, blockEl);
        else indentBlock(state, blockEl);
        return;
    }

    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'b') {
        event.preventDefault();
        toggleInlineTag('b');
        scheduleNotify(state);
        return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'i') {
        event.preventDefault();
        toggleInlineTag('i');
        scheduleNotify(state);
        return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        const url = window.prompt('Link URL');
        if (url) { toggleInlineTag('a', { href: url }); scheduleNotify(state); }
        return;
    }
}

function splitBlock(state, blockEl, content) {
    const range = getCaretRange(content);
    const afterFragment = range ? range.cloneRange() : null;
    let afterHtml = '';
    if (afterFragment) {
        afterFragment.setEnd(content, content.childNodes.length);
        const fragment = afterFragment.cloneContents();
        const div = document.createElement('div');
        div.appendChild(fragment);
        afterHtml = div.innerHTML;
        range.deleteContents();
        const trimRange = document.createRange();
        trimRange.setStart(range.endContainer, range.endOffset);
        trimRange.setEnd(content, content.childNodes.length);
        trimRange.deleteContents();
    }

    const newType = ['heading_1', 'heading_2', 'heading_3'].includes(blockEl.dataset.blockType)
        ? 'paragraph'
        : blockEl.dataset.blockType;
    const newBlock = emptyBlock(newType);
    newBlock.indentLevel = Number(blockEl.dataset.indent || '0');
    const newEl = createBlockElement(newBlock, state);
    blockEl.after(newEl);
    const newContent = newEl.querySelector('.wiki-block-content');
    if (newContent) newContent.innerHTML = afterHtml;

    focusBlock(newEl);
    notifyChanged(state);
}

function mergeIntoPrevious(state, blockEl, previous) {
    const previousContent = previous.querySelector('.wiki-block-content');
    const currentContent = blockEl.querySelector('.wiki-block-content');
    if (previousContent && currentContent) {
        const caretOffset = previousContent.textContent.length;
        previousContent.innerHTML += currentContent.innerHTML;
        blockEl.remove();
        placeCaretAtTextOffset(previousContent, caretOffset);
    } else {
        blockEl.remove();
        focusBlock(previous);
    }
    notifyChanged(state);
}

function indentBlock(state, blockEl) {
    const previous = blockEl.previousElementSibling;
    if (!previous) return;
    const current = Number(blockEl.dataset.indent || '0');
    const max = Number(previous.dataset.indent || '0') + 1;
    blockEl.dataset.indent = String(Math.min(current + 1, max));
    applyIndentStyle(blockEl);
    notifyChanged(state);
}

function outdentBlock(state, blockEl) {
    const current = Number(blockEl.dataset.indent || '0');
    if (current === 0) return;
    blockEl.dataset.indent = String(current - 1);
    applyIndentStyle(blockEl);
    notifyChanged(state);
}

// ---- Slash command menu (same trigger -> async search -> floating dropdown
// -> mousedown-commit template proven by markdownEditor.js's wiki-link autocomplete) -------

function checkSlashTrigger(state, content) {
    const text = content.textContent;
    const match = text.match(/^\/(\w*)$/);
    closeSlashMenu(state);
    if (!match) return;

    const query = match[1].toLowerCase();
    const matches = BLOCK_TYPES.filter(item => item.label.toLowerCase().includes(query) || item.type.includes(query));
    if (matches.length === 0) return;

    const menu = document.createElement('div');
    menu.className = 'wiki-slash-menu list-group shadow-sm';
    positionMenu(menu, content);

    for (const item of matches) {
        const option = document.createElement('button');
        option.type = 'button';
        option.className = 'list-group-item list-group-item-action py-1 px-2 small d-flex align-items-center gap-2';
        option.innerHTML = `<span class="wiki-slash-icon">${item.icon}</span><span>${item.label}</span>`;
        option.addEventListener('mousedown', event => {
            event.preventDefault();
            convertBlockType(state, content.closest('.wiki-block'), item.type);
            closeSlashMenu(state);
        });
        menu.appendChild(option);
    }

    document.body.appendChild(menu);
    state.slashMenu = menu;
}

function convertBlockType(state, blockEl, newType) {
    const block = serializeBlock(blockEl);
    block.type = newType;
    block.richText = [];
    block.props = {};
    const newEl = createBlockElement(block, state);
    blockEl.replaceWith(newEl);
    const focusable = newEl.querySelector('.wiki-block-content, input');
    if (focusable) focusable.focus();
    notifyChanged(state);
}

function closeSlashMenu(state) {
    if (state.slashMenu) { state.slashMenu.remove(); state.slashMenu = null; }
}

// ---- Wiki-link ([[Page]]) autocomplete, same trigger pattern -------------

function checkWikiLinkTrigger(state, content) {
    const range = getCaretRange(content);
    closeWikiLinkMenu(state);
    if (!range) return;

    const textBeforeCaret = textBefore(content, range);
    const match = textBeforeCaret.match(/\[\[([^[\]]*)$/);
    if (!match) return;

    const query = match[1];
    // SearchWikiLinkSuggestions returns { id, title } pairs (not just titles) so the chosen
    // page's id is already in hand here - no second round-trip needed to resolve an href.
    state.dotNetRef.invokeMethodAsync('SearchWikiLinkSuggestions', query).then(suggestions => {
        closeWikiLinkMenu(state);
        if (!suggestions || suggestions.length === 0) return;

        const menu = document.createElement('div');
        menu.className = 'wiki-slash-menu list-group shadow-sm';
        positionMenu(menu, content);

        for (const suggestion of suggestions) {
            const option = document.createElement('button');
            option.type = 'button';
            option.className = 'list-group-item list-group-item-action py-1 px-2 small';
            option.textContent = suggestion.title;
            option.addEventListener('mousedown', event => {
                event.preventDefault();
                insertWikiLink(state, content, query, suggestion.id, suggestion.title);
                closeWikiLinkMenu(state);
            });
            menu.appendChild(option);
        }

        document.body.appendChild(menu);
        state.wikiLinkMenu = menu;
    }).catch(() => { /* circuit may be gone */ });
}

function insertWikiLink(state, content, query, pageId, title) {
    const range = getCaretRange(content);
    if (!range) return;
    const textBeforeCaret = textBefore(content, range);
    const start = textBeforeCaret.length - (query.length + 2);
    const deleteRange = document.createRange();
    const position = resolveTextOffset(content, Math.max(0, start));
    deleteRange.setStart(position.node, position.offset);
    deleteRange.setEnd(range.endContainer, range.endOffset);
    deleteRange.deleteContents();

    const anchor = document.createElement('a');
    anchor.href = `wikilink:${pageId}`;
    anchor.textContent = title;
    deleteRange.insertNode(anchor);
    anchor.after(document.createTextNode(' '));
    placeCaretAtTextOffset(content, content.textContent.length);
    scheduleNotify(state);
}

function closeWikiLinkMenu(state) {
    if (state.wikiLinkMenu) { state.wikiLinkMenu.remove(); state.wikiLinkMenu = null; }
}

// ---- Structured @person and @date mentions --------------------------------

function checkMentionTrigger(state, content) {
    const range = getCaretRange(content);
    closeMentionMenu(state);
    if (!range) return;

    const textBeforeCaret = textBefore(content, range);
    const match = textBeforeCaret.match(/(?:^|\s)@([\w.-]*)$/);
    if (!match) return;

    const query = match[1];
    state.dotNetRef.invokeMethodAsync('SearchMentionSuggestions', query).then(suggestions => {
        closeMentionMenu(state);
        if (!suggestions || suggestions.length === 0) return;

        const menu = document.createElement('div');
        menu.className = 'wiki-slash-menu list-group shadow-sm';
        positionMenu(menu, content);
        for (const suggestion of suggestions) {
            const option = document.createElement('button');
            option.type = 'button';
            option.className = 'list-group-item list-group-item-action py-1 px-2 small';
            option.innerHTML = `<span class="fw-semibold">${escapeHtml(suggestion.label)}</span>`
                + `<span class="text-secondary ms-2">${escapeHtml(suggestion.description)}</span>`;
            option.addEventListener('mousedown', event => {
                event.preventDefault();
                insertMention(state, content, query, suggestion);
                closeMentionMenu(state);
            });
            menu.appendChild(option);
        }

        document.body.appendChild(menu);
        state.mentionMenu = menu;
    }).catch(() => { /* circuit may be gone */ });
}

function insertMention(state, content, query, suggestion) {
    const range = getCaretRange(content);
    if (!range) return;
    const textBeforeCaret = textBefore(content, range);
    const start = textBeforeCaret.length - (query.length + 1);
    const deleteRange = document.createRange();
    const position = resolveTextOffset(content, Math.max(0, start));
    deleteRange.setStart(position.node, position.offset);
    deleteRange.setEnd(range.endContainer, range.endOffset);
    deleteRange.deleteContents();

    const anchor = document.createElement('a');
    anchor.href = `${suggestion.kind}mention:${suggestion.value}`;
    anchor.className = 'wiki-mention';
    anchor.textContent = suggestion.label;
    deleteRange.insertNode(anchor);
    anchor.after(document.createTextNode(' '));
    placeCaretAtTextOffset(content, content.textContent.length);
    scheduleNotify(state);
}

function closeMentionMenu(state) {
    if (state.mentionMenu) { state.mentionMenu.remove(); state.mentionMenu = null; }
}

function closeFloatingMenus(state, event) {
    if (event && (state.slashMenu?.contains(event.target) || state.wikiLinkMenu?.contains(event.target)
        || state.mentionMenu?.contains(event.target))) return;
    closeSlashMenu(state);
    closeWikiLinkMenu(state);
    closeMentionMenu(state);
}

function positionMenu(menu, anchorEl) {
    const rect = anchorEl.getBoundingClientRect();
    menu.style.position = 'absolute';
    menu.style.left = `${window.scrollX + rect.left}px`;
    menu.style.top = `${window.scrollY + rect.bottom}px`;
    menu.style.zIndex = '2000';
}

// ---- Drag-to-reorder (Pointer Events, matching automation-editor.js) -----

function onHandlePointerDown(state, event) {
    if (event.button !== 0) return;
    const handle = event.target.closest('.wiki-block-handle');
    if (!handle) return;
    const blockEl = handle.closest('.wiki-block');
    if (!blockEl) return;

    state.drag = { blockEl, pointerId: event.pointerId, placeholder: null };
    blockEl.classList.add('is-dragging');
    handle.setPointerCapture(event.pointerId);
    event.preventDefault();
}

function onHandlePointerMove(state, event) {
    if (!state.drag || state.drag.pointerId !== event.pointerId) return;
    const siblings = [...state.container.querySelectorAll('.wiki-block')].filter(el => el !== state.drag.blockEl);
    const target = siblings.find(el => {
        const rect = el.getBoundingClientRect();
        return event.clientY >= rect.top && event.clientY <= rect.bottom;
    });
    if (!target) return;

    const rect = target.getBoundingClientRect();
    const insertAfter = event.clientY > rect.top + rect.height / 2;
    if (insertAfter) target.after(state.drag.blockEl);
    else target.before(state.drag.blockEl);
}

function onHandlePointerUp(state, event) {
    if (!state.drag || state.drag.pointerId !== event.pointerId) return;
    state.drag.blockEl.classList.remove('is-dragging');
    state.drag = null;
    notifyChanged(state);
}

// ---- Serialization ---------------------------------------------------------

function scheduleNotify(state) {
    if (state.notifyTimer) clearTimeout(state.notifyTimer);
    state.notifyTimer = setTimeout(() => notifyChanged(state), 250);
}

function notifyChanged(state) {
    if (state.notifyTimer) { clearTimeout(state.notifyTimer); state.notifyTimer = null; }
    const blocks = [...state.container.querySelectorAll(':scope > .wiki-block')].map(serializeBlock);
    try { state.dotNetRef.invokeMethodAsync('OnBlocksChanged', JSON.stringify(blocks)); }
    catch { /* the Blazor circuit may have disconnected */ }
}

function serializeBlock(blockEl) {
    const type = blockEl.dataset.blockType;
    const props = {};
    if (type === 'to_do') props.checked = blockEl.dataset.checked === 'true' ? 'true' : 'false';
    if (type === 'image' || type === 'embed') props.url = blockEl.dataset.url || '';

    const contentEl = blockEl.querySelector('.wiki-block-content');
    return {
        id: blockEl.dataset.blockId,
        type,
        indentLevel: Number(blockEl.dataset.indent || '0'),
        richText: contentEl ? richTextFromNode(contentEl) : [],
        props
    };
}

function richTextFromNode(root) {
    const spans = [];
    walkRichText(root, {}, spans);
    return mergeAdjacentSpans(spans);
}

function walkRichText(node, marks, spans) {
    for (const child of node.childNodes) {
        if (child.nodeType === Node.TEXT_NODE) {
            if (child.textContent.length > 0) spans.push({ text: child.textContent, ...marks });
            continue;
        }
        if (child.nodeType !== Node.ELEMENT_NODE) continue;
        if (child.tagName === 'BR') { spans.push({ text: '\n', ...marks }); continue; }

        const nextMarks = { ...marks };
        const tag = child.tagName.toLowerCase();
        if (tag === 'b' || tag === 'strong') nextMarks.bold = true;
        else if (tag === 'i' || tag === 'em') nextMarks.italic = true;
        else if (tag === 's' || tag === 'strike' || tag === 'del') nextMarks.strikethrough = true;
        else if (tag === 'code') nextMarks.code = true;
        else if (tag === 'a') nextMarks.link = child.getAttribute('href') || '';
        walkRichText(child, nextMarks, spans);
    }
}

function marksEqual(a, b) {
    return !!a.bold === !!b.bold && !!a.italic === !!b.italic
        && !!a.strikethrough === !!b.strikethrough && !!a.code === !!b.code
        && (a.link || '') === (b.link || '');
}

function mergeAdjacentSpans(spans) {
    const merged = [];
    for (const span of spans) {
        const last = merged[merged.length - 1];
        if (last && marksEqual(last, span)) last.text += span.text;
        else merged.push({ ...span });
    }
    return merged;
}

function htmlFromRichText(spans) {
    return spans.map(span => {
        let html = escapeHtml(span.text).replace(/\n/g, '<br>');
        if (span.code) html = `<code>${html}</code>`;
        if (span.bold) html = `<b>${html}</b>`;
        if (span.italic) html = `<i>${html}</i>`;
        if (span.strikethrough) html = `<s>${html}</s>`;
        if (span.link) {
            const mentionClass = /^(user|date)mention:/i.test(span.link) ? ' class="wiki-mention"' : '';
            html = `<a${mentionClass} href="${escapeHtml(span.link)}">${html}</a>`;
        }
        return html;
    }).join('');
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text ?? '';
    return div.innerHTML;
}

// ---- Inline formatting -----------------------------------------------------

function toggleInlineTag(tagName, attributes) {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return;
    const range = selection.getRangeAt(0);

    const existing = findAncestorTag(range.commonAncestorContainer, tagName);
    if (existing) {
        const parent = existing.parentNode;
        while (existing.firstChild) parent.insertBefore(existing.firstChild, existing);
        parent.removeChild(existing);
        return;
    }

    const wrapper = document.createElement(tagName);
    if (attributes) for (const [key, value] of Object.entries(attributes)) wrapper.setAttribute(key, value);
    try {
        range.surroundContents(wrapper);
    } catch {
        const fragment = range.extractContents();
        wrapper.appendChild(fragment);
        range.insertNode(wrapper);
    }
    selection.removeAllRanges();
    const newRange = document.createRange();
    newRange.selectNodeContents(wrapper);
    selection.addRange(newRange);
}

function findAncestorTag(node, tagName) {
    let current = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    while (current && !current.classList?.contains('wiki-block-content')) {
        if (current.tagName && current.tagName.toLowerCase() === tagName) return current;
        current = current.parentElement;
    }
    return null;
}

// ---- Caret helpers ----------------------------------------------------------

function getCaretRange(content) {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return null;
    const range = selection.getRangeAt(0);
    return content.contains(range.startContainer) ? range : null;
}

function hasSelection() {
    const selection = window.getSelection();
    return selection && !selection.isCollapsed;
}

function isCaretAtStart(content) {
    const range = getCaretRange(content);
    if (!range) return false;
    const preRange = range.cloneRange();
    preRange.selectNodeContents(content);
    preRange.setEnd(range.startContainer, range.startOffset);
    return preRange.toString().length === 0;
}

function textBefore(content, range) {
    const preRange = range.cloneRange();
    preRange.selectNodeContents(content);
    preRange.setEnd(range.endContainer, range.endOffset);
    return preRange.toString();
}

function resolveTextOffset(root, offset) {
    let remaining = offset;
    let node = null;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
        node = walker.currentNode;
        if (remaining <= node.textContent.length) return { node, offset: remaining };
        remaining -= node.textContent.length;
    }
    return node ? { node, offset: node.textContent.length } : { node: root, offset: 0 };
}

function placeCaretAtTextOffset(content, offset) {
    const position = resolveTextOffset(content, offset);
    const range = document.createRange();
    range.setStart(position.node, position.offset);
    range.collapse(true);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    content.focus();
}

function focusBlock(blockEl) {
    const target = blockEl.querySelector('.wiki-block-content, input');
    if (target) target.focus();
}
