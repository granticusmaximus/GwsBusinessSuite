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
    { type: 'embed', label: 'Embed link', icon: '🔗' },
    { type: 'linked_database', label: 'Linked database', icon: '▦' }
];
const TEXTLESS_TYPES = new Set(['divider', 'image', 'embed', 'linked_database']);

export function initialize(container, dotNetRef, initialBlocksJson) {
    dispose(container);
    const state = {
        container,
        dotNetRef,
        drag: null,
        notifyTimer: null,
        slashMenu: null,
        wikiLinkMenu: null,
        mentionMenu: null,
        inlineToolbar: null,
        discussionCounts: new Map()
    };
    states.set(container, state);
    setBlocks(container, initialBlocksJson);

    container.addEventListener('pointerdown', event => onHandlePointerDown(state, event));
    container.addEventListener('pointermove', event => onHandlePointerMove(state, event));
    container.addEventListener('pointerup', event => onHandlePointerUp(state, event));
    container.addEventListener('pointercancel', event => onHandlePointerUp(state, event));
    container.addEventListener('mouseup', state.selectionHandler = () => showInlineToolbar(state));
    container.addEventListener('keyup', state.selectionHandler);
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

export function setDiscussionCounts(container, counts) {
    const state = states.get(container);
    if (!state) return;

    state.discussionCounts = new Map(
        Object.entries(counts || {}).map(([blockId, count]) => [blockId.toLowerCase(), Number(count) || 0]));
    for (const blockEl of container.querySelectorAll(':scope > .wiki-block')) {
        applyDiscussionCount(blockEl, state);
    }
}

export function dispose(container) {
    const state = states.get(container);
    if (!state) return;
    if (state.notifyTimer) clearTimeout(state.notifyTimer);
    closeFloatingMenus(state);
    if (state.selectionHandler) {
        container.removeEventListener('mouseup', state.selectionHandler);
        container.removeEventListener('keyup', state.selectionHandler);
    }
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
    if (block.type === 'linked_database') {
        el.dataset.databaseId = (block.props && block.props.databaseId) || '';
        el.dataset.databaseTitle = (block.props && block.props.databaseTitle) || '';
        el.dataset.databaseIcon = (block.props && block.props.databaseIcon) || '';
    }
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

    const discussionBtn = document.createElement('button');
    discussionBtn.type = 'button';
    discussionBtn.className = 'wiki-block-discussion';
    discussionBtn.addEventListener('mousedown', event => event.preventDefault());
    discussionBtn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        try { state.dotNetRef.invokeMethodAsync('OpenBlockDiscussion', block.id); }
        catch { /* the Blazor circuit may have disconnected */ }
    });

    gutter.append(addBtn, discussionBtn, handle);
    el.appendChild(gutter);
    el.appendChild(createBlockBody(block, state));
    applyDiscussionCount(el, state);
    return el;
}

function applyDiscussionCount(blockEl, state) {
    const button = blockEl.querySelector('.wiki-block-discussion');
    if (!button) return;

    const count = state.discussionCounts.get((blockEl.dataset.blockId || '').toLowerCase()) || 0;
    blockEl.querySelector('.wiki-block-gutter')?.classList.toggle('has-discussions', count > 0);
    button.classList.toggle('has-discussions', count > 0);
    button.textContent = count > 0 ? `💬 ${count}` : '💬';
    button.title = count > 0
        ? `Open ${count} block discussion${count === 1 ? '' : 's'}`
        : 'Start a discussion on this block';
    button.setAttribute('aria-label', button.title);
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

    if (block.type === 'linked_database') {
        body.appendChild(createLinkedDatabaseBody(block, state));
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
    wrapper.classList.toggle('has-source', Boolean(url));

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'form-control form-control-sm';
    input.placeholder = block.type === 'image' ? 'Paste an image URL and press Enter' : 'Paste a link and press Enter';
    input.value = url;
    input.setAttribute('aria-label', block.type === 'image' ? 'Image URL' : 'Embed URL');

    const preview = document.createElement('div');
    preview.className = 'wiki-media-preview';
    renderMediaPreview(preview, block.type, url);

    const commit = () => {
        const el = wrapper.closest('.wiki-block');
        el.dataset.url = input.value.trim();
        wrapper.classList.toggle('has-source', Boolean(input.value.trim()));
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
        img.className = 'wiki-media-image';
        img.alt = '';
        preview.appendChild(img);
    } else {
        const link = document.createElement('a');
        link.href = url;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.className = 'wiki-embed-link';
        link.textContent = url;
        preview.appendChild(link);
    }
}

function createLinkedDatabaseBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-linked-database-editor';
    let databaseId = (block.props && block.props.databaseId) || '';
    let databaseTitle = (block.props && block.props.databaseTitle) || '';
    let databaseIcon = (block.props && block.props.databaseIcon) || '';
    let searchGeneration = 0;

    const syncBlockDataset = () => {
        const blockEl = wrapper.closest('.wiki-block');
        if (!blockEl) return;
        blockEl.dataset.databaseId = databaseId;
        blockEl.dataset.databaseTitle = databaseTitle;
        blockEl.dataset.databaseIcon = databaseIcon;
    };

    const render = () => {
        wrapper.innerHTML = '';
        if (databaseId) {
            wrapper.classList.add('has-database');
            const card = document.createElement('button');
            card.type = 'button';
            card.className = 'wiki-linked-database-card';
            card.title = `Open ${databaseTitle || 'linked database'}`;

            const icon = document.createElement('span');
            icon.className = 'wiki-linked-database-icon';
            icon.textContent = databaseIcon || '▦';
            const label = document.createElement('span');
            label.className = 'wiki-linked-database-label';
            label.textContent = databaseTitle || 'Linked database';
            const arrow = document.createElement('span');
            arrow.className = 'wiki-linked-database-arrow';
            arrow.textContent = '↗';
            card.append(icon, label, arrow);
            card.addEventListener('click', () => {
                try { state.dotNetRef.invokeMethodAsync('OpenLinkedDatabase', databaseId); }
                catch { /* the Blazor circuit may have disconnected */ }
            });

            const change = document.createElement('button');
            change.type = 'button';
            change.className = 'wiki-linked-database-change';
            change.textContent = 'Change';
            change.addEventListener('click', () => {
                databaseId = '';
                databaseTitle = '';
                databaseIcon = '';
                syncBlockDataset();
                render();
                notifyChanged(state);
            });
            wrapper.append(card, change);
            return;
        }

        wrapper.classList.remove('has-database');
        const chooser = document.createElement('div');
        chooser.className = 'wiki-linked-database-chooser';
        const input = document.createElement('input');
        input.type = 'search';
        input.className = 'form-control form-control-sm';
        input.placeholder = 'Search databases to link…';
        input.setAttribute('aria-label', 'Search Sentinel databases');
        const results = document.createElement('div');
        results.className = 'wiki-linked-database-results';

        const search = query => {
            const generation = ++searchGeneration;
            state.dotNetRef.invokeMethodAsync('SearchLinkedDatabaseSuggestions', query).then(suggestions => {
                if (generation !== searchGeneration) return;
                results.innerHTML = '';
                for (const suggestion of suggestions || []) {
                    const option = document.createElement('button');
                    option.type = 'button';
                    option.className = 'wiki-linked-database-option';
                    const optionIcon = document.createElement('span');
                    optionIcon.textContent = suggestion.icon || '▦';
                    const optionTitle = document.createElement('span');
                    optionTitle.textContent = suggestion.title;
                    option.append(optionIcon, optionTitle);
                    option.addEventListener('click', () => {
                        databaseId = suggestion.id;
                        databaseTitle = suggestion.title;
                        databaseIcon = suggestion.icon || '';
                        syncBlockDataset();
                        render();
                        notifyChanged(state);
                    });
                    results.appendChild(option);
                }
                if (!suggestions || suggestions.length === 0) {
                    const empty = document.createElement('span');
                    empty.className = 'wiki-linked-database-empty';
                    empty.textContent = 'No databases found';
                    results.appendChild(empty);
                }
            }).catch(() => { results.innerHTML = ''; });
        };

        input.addEventListener('input', () => search(input.value.trim()));
        chooser.append(input, results);
        wrapper.appendChild(chooser);
        queueMicrotask(() => {
            input.focus();
            search('');
        });
    };

    render();
    return wrapper;
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
        || state.mentionMenu?.contains(event.target) || state.inlineToolbar?.contains(event.target))) return;
    closeSlashMenu(state);
    closeWikiLinkMenu(state);
    closeMentionMenu(state);
    closeInlineToolbar(state);
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
    if (type === 'linked_database') {
        props.databaseId = blockEl.dataset.databaseId || '';
        props.databaseTitle = blockEl.dataset.databaseTitle || '';
        props.databaseIcon = blockEl.dataset.databaseIcon || '';
    }

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

function showInlineToolbar(state) {
    closeInlineToolbar(state);
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return;

    const range = selection.getRangeAt(0);
    const anchor = range.commonAncestorContainer.nodeType === Node.ELEMENT_NODE
        ? range.commonAncestorContainer
        : range.commonAncestorContainer.parentElement;
    const content = anchor?.closest?.('.wiki-block-content');
    if (!content || !state.container.contains(content)) return;

    const toolbar = document.createElement('div');
    toolbar.className = 'wiki-inline-toolbar';
    toolbar.setAttribute('role', 'toolbar');
    toolbar.setAttribute('aria-label', 'Text formatting');

    const actions = [
        { label: 'B', title: 'Bold', tag: 'b', className: 'is-bold' },
        { label: 'I', title: 'Italic', tag: 'i', className: 'is-italic' },
        { label: 'S', title: 'Strikethrough', tag: 's', className: 'is-strike' },
        { label: '<>', title: 'Inline code', tag: 'code', className: 'is-code' }
    ];

    for (const action of actions) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = action.className;
        button.textContent = action.label;
        button.title = action.title;
        button.setAttribute('aria-label', action.title);
        button.addEventListener('mousedown', event => event.preventDefault());
        button.addEventListener('click', () => {
            toggleInlineTag(action.tag);
            scheduleNotify(state);
        });
        toolbar.appendChild(button);
    }

    const linkButton = document.createElement('button');
    linkButton.type = 'button';
    linkButton.innerHTML = '&#128279;';
    linkButton.title = 'Link';
    linkButton.setAttribute('aria-label', 'Link');
    linkButton.addEventListener('mousedown', event => event.preventDefault());
    linkButton.addEventListener('click', () => {
        const url = window.prompt('Link URL');
        if (url) {
            toggleInlineTag('a', { href: url });
            scheduleNotify(state);
        }
    });
    toolbar.appendChild(linkButton);

    document.body.appendChild(toolbar);
    const rect = range.getBoundingClientRect();
    const toolbarRect = toolbar.getBoundingClientRect();
    toolbar.style.left = `${window.scrollX + rect.left + (rect.width - toolbarRect.width) / 2}px`;
    toolbar.style.top = `${window.scrollY + rect.top - toolbarRect.height - 8}px`;
    state.inlineToolbar = toolbar;
}

function closeInlineToolbar(state) {
    if (state.inlineToolbar) { state.inlineToolbar.remove(); state.inlineToolbar = null; }
}

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
