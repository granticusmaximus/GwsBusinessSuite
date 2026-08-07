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
const HISTORY_STORAGE_PREFIX = 'sentinel:block-history:v1:';
const DRAFT_STORAGE_PREFIX = 'sentinel:block-draft:v1:';
const MAX_PERSISTED_HISTORY_CHARS = 1_500_000;
const MAX_PERSISTED_DRAFT_CHARS = 1_500_000;
const MAX_DRAFT_AGE_MS = 14 * 24 * 60 * 60 * 1000;
let suggestionMenuSequence = 0;

const BLOCK_TYPES = [
    { type: 'paragraph', label: 'Text', icon: '¶', group: 'Basic blocks', description: 'Start writing with plain text.', keywords: 'paragraph' },
    { type: 'heading_1', label: 'Heading 1', icon: 'H1', group: 'Basic blocks', description: 'Large section heading.', keywords: 'title' },
    { type: 'heading_2', label: 'Heading 2', icon: 'H2', group: 'Basic blocks', description: 'Medium section heading.', keywords: 'subtitle' },
    { type: 'heading_3', label: 'Heading 3', icon: 'H3', group: 'Basic blocks', description: 'Small section heading.', keywords: 'subtitle' },
    { type: 'bulleted_list_item', label: 'Bulleted list', icon: '•', group: 'Lists', description: 'Create a simple bulleted list.', keywords: 'unordered' },
    { type: 'numbered_list_item', label: 'Numbered list', icon: '1.', group: 'Lists', description: 'Create an ordered list.', keywords: 'ordered' },
    { type: 'to_do', label: 'To-do', icon: '☑', group: 'Lists', description: 'Track a task with a checkbox.', keywords: 'task checkbox' },
    { type: 'toggle', label: 'Toggle', icon: '▸', group: 'Lists', description: 'Hide content inside a collapsible branch.', keywords: 'details collapse' },
    { type: 'quote', label: 'Quote', icon: '❝', group: 'Advanced blocks', description: 'Capture a quotation.', keywords: 'blockquote' },
    { type: 'callout', label: 'Callout', icon: '💡', group: 'Advanced blocks', description: 'Emphasize an important note.', keywords: 'notice aside' },
    { type: 'code', label: 'Code', icon: '</>', group: 'Advanced blocks', description: 'Write a formatted code snippet.', keywords: 'preformatted' },
    { type: 'divider', label: 'Divider', icon: '—', group: 'Advanced blocks', description: 'Visually separate sections.', keywords: 'rule separator' },
    { type: 'equation', label: 'Equation', icon: '∑', group: 'Advanced blocks', description: 'Display a mathematical expression.', keywords: 'math formula' },
    { type: 'button', label: 'Button', icon: '▣', group: 'Advanced blocks', description: 'Add a prominent action.', keywords: 'action link' },
    { type: 'synced_block', label: 'Synced block', icon: '↻', group: 'Advanced blocks', description: 'Reuse synchronized content.', keywords: 'reusable' },
    { type: 'columns', label: 'Columns', icon: '▥', group: 'Advanced blocks', description: 'Lay content out side by side.', keywords: 'layout' },
    { type: 'image', label: 'Image', icon: '🖼', group: 'Media', description: 'Upload or link to an image.', keywords: 'photo picture' },
    { type: 'embed', label: 'Embed link', icon: '🔗', group: 'Media', description: 'Embed content from another site.', keywords: 'video url' },
    { type: 'linked_database', label: 'Linked database', icon: '▦', group: 'Data', description: 'Show an existing database view.', keywords: 'data view' },
    { type: 'inline_database', label: 'Inline database', icon: '▤', group: 'Data', description: 'Create a database inside this page.', keywords: 'data collection' },
    { type: 'table', label: 'Table', icon: '▦', group: 'Data', description: 'Add a simple table.', keywords: 'grid rows columns' },
    { type: 'breadcrumb', label: 'Breadcrumb', icon: '›', group: 'Page tools', description: 'Show this page’s location.', keywords: 'navigation path' },
    { type: 'table_of_contents', label: 'Table of contents', icon: '☷', group: 'Page tools', description: 'Link to headings on this page.', keywords: 'outline headings' }
];
const TEXTLESS_TYPES = new Set(['divider', 'image', 'embed', 'linked_database', 'inline_database', 'breadcrumb', 'table_of_contents']);
const RICH_TEXT_COLORS = ['gray', 'brown', 'orange', 'yellow', 'green', 'blue', 'purple', 'pink', 'red'];

export function initialize(container, dotNetRef, initialBlocksJson, historyKey = null) {
    dispose(container);
    const state = {
        container,
        dotNetRef,
        drag: null,
        notifyTimer: null,
        slashMenu: null,
        wikiLinkMenu: null,
        mentionMenu: null,
        activeSuggestionMenu: null,
        wikiLinkRequestId: 0,
        mentionRequestId: 0,
        inlineToolbar: null,
        blockMenu: null,
        discussionCounts: new Map(),
        // In-memory only (not persisted) - one entry per debounced edit burst or structural
        // op, same granularity as OnBlocksChanged. Cleared whenever setBlocks replaces the
        // document wholesale (initial load, or a Blazor-driven external reload like revert),
        // since undoing past that boundary would fight the server's own source of truth.
        undoStack: [],
        redoStack: [],
        lastSnapshot: undefined,
        baseSnapshot: undefined,
        historyKey: normalizeHistoryKey(historyKey),
        lastCursorKey: null,
        isOffline: !navigator.onLine,
        offlineBanner: null
    };
    states.set(container, state);
    setBlocks(container, initialBlocksJson, historyKey);

    container.addEventListener('pointerdown', event => onHandlePointerDown(state, event));
    container.addEventListener('pointermove', event => onHandlePointerMove(state, event));
    container.addEventListener('pointerup', event => onHandlePointerUp(state, event));
    container.addEventListener('pointercancel', event => onHandlePointerUp(state, event));
    container.addEventListener('click', state.linkClickHandler = event => {
        const anchor = wikiLinkAnchorFromEvent(event);
        if (!anchor) return;

        event.preventDefault();
        event.stopPropagation();
        const href = anchor.getAttribute('href');
        if (state.lastWikiLinkPointerNavigation?.href === href
            && performance.now() - state.lastWikiLinkPointerNavigation.at < 1000) {
            return;
        }
        navigateToWikiLink(state, anchor);
    });
    container.addEventListener('mouseup', state.selectionHandler = () => {
        showInlineToolbar(state);
        reportCursor(state);
    });
    container.addEventListener('keyup', state.selectionHandler);
    document.addEventListener('mousedown', state.outsideClickHandler = event => closeFloatingMenus(state, event));
    window.addEventListener('resize', state.resizeHandler = () => repositionSuggestionMenu(state));
    window.addEventListener('offline', state.offlineHandler = () => setOfflineState(state, true));
    window.addEventListener('online', state.onlineHandler = () => {
        setOfflineState(state, false);
        notifyChanged(state);
    });
    setOfflineState(state, state.isOffline);
    // Broadcast the active block plus character selection offsets. The offsets are visual
    // presence metadata only; block-level three-way merge remains the content safety boundary.
    container.addEventListener('focusin', state.focusInHandler = event => {
        reportCursor(state, event.target.closest('.wiki-block-content'));
    });
}

export function setBlocks(container, blocksJson, historyKey = null) {
    const state = states.get(container);
    if (!state) return;

    if (historyKey !== null && historyKey !== undefined) {
        state.historyKey = normalizeHistoryKey(historyKey);
    }
    renderBlocks(container, state, blocksJson);
    // An externally-driven document replacement (initial load, or a Blazor-triggered reload
    // such as revert-to-revision) is a new baseline, not an edit - nothing before it should
    // be undoable, and any pending redo would apply to a document that no longer exists.
    const incomingSnapshot = getBlocksJson(container);
    const persistedDraft = readPersistedDraft(state);
    let activeSnapshot = incomingSnapshot;
    if (persistedDraft?.baseSnapshot === incomingSnapshot
        && persistedDraft.snapshot !== incomingSnapshot) {
        renderBlocks(container, state, persistedDraft.snapshot);
        activeSnapshot = getBlocksJson(container);
        try { state.dotNetRef.invokeMethodAsync('OnDraftRecovered', activeSnapshot); }
        catch { /* the Blazor circuit may have disconnected */ }
    } else if (persistedDraft?.snapshot === incomingSnapshot) {
        clearPersistedDraft(state);
    }
    state.baseSnapshot = incomingSnapshot;
    const persisted = readPersistedHistory(state);
    state.lastSnapshot = activeSnapshot;
    if (persisted?.lastSnapshot === activeSnapshot) {
        state.undoStack = persisted.undoStack;
        state.redoStack = persisted.redoStack;
    } else {
        state.undoStack = [];
        state.redoStack = [];
    }
    persistHistory(state);
}

export function setHistoryKey(container, historyKey) {
    const state = states.get(container);
    if (!state) return;
    const previousStorageKey = historyStorageKey(state);
    state.historyKey = normalizeHistoryKey(historyKey);
    persistHistory(state);
    const nextStorageKey = historyStorageKey(state);
    if (previousStorageKey && previousStorageKey !== nextStorageKey) {
        try { sessionStorage.removeItem(previousStorageKey); } catch { /* storage may be unavailable */ }
    }
}

function renderBlocks(container, state, blocksJson) {
    let blocks;
    try { blocks = JSON.parse(blocksJson || '[]'); } catch { blocks = []; }
    if (!Array.isArray(blocks) || blocks.length === 0) {
        blocks = [emptyBlock('paragraph')];
    }

    container.innerHTML = '';
    for (const block of blocks) {
        container.appendChild(createBlockElement(block, state));
    }
    refreshBlockPresentation(container);
}

export function getBlocksJson(container) {
    return JSON.stringify(
        [...container.querySelectorAll(':scope > .wiki-block')].map(serializeBlock));
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

// Renders each active (unresolved) discussion's anchor as a highlighted overlay over the
// exact commented text - same non-destructive "positioned overlay, nothing inserted into the
// contenteditable DOM" technique setRemoteCursors already uses below, so a highlight can never
// leak into serialized page content or corrupt the rich-text span structure it visually sits
// over. Re-applied wherever setDiscussionCounts already is (initial load, block re-render,
// discussion list changes), since redrawing is exactly as cheap and keeps both in lockstep.
export function setDiscussionHighlights(container, highlightsByBlockId) {
    const state = states.get(container);
    if (!state) return;

    container.querySelectorAll(':scope > .wiki-block > .wiki-discussion-highlight').forEach(el => el.remove());
    for (const [blockId, highlights] of Object.entries(highlightsByBlockId || {})) {
        const blockEl = container.querySelector(`:scope > .wiki-block[data-block-id="${cssEscape(blockId)}"]`);
        const content = blockEl?.querySelector('.wiki-block-content');
        if (!content) continue;

        for (const highlight of highlights || []) {
            const start = Number.isInteger(highlight.start) ? Math.max(0, highlight.start) : null;
            const end = Number.isInteger(highlight.end) ? Math.max(start ?? 0, highlight.end) : start;
            if (start === null || end === start) continue;

            const startPoint = textPointAtOffset(content, start);
            const endPoint = textPointAtOffset(content, end);
            if (!startPoint || !endPoint) continue;

            const range = document.createRange();
            range.setStart(startPoint.node, startPoint.offset);
            range.setEnd(endPoint.node, endPoint.offset);
            if (range.collapsed) continue;

            const blockRect = blockEl.getBoundingClientRect();
            for (const rect of range.getClientRects()) {
                if (rect.width <= 0 || rect.height <= 0) continue;
                const overlay = document.createElement('span');
                overlay.className = 'wiki-discussion-highlight';
                overlay.title = 'Open discussion';
                overlay.style.left = `${rect.left - blockRect.left}px`;
                overlay.style.top = `${rect.top - blockRect.top}px`;
                overlay.style.width = `${rect.width}px`;
                overlay.style.height = `${rect.height}px`;
                overlay.addEventListener('click', () => {
                    try { state.dotNetRef.invokeMethodAsync('OpenDiscussionById', highlight.discussionId); }
                    catch { /* the Blazor circuit may have disconnected */ }
                });
                blockEl.appendChild(overlay);
            }
        }
    }
}

// Clears prior overlays and renders character selections/carets without inserting anything
// into the contenteditable DOM (so cursor UI can never leak into serialized page content).
export function setRemoteCursors(container, cursors) {
    const state = states.get(container);
    if (!state) return;

    container.querySelectorAll(':scope > .wiki-block > .wiki-remote-cursor, :scope > .wiki-block > .wiki-remote-selection').forEach(el => el.remove());
    for (const cursor of cursors || []) {
        const blockEl = container.querySelector(`:scope > .wiki-block[data-block-id="${cssEscape(cursor.blockId)}"]`);
        if (!blockEl) continue;
        const content = blockEl.querySelector('.wiki-block-content');
        const start = Number.isInteger(cursor.start) ? Math.max(0, cursor.start) : null;
        const end = Number.isInteger(cursor.end) ? Math.max(start ?? 0, cursor.end) : start;
        let caretRect = null;
        if (content && start !== null) {
            const startPoint = textPointAtOffset(content, start);
            const endPoint = textPointAtOffset(content, end ?? start);
            if (startPoint && endPoint) {
                const range = document.createRange();
                range.setStart(startPoint.node, startPoint.offset);
                range.setEnd(endPoint.node, endPoint.offset);
                const blockRect = blockEl.getBoundingClientRect();
                if (!range.collapsed) {
                    for (const rect of range.getClientRects()) {
                        if (rect.width <= 0 || rect.height <= 0) continue;
                        const highlight = document.createElement('span');
                        highlight.className = 'wiki-remote-selection';
                        highlight.style.setProperty('--wiki-remote-cursor-color', cursor.color || '#f59e0b');
                        highlight.style.left = `${rect.left - blockRect.left}px`;
                        highlight.style.top = `${rect.top - blockRect.top}px`;
                        highlight.style.width = `${rect.width}px`;
                        highlight.style.height = `${rect.height}px`;
                        blockEl.appendChild(highlight);
                        caretRect = rect;
                    }
                } else {
                    caretRect = range.getBoundingClientRect();
                }
            }
        }
        const marker = document.createElement('span');
        marker.className = 'wiki-remote-cursor';
        marker.style.setProperty('--wiki-remote-cursor-color', cursor.color || '#f59e0b');
        marker.textContent = cursor.username;
        marker.title = `${cursor.username} is editing here`;
        if (caretRect && (caretRect.width || caretRect.height)) {
            const blockRect = blockEl.getBoundingClientRect();
            marker.classList.add('is-character-cursor');
            marker.style.left = `${caretRect.right - blockRect.left}px`;
            marker.style.top = `${caretRect.top - blockRect.top}px`;
        }
        blockEl.appendChild(marker);
    }
}

function textPointAtOffset(content, requestedOffset) {
    const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
    let remaining = requestedOffset;
    let lastNode = null;
    while (walker.nextNode()) {
        const node = walker.currentNode;
        lastNode = node;
        if (remaining <= node.textContent.length) {
            return { node, offset: remaining };
        }
        remaining -= node.textContent.length;
    }
    return lastNode
        ? { node: lastNode, offset: lastNode.textContent.length }
        : { node: content, offset: 0 };
}

function reportCursor(state, preferredContent = null) {
    const selection = window.getSelection();
    const content = preferredContent
        || selection?.anchorNode?.parentElement?.closest?.('.wiki-block-content')
        || document.activeElement?.closest?.('.wiki-block-content');
    if (!content || !state.container.contains(content)) return;
    const blockEl = content.closest('.wiki-block');
    if (!blockEl) return;

    let start = 0;
    let end = 0;
    if (selection && selection.rangeCount > 0 && content.contains(selection.anchorNode) && content.contains(selection.focusNode)) {
        const range = selection.getRangeAt(0);
        const beforeStart = range.cloneRange();
        beforeStart.selectNodeContents(content);
        beforeStart.setEnd(range.startContainer, range.startOffset);
        start = beforeStart.toString().length;
        end = start + range.toString().length;
    }
    const key = `${blockEl.dataset.blockId}:${start}:${end}`;
    if (key === state.lastCursorKey) return;
    state.lastCursorKey = key;
    try { state.dotNetRef.invokeMethodAsync('OnCursorMoved', blockEl.dataset.blockId, start, end); }
    catch { /* the Blazor circuit may have disconnected */ }
}

function cssEscape(value) {
    return typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(value) : String(value).replace(/["\\]/g, '\\$&');
}

function setOfflineState(state, offline) {
    state.isOffline = offline;
    if (offline) {
        if (!state.offlineBanner) {
            const banner = document.createElement('div');
            banner.className = 'wiki-offline-banner';
            banner.setAttribute('role', 'status');
            banner.textContent = 'Offline — edits are saved safely in this browser and will sync when you reconnect.';
            state.container.before(banner);
            state.offlineBanner = banner;
        }
        return;
    }
    state.offlineBanner?.remove();
    state.offlineBanner = null;
}

export function dispose(container) {
    const state = states.get(container);
    if (!state) return;
    if (state.notifyTimer) clearTimeout(state.notifyTimer);
    closeFloatingMenus(state);
    if (state.focusInHandler) container.removeEventListener('focusin', state.focusInHandler);
    if (state.linkClickHandler) container.removeEventListener('click', state.linkClickHandler);
    if (state.selectionHandler) {
        container.removeEventListener('mouseup', state.selectionHandler);
        container.removeEventListener('keyup', state.selectionHandler);
    }
    if (state.outsideClickHandler) document.removeEventListener('mousedown', state.outsideClickHandler);
    if (state.resizeHandler) window.removeEventListener('resize', state.resizeHandler);
    if (state.offlineHandler) window.removeEventListener('offline', state.offlineHandler);
    if (state.onlineHandler) window.removeEventListener('online', state.onlineHandler);
    state.offlineBanner?.remove();
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
    el.dataset.propsJson = JSON.stringify(block.props || {});
    if (block.props && block.props.notionChildPage === 'true') {
        el.classList.add('wiki-notion-child-page-link');
    }
    if (block.type === 'to_do' && block.props && block.props.checked === 'true') el.dataset.checked = 'true';
    if (block.type === 'numbered_list_item') el.dataset.number = (block.props && block.props.number) || '';
    if (block.type === 'toggle') el.dataset.open = block.props && block.props.open === 'true' ? 'true' : 'false';
    if (block.type === 'image' || block.type === 'embed') {
        el.dataset.url = (block.props && block.props.url) || '';
        el.dataset.fileName = (block.props && block.props.fileName) || '';
        el.dataset.notionBlockId = (block.props && block.props.notionBlockId) || '';
        el.dataset.mediaKind = (block.props && block.props.mediaKind) || '';
    }
    if (block.type === 'linked_database' || block.type === 'inline_database') {
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
        const block = emptyBlock('paragraph');
        block.indentLevel = blockIndent(el);
        const created = createBlockElement(block, state);
        const branch = getBlockBranch(el);
        branch[branch.length - 1].after(created);
        refreshBlockPresentation(state.container);
        focusBlock(created);
        notifyChanged(state);
    });

    const handle = document.createElement('span');
    handle.className = 'wiki-block-handle';
    handle.title = 'Drag to reorder';
    handle.textContent = '⠿';

    const menuBtn = document.createElement('button');
    menuBtn.type = 'button';
    menuBtn.className = 'wiki-block-menu-toggle';
    menuBtn.title = 'Block actions';
    menuBtn.setAttribute('aria-label', 'Block actions');
    menuBtn.textContent = '⋮';
    menuBtn.addEventListener('mousedown', event => event.preventDefault());
    menuBtn.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        openBlockMenu(state, el, menuBtn);
    });

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

    gutter.append(addBtn, discussionBtn, menuBtn, handle);
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

    if (block.type === 'table') {
        body.appendChild(createTableBody(block, state));
        return body;
    }

    if (block.type === 'columns') {
        body.appendChild(createColumnsBody(block, state));
        return body;
    }

    if (block.type === 'breadcrumb' || block.type === 'table_of_contents') {
        const placeholder = document.createElement('div');
        placeholder.className = `wiki-${block.type.replaceAll('_', '-')}`;
        placeholder.textContent = block.type === 'breadcrumb' ? 'Workspace / Parent page / Current page' : 'Table of contents';
        body.appendChild(placeholder);
        return body;
    }

    if (block.type === 'image' || block.type === 'embed') {
        body.appendChild(createMediaBody(block, state));
        return body;
    }

    if (block.type === 'linked_database' || block.type === 'inline_database') {
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

    if (block.type === 'bulleted_list_item' || block.type === 'numbered_list_item') {
        const bullet = document.createElement('span');
        bullet.className = 'wiki-list-marker';
        bullet.textContent = block.type === 'bulleted_list_item' ? '•' : `${(block.props && block.props.number) || '1'}.`;
        body.appendChild(bullet);
    }

    if (block.type === 'toggle') {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'wiki-toggle-button';
        toggle.title = 'Expand or collapse';
        toggle.setAttribute('aria-label', toggle.title);
        toggle.addEventListener('click', () => {
            const blockEl = toggle.closest('.wiki-block');
            blockEl.dataset.open = blockEl.dataset.open === 'true' ? 'false' : 'true';
            refreshBlockPresentation(state.container);
            notifyChanged(state);
        });
        body.appendChild(toggle);
    }

    if (block.type === 'callout') {
        const icon = document.createElement('span');
        icon.className = 'wiki-callout-icon';
        icon.textContent = (block.props && block.props.icon) || '💡';
        body.appendChild(icon);
    }

    if (block.type === 'code' && block.props && block.props.language) {
        const language = document.createElement('span');
        language.className = 'wiki-code-language';
        language.textContent = block.props.language;
        body.appendChild(language);
    }

    body.appendChild(createContentEditable(block, state));
    return body;
}

function createTableBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-native-table-editor';
    const rows = parseTableRows(block);
    const columnCount = Math.max(1, ...rows.map(row => row.length));
    if (rows.length === 0) rows.push(Array.from({ length: columnCount }, () => []));
    for (const row of rows) {
        while (row.length < columnCount) row.push([]);
    }

    const table = document.createElement('table');
    table.className = 'wiki-native-table';
    const hasColumnHeader = !block.props || block.props.hasColumnHeader !== 'false';
    rows.forEach((row, rowIndex) => {
        const tr = document.createElement('tr');
        row.forEach(spans => {
            const cell = document.createElement(hasColumnHeader && rowIndex === 0 ? 'th' : 'td');
            cell.contentEditable = 'plaintext-only' in document.body ? 'plaintext-only' : 'true';
            cell.innerHTML = htmlFromRichText(spans);
            cell.addEventListener('keydown', event => {
                if (event.key === 'Enter') event.preventDefault();
            });
            cell.addEventListener('input', () => scheduleNotify(state));
            tr.appendChild(cell);
        });
        table.appendChild(tr);
    });

    const actions = document.createElement('div');
    actions.className = 'wiki-native-table-actions';
    const addRow = document.createElement('button');
    addRow.type = 'button';
    addRow.textContent = '+ row';
    addRow.addEventListener('click', () => {
        const tr = document.createElement('tr');
        for (let index = 0; index < table.rows[0].cells.length; index++) {
            const cell = document.createElement('td');
            cell.contentEditable = 'plaintext-only' in document.body ? 'plaintext-only' : 'true';
            cell.addEventListener('keydown', event => {
                if (event.key === 'Enter') event.preventDefault();
            });
            cell.addEventListener('input', () => scheduleNotify(state));
            tr.appendChild(cell);
        }
        table.appendChild(tr);
        notifyChanged(state);
    });
    const addColumn = document.createElement('button');
    addColumn.type = 'button';
    addColumn.textContent = '+ column';
    addColumn.addEventListener('click', () => {
        [...table.rows].forEach((row, rowIndex) => {
            const cell = document.createElement(hasColumnHeader && rowIndex === 0 ? 'th' : 'td');
            cell.contentEditable = 'plaintext-only' in document.body ? 'plaintext-only' : 'true';
            cell.addEventListener('keydown', event => {
                if (event.key === 'Enter') event.preventDefault();
            });
            cell.addEventListener('input', () => scheduleNotify(state));
            row.appendChild(cell);
        });
        notifyChanged(state);
    });
    actions.append(addRow, addColumn);
    wrapper.append(table, actions);
    return wrapper;
}

function createColumnsBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-columns-editor';
    if (block.props && block.props.notionPageLinkColumns === 'true') {
        wrapper.classList.add('wiki-notion-page-link-columns');
    }
    const text = (block.richText || []).map(span => span.text || '').join('');
    const fallbackColumns = (text || 'Column one ||| Column two')
        .split('|||', 5)
        .map(column => column.trim());
    let columns = fallbackColumns.map(column => [{ text: column }]);
    try {
        const richColumns = JSON.parse((block.props && block.props.columnRichTextJson) || '[]');
        if (Array.isArray(richColumns) && richColumns.length > 0) columns = richColumns;
    } catch { /* use the plain-text fallback from older Sentinel versions */ }
    while (columns.length < 2) columns.push('');

    const renderColumn = value => {
        const column = document.createElement('section');
        column.className = 'wiki-column-editor';
        const controls = document.createElement('div');
        controls.className = 'wiki-column-controls';

        const moveLeft = document.createElement('button');
        moveLeft.type = 'button';
        moveLeft.title = 'Move column left';
        moveLeft.setAttribute('aria-label', 'Move column left');
        moveLeft.textContent = '←';
        moveLeft.addEventListener('click', () => {
            const previous = column.previousElementSibling;
            if (!previous?.classList.contains('wiki-column-editor')) return;
            wrapper.insertBefore(column, previous);
            refreshColumnControls(wrapper);
            notifyChanged(state);
        });

        const moveRight = document.createElement('button');
        moveRight.type = 'button';
        moveRight.title = 'Move column right';
        moveRight.setAttribute('aria-label', 'Move column right');
        moveRight.textContent = '→';
        moveRight.addEventListener('click', () => {
            const next = column.nextElementSibling;
            if (!next?.classList.contains('wiki-column-editor')) return;
            wrapper.insertBefore(next, column);
            refreshColumnControls(wrapper);
            notifyChanged(state);
        });

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.title = 'Remove column';
        remove.setAttribute('aria-label', 'Remove column');
        remove.textContent = '×';
        remove.addEventListener('click', () => {
            if (wrapper.querySelectorAll(':scope > .wiki-column-editor').length <= 2) return;
            column.remove();
            refreshColumnControls(wrapper);
            notifyChanged(state);
        });

        const content = document.createElement('div');
        content.className = 'wiki-block-content wiki-column-content';
        content.contentEditable = 'true';
        content.innerHTML = Array.isArray(value)
            ? htmlFromRichText(value)
            : htmlFromRichText([{ text: value || '' }]);
        content.dataset.placeholder = 'Type in this column';
        content.addEventListener('input', () => scheduleNotify(state));
        controls.append(moveLeft, moveRight, remove);
        column.append(controls, content);
        return column;
    };

    columns.forEach(column => wrapper.appendChild(renderColumn(column)));
    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'wiki-column-add';
    add.textContent = '+ Add column';
    add.addEventListener('click', () => {
        if (wrapper.querySelectorAll(':scope > .wiki-column-editor').length >= 5) return;
        wrapper.insertBefore(renderColumn(''), add);
        refreshColumnControls(wrapper);
        notifyChanged(state);
    });
    wrapper.appendChild(add);
    refreshColumnControls(wrapper);
    return wrapper;
}

function refreshColumnControls(wrapper) {
    const columns = [...wrapper.querySelectorAll(':scope > .wiki-column-editor')];
    columns.forEach((column, index) => {
        column.querySelector('[aria-label="Move column left"]').disabled = index === 0;
        column.querySelector('[aria-label="Move column right"]').disabled = index === columns.length - 1;
        column.querySelector('[aria-label="Remove column"]').disabled = columns.length <= 2;
    });
    const add = wrapper.querySelector(':scope > .wiki-column-add');
    if (add) add.disabled = columns.length >= 5;
}

function parseTableRows(block) {
    try {
        const richRows = JSON.parse((block.props && block.props.tableJson) || '[]');
        if (Array.isArray(richRows) && richRows.length > 0) return richRows;
    } catch { /* use the text fallback written by earlier Sentinel versions */ }

    const text = (block.richText || []).map(span => span.text || '').join('');
    return (text || '').split('\n')
        .map(line => line.split('|').map(cell => [{ text: cell.trim() }]))
        .filter(row => row.some(cell => cell.some(span => span.text.length > 0)));
}

function serializeTable(blockEl) {
    return [...blockEl.querySelectorAll('.wiki-native-table tr')]
        .map(row => [...row.cells].map(cell => cell.textContent.trim()).join(' | '))
        .join('\n');
}

function serializeTableRichText(blockEl) {
    return [...blockEl.querySelectorAll('.wiki-native-table tr')]
        .map(row => [...row.cells].map(cell => richTextFromNode(cell)));
}

function refreshBlockPresentation(container) {
    refreshNumberedListMarkers(container);
    refreshToggleVisibility(container);
}

function refreshNumberedListMarkers(container) {
    const counters = new Map();
    let previousType = '';
    let previousIndent = -1;
    for (const block of container.querySelectorAll(':scope > .wiki-block')) {
        const type = block.dataset.blockType;
        const indent = Number(block.dataset.indent || '0');
        if (type !== 'numbered_list_item') {
            counters.clear();
            previousType = type;
            previousIndent = indent;
            continue;
        }

        if (previousType !== 'numbered_list_item' || indent > previousIndent) {
            const requested = Number(block.dataset.number || '1');
            counters.set(indent, Number.isFinite(requested) && requested > 0 ? requested : 1);
        } else {
            counters.set(indent, (counters.get(indent) || 0) + 1);
        }
        for (const key of [...counters.keys()]) {
            if (key > indent) counters.delete(key);
        }
        const marker = block.querySelector('.wiki-list-marker');
        if (marker) marker.textContent = `${counters.get(indent)}.`;
        previousType = type;
        previousIndent = indent;
    }
}

function refreshToggleVisibility(container) {
    const blocks = [...container.querySelectorAll(':scope > .wiki-block')];
    blocks.forEach(block => block.classList.remove('is-toggle-hidden'));
    blocks.forEach((block, index) => {
        if (block.dataset.blockType !== 'toggle') return;
        const isOpen = block.dataset.open === 'true';
        const button = block.querySelector('.wiki-toggle-button');
        if (button) {
            button.textContent = isOpen ? '▾' : '▸';
            button.setAttribute('aria-expanded', String(isOpen));
        }
        if (isOpen) return;

        const indent = Number(block.dataset.indent || '0');
        for (let childIndex = index + 1; childIndex < blocks.length; childIndex++) {
            const childIndent = Number(blocks[childIndex].dataset.indent || '0');
            if (childIndent <= indent) break;
            blocks[childIndex].classList.add('is-toggle-hidden');
        }
    });
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
    renderMediaPreview(preview, block.type, url, (block.props && block.props.fileName) || '', (block.props && block.props.mediaKind) || '');

    const commit = () => {
        const el = wrapper.closest('.wiki-block');
        el.dataset.url = input.value.trim();
        wrapper.classList.toggle('has-source', Boolean(input.value.trim()));
        renderMediaPreview(preview, block.type, input.value.trim(), el?.dataset.fileName || '', el?.dataset.mediaKind || '');
        notifyChanged(state);
    };
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') { event.preventDefault(); commit(); }
    });
    input.addEventListener('blur', commit);

    wrapper.append(input, preview);
    return wrapper;
}

// Mirrors GwsBusinessSuite.Application.Wiki.WikiEmbedResolver so the live editor preview
// matches the server-rendered page - keep both in sync when adding a provider. Every pattern
// is anchored to a fixed hostname; an unrecognized URL always falls back to a plain link.
const EMBED_PROVIDERS = [
    { name: 'YouTube', pattern: /^https?:\/\/(?:www\.)?(?:youtube\.com\/watch\?v=|youtube\.com\/embed\/|youtu\.be\/)([\w-]{6,})/i,
        embedUrl: match => `https://www.youtube.com/embed/${match[1]}` },
    { name: 'Vimeo', pattern: /^https?:\/\/(?:www\.)?vimeo\.com\/(\d+)/i,
        embedUrl: match => `https://player.vimeo.com/video/${match[1]}` },
    { name: 'Spotify', pattern: /^https?:\/\/open\.spotify\.com\/(track|album|playlist|episode|show|artist)\/([\w]+)/i,
        embedUrl: match => `https://open.spotify.com/embed/${match[1]}/${match[2]}` },
    { name: 'Figma', pattern: /^https?:\/\/(?:www\.)?figma\.com\/(?:file|proto|design)\/[\w-]+\//i,
        embedUrl: (match, url) => `https://www.figma.com/embed?embed_host=sentinel&url=${encodeURIComponent(url)}` },
    { name: 'CodePen', pattern: /^https?:\/\/codepen\.io\/([\w-]+)\/pen\/([\w-]+)/i,
        embedUrl: match => `https://codepen.io/${match[1]}/embed/${match[2]}?default-tab=result` },
    { name: 'Loom', pattern: /^https?:\/\/(?:www\.)?loom\.com\/share\/([\w]+)/i,
        embedUrl: match => `https://www.loom.com/embed/${match[1]}` }
];

function resolveEmbedUrl(url) {
    if (!url) return null;
    const trimmed = url.trim();
    for (const provider of EMBED_PROVIDERS) {
        const match = trimmed.match(provider.pattern);
        if (match) return { embedUrl: provider.embedUrl(match, trimmed), name: provider.name };
    }
    return null;
}

function renderMediaPreview(preview, type, url, fileName = '', mediaKind = '') {
    preview.innerHTML = '';
    if (!url) return;
    const safeUrl = safeMediaHref(url);
    if (!safeUrl) return;
    if (type === 'image') {
        const img = document.createElement('img');
        img.src = safeUrl;
        img.loading = 'lazy';
        img.className = 'wiki-media-image';
        img.alt = '';
        preview.appendChild(img);
        return;
    }

    // Set only on blocks imported from Notion's video/audio types (NotionMapping.MapBlock) -
    // Sentinel has no dedicated block type for these, but an inline player reads far better
    // than a bare link for the two kinds a <video>/<audio> tag can actually play.
    if (mediaKind === 'video' || mediaKind === 'audio') {
        const player = document.createElement(mediaKind);
        player.className = 'wiki-embed-media';
        player.src = safeUrl;
        player.controls = true;
        player.preload = 'metadata';
        preview.appendChild(player);
        return;
    }

    const resolved = resolveEmbedUrl(safeUrl);
    if (resolved) {
        const frame = document.createElement('div');
        frame.className = 'wiki-embed-frame';
        frame.dataset.provider = resolved.name;
        const iframe = document.createElement('iframe');
        iframe.src = resolved.embedUrl;
        iframe.loading = 'lazy';
        iframe.allowFullscreen = true;
        iframe.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-popups allow-presentation');
        iframe.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        frame.appendChild(iframe);
        preview.appendChild(frame);
        return;
    }

    const link = document.createElement('a');
    link.href = safeUrl;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    link.className = 'wiki-embed-link';
    link.textContent = fileName || safeUrl;
    if (fileName) link.title = safeUrl;
    preview.appendChild(link);
}

function createLinkedDatabaseBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-linked-database-editor';
    const isInline = block.type === 'inline_database';
    wrapper.classList.toggle('is-inline', isInline);
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
            if (isInline) {
                renderInlineDatabase(wrapper, state, databaseId, () => {
                    databaseId = '';
                    databaseTitle = '';
                    databaseIcon = '';
                    syncBlockDataset();
                    render();
                    notifyChanged(state);
                });
                return;
            }
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
        input.placeholder = isInline ? 'Search databases to show inline…' : 'Search databases to link…';
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

function renderInlineDatabase(wrapper, state, databaseId, resetDatabase) {
    wrapper.innerHTML = '<div class="wiki-inline-database-loading">Loading database…</div>';
    state.dotNetRef.invokeMethodAsync('GetInlineDatabase', databaseId).then(snapshot => {
        if (!snapshot) {
            wrapper.innerHTML = '<div class="wiki-inline-database-error">This database is no longer available.</div>';
            return;
        }
        renderInlineDatabaseSnapshot(wrapper, state, snapshot, resetDatabase);
    }).catch(() => {
        wrapper.innerHTML = '<div class="wiki-inline-database-error">Unable to load this database.</div>';
    });
}

function renderInlineDatabaseSnapshot(wrapper, state, snapshot, resetDatabase, selectedViewId = null) {
    wrapper.innerHTML = '';
    const views = snapshot.views || [];
    const activeView = views.find(view => view.id === selectedViewId) || views[0] || null;
    const selectProperties = (snapshot.properties || [])
        .filter(property => property.type === 'select');
    const boardGroupByPropertyId = activeView?.groupByPropertyId
        || (activeView?.type === 'board' && selectProperties.length === 1
            ? selectProperties[0].id
            : null);
    const header = document.createElement('div');
    header.className = 'wiki-inline-database-header';
    const identity = document.createElement('button');
    identity.type = 'button';
    identity.className = 'wiki-inline-database-identity';
    const identityIcon = document.createElement('span');
    identityIcon.textContent = snapshot.icon || '▤';
    const identityTitle = document.createElement('strong');
    identityTitle.textContent = snapshot.title;
    identity.append(identityIcon, identityTitle);
    identity.addEventListener('click', () => state.dotNetRef.invokeMethodAsync('OpenLinkedDatabase', snapshot.id));

    const headerActions = document.createElement('div');
    headerActions.className = 'wiki-inline-database-actions';
    const open = document.createElement('button');
    open.type = 'button';
    open.textContent = 'Open';
    open.addEventListener('click', () => state.dotNetRef.invokeMethodAsync('OpenLinkedDatabase', snapshot.id));
    const change = document.createElement('button');
    change.type = 'button';
    change.textContent = 'Change source';
    change.addEventListener('click', resetDatabase);
    headerActions.append(open, change);
    header.append(identity, headerActions);

    const viewTabs = document.createElement('div');
    viewTabs.className = 'wiki-inline-database-views';
    for (const view of views) {
        const tab = document.createElement('button');
        tab.type = 'button';
        tab.className = 'wiki-inline-database-view';
        tab.classList.toggle('is-active', view.id === activeView?.id);
        tab.textContent = view.name || view.type;
        tab.addEventListener('click', () =>
            renderInlineDatabaseSnapshot(wrapper, state, snapshot, resetDatabase, view.id));
        viewTabs.appendChild(tab);
    }

    const scroller = document.createElement('div');
    scroller.className = 'wiki-inline-database-scroller';
    const isBoard = activeView?.type === 'board' && boardGroupByPropertyId;
    if (isBoard) {
        scroller.appendChild(createInlineBoard(
            snapshot,
            boardGroupByPropertyId,
            rowId => state.dotNetRef.invokeMethodAsync('OpenLinkedDatabaseRow', snapshot.id, rowId),
            async (rowId, optionId, newSortOrder) => {
                const updated = await state.dotNetRef.invokeMethodAsync(
                    'MoveInlineDatabaseRow',
                    snapshot.id,
                    rowId,
                    boardGroupByPropertyId,
                    optionId || null,
                    newSortOrder);
                if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id);
            },
            async (optionId, title) => {
                const updated = await state.dotNetRef.invokeMethodAsync(
                    'AddInlineBoardTask',
                    snapshot.id,
                    boardGroupByPropertyId,
                    optionId || null,
                    title);
                if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id);
            }));
    } else {
        // Every non-board view (List, Calendar, Gallery, Timeline, etc.) renders through this
        // shared table fallback, so fixing the row-open affordance here covers all of them at
        // once. Cells stay click-to-edit (createInlineCellEditor); a dedicated leading column
        // opens the full row page, mirroring what the board's card click already does, rather
        // than making the whole <tr> clickable and fighting the per-cell editors for clicks.
        const openRow = rowId => state.dotNetRef.invokeMethodAsync('OpenLinkedDatabaseRow', snapshot.id, rowId);
        const table = document.createElement('table');
        table.className = 'wiki-inline-database-table';
        const thead = document.createElement('thead');
        const headingRow = document.createElement('tr');
        const openHeading = document.createElement('th');
        openHeading.className = 'wiki-inline-database-open-column';
        headingRow.appendChild(openHeading);
        for (const property of snapshot.properties) {
            const heading = document.createElement('th');
            heading.textContent = property.name;
            heading.title = property.type;
            headingRow.appendChild(heading);
        }
        thead.appendChild(headingRow);
        table.appendChild(thead);

        const tbody = document.createElement('tbody');
        for (const row of snapshot.rows) {
            const tableRow = document.createElement('tr');
            const openCell = document.createElement('td');
            openCell.className = 'wiki-inline-database-open-column';
            const openButton = document.createElement('button');
            openButton.type = 'button';
            openButton.className = 'wiki-inline-database-open-row';
            openButton.title = 'Open row';
            openButton.setAttribute('aria-label', 'Open row');
            openButton.innerHTML = '<i class="bi bi-arrows-angle-expand"></i>';
            openButton.addEventListener('click', () => openRow(row.id));
            openCell.appendChild(openButton);
            tableRow.appendChild(openCell);
            for (const property of snapshot.properties) {
                const cell = document.createElement('td');
                const value = row.cells.find(item => item.propertyId === property.id)?.value || '';
                cell.appendChild(createInlineCellEditor(state, snapshot.id, row.id, property, value, updated => {
                    if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id);
                }));
                tableRow.appendChild(cell);
            }
            tbody.appendChild(tableRow);
        }
        table.appendChild(tbody);
        scroller.appendChild(table);
    }

    wrapper.append(header);
    if (views.length > 0) wrapper.append(viewTabs);
    wrapper.append(scroller);
    if (!isBoard) {
        const footer = document.createElement('div');
        footer.className = 'wiki-inline-database-footer';
        const addRow = document.createElement('button');
        addRow.type = 'button';
        addRow.innerHTML = '<span>+</span> New row';
        addRow.addEventListener('click', async () => {
            addRow.disabled = true;
            try {
                const updated = await state.dotNetRef.invokeMethodAsync('AddInlineDatabaseRow', snapshot.id);
                if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id);
            } finally {
                addRow.disabled = false;
            }
        });
        footer.appendChild(addRow);
        wrapper.append(footer);
    }
}

function createInlineBoard(snapshot, groupByPropertyId, openRow, moveRow, addTask) {
    const board = document.createElement('div');
    board.className = 'wiki-inline-database-board';
    const groupProperty = snapshot.properties.find(property => property.id === groupByPropertyId);
    const titleProperty = snapshot.properties.find(property => property.type === 'title')
        || snapshot.properties[0];
    const options = [...(groupProperty?.options || [])];
    options.push({ id: '', label: 'No status' });

    for (const option of options) {
        const rows = snapshot.rows.filter(row =>
            (row.cells.find(cell => cell.propertyId === groupByPropertyId)?.value || '') === option.id);
        if (option.id === '' && rows.length === 0) continue;
        const column = document.createElement('section');
        column.className = 'wiki-inline-board-column';
        const heading = document.createElement('div');
        heading.className = 'wiki-inline-board-heading';
        const label = document.createElement('span');
        label.className = 'wiki-inline-board-status';
        label.dataset.color = option.color || 'default';
        if (/^#[0-9a-f]{6}$/i.test(option.color || '')) {
            label.style.backgroundColor = `${option.color}45`;
            label.style.color = option.color;
        }
        label.textContent = option.label || 'No status';
        const count = document.createElement('span');
        count.textContent = String(rows.length);
        heading.append(label, count);
        column.appendChild(heading);

        const cards = document.createElement('div');
        cards.className = 'wiki-inline-board-cards';
        cards.addEventListener('dragover', event => {
            event.preventDefault();
            if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
            column.classList.add('is-drop-target');
        });
        cards.addEventListener('dragleave', event => {
            if (!cards.contains(event.relatedTarget)) column.classList.remove('is-drop-target');
        });
        cards.addEventListener('drop', async event => {
            event.preventDefault();
            column.classList.remove('is-drop-target');
            const rowId = event.dataTransfer?.getData('text/plain');
            if (!rowId) return;

            const targetCards = [...cards.querySelectorAll('.wiki-inline-board-card:not(.is-dragging)')];
            const targetIndex = targetCards.findIndex(card => {
                const rect = card.getBoundingClientRect();
                return event.clientY < rect.top + (rect.height / 2);
            });
            column.classList.add('is-saving');
            try {
                await moveRow(rowId, option.id, targetIndex < 0 ? targetCards.length : targetIndex);
            } finally {
                column.classList.remove('is-saving');
            }
        });

        for (const row of rows) {
            const card = document.createElement('button');
            card.type = 'button';
            card.className = 'wiki-inline-board-card';
            card.draggable = true;
            card.dataset.rowId = row.id;
            const title = document.createElement('strong');
            title.textContent = row.cells.find(cell => cell.propertyId === titleProperty?.id)?.value || 'Untitled';
            card.appendChild(title);
            const visibleProperties = snapshot.properties
                .filter(property => property.id !== titleProperty?.id && property.id !== groupByPropertyId)
                .map(property => ({
                    name: property.name,
                    value: row.cells.find(cell => cell.propertyId === property.id)?.value || ''
                }))
                .filter(item => item.value)
                .slice(0, 3);
            for (const property of visibleProperties) {
                const detail = document.createElement('span');
                detail.className = 'wiki-inline-board-card-detail';
                detail.textContent = `${property.name}: ${property.value}`;
                card.appendChild(detail);
            }
            card.addEventListener('dragstart', event => {
                card.classList.add('is-dragging');
                event.dataTransfer?.setData('text/plain', row.id);
                if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
            });
            card.addEventListener('dragend', () => {
                card.classList.remove('is-dragging');
                board.querySelectorAll('.wiki-inline-board-column').forEach(item =>
                    item.classList.remove('is-drop-target', 'is-saving'));
            });
            card.addEventListener('click', () => openRow(row.id));
            cards.appendChild(card);
        }
        column.appendChild(cards);

        const newTask = document.createElement('button');
        newTask.type = 'button';
        newTask.className = 'wiki-inline-board-new-task';
        newTask.innerHTML = '<span aria-hidden="true">+</span> New task';
        newTask.addEventListener('click', () => {
            newTask.hidden = true;
            const composer = document.createElement('div');
            composer.className = 'wiki-inline-board-composer';
            const input = document.createElement('input');
            input.type = 'text';
            input.placeholder = 'Task name';
            input.setAttribute('aria-label', `New task in ${option.label || 'No status'}`);
            const actions = document.createElement('div');
            const save = document.createElement('button');
            save.type = 'button';
            save.textContent = 'Add task';
            const cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.textContent = 'Cancel';
            const closeComposer = () => {
                composer.remove();
                newTask.hidden = false;
            };
            const submit = async () => {
                const title = input.value.trim();
                if (!title) {
                    input.focus();
                    return;
                }
                input.disabled = save.disabled = cancel.disabled = true;
                try {
                    await addTask(option.id, title);
                } catch {
                    input.disabled = save.disabled = cancel.disabled = false;
                    input.focus();
                }
            };
            input.addEventListener('keydown', event => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    submit();
                } else if (event.key === 'Escape') {
                    event.preventDefault();
                    closeComposer();
                }
            });
            save.addEventListener('click', submit);
            cancel.addEventListener('click', closeComposer);
            actions.append(save, cancel);
            composer.append(input, actions);
            column.insertBefore(composer, newTask);
            input.focus();
        });
        column.appendChild(newTask);
        board.appendChild(column);
    }
    return board;
}

function createInlineCellEditor(state, databaseId, rowId, property, value, onSaved) {
    const commit = async nextValue => {
        try {
            const updated = await state.dotNetRef.invokeMethodAsync(
                'SaveInlineDatabaseCell', databaseId, rowId, property.id, nextValue);
            onSaved(updated);
        } catch { /* the Blazor circuit or mutation may have failed */ }
    };

    // Relation/Person/Files are JSON-array-shaped (see WikiPropertyValues.SetRelation/
    // SetPerson/SetFiles) - this editor only ever produces a single scalar string, which
    // silently corrupted them (SaveInlineDatabaseCell's default branch wrote that string over
    // the array; every reader - dependent rollups, reciprocal relation sync, the row panel -
    // then read the property back as empty, no error shown). Building the real array-shaped
    // editors (a relation search-and-link picker, a person picker, a file upload flow) inline
    // is real, separate work - until then this treats them as read-only, matching
    // property.isReadOnly below, rather than offering a control that quietly loses data.
    if (property.isReadOnly || property.type === 'relation' || property.type === 'person' || property.type === 'files') {
        const readOnly = document.createElement('span');
        readOnly.className = 'wiki-inline-cell-readonly';
        readOnly.textContent = value;
        if (!property.isReadOnly) {
            readOnly.title = 'Open the row to edit this property.';
        }
        return readOnly;
    }

    if (property.type === 'checkbox') {
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = value === 'true';
        checkbox.addEventListener('change', () => commit(String(checkbox.checked)));
        return checkbox;
    }

    if (property.type === 'select' || property.type === 'multiSelect') {
        const select = document.createElement('select');
        select.className = 'wiki-inline-cell-control';
        select.multiple = property.type === 'multiSelect';
        if (!select.multiple) {
            const empty = document.createElement('option');
            empty.value = '';
            empty.textContent = '—';
            select.appendChild(empty);
        }
        const selected = new Set(value.split(',').filter(Boolean));
        for (const option of property.options || []) {
            const element = document.createElement('option');
            element.value = option.id;
            element.textContent = option.label;
            element.selected = selected.has(option.id);
            select.appendChild(element);
        }
        select.addEventListener('change', () => {
            const next = select.multiple
                ? [...select.selectedOptions].map(option => option.value).join(',')
                : select.value;
            commit(next);
        });
        return select;
    }

    const input = document.createElement('input');
    input.className = 'wiki-inline-cell-control';
    input.type = property.type === 'number' ? 'number' : property.type === 'date' ? 'date' : 'text';
    input.value = value;
    input.placeholder = property.type === 'title' ? 'Untitled' : '';
    input.addEventListener('change', () => commit(input.value));
    return input;
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
        reportCursor(state, content);
    });
    content.addEventListener('keyup', () => reportCursor(state, content));
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
        case 'table': return 'Header 1 | Header 2\nCell 1 | Cell 2';
        case 'equation': return 'Type an equation';
        case 'button': return 'Button label';
        case 'synced_block': return 'Synced content';
        case 'columns': return 'Column one ||| Column two';
        default: return "Type '/' for commands";
    }
}

// ---- Keyboard model ------------------------------------------------------

function onContentKeyDown(state, content, event) {
    const blockEl = content.closest('.wiki-block');

    if (handleSuggestionMenuKey(state, event)) {
        return;
    }

    if (event.key === 'Escape') {
        closeFloatingMenus(state);
        return;
    }

    if ((event.ctrlKey || event.metaKey) && !event.shiftKey && event.key.toLowerCase() === 'z') {
        event.preventDefault();
        undo(state);
        return;
    }
    if ((event.ctrlKey || event.metaKey) && (event.key.toLowerCase() === 'y'
        || (event.shiftKey && event.key.toLowerCase() === 'z'))) {
        event.preventDefault();
        redo(state);
        return;
    }

    if (event.altKey && event.shiftKey && event.key.toLowerCase() === 'd') {
        event.preventDefault();
        duplicateBlock(state, blockEl);
        return;
    }
    if (event.altKey && event.shiftKey && event.key === 'Backspace') {
        event.preventDefault();
        deleteBlockAction(state, blockEl);
        return;
    }
    if (event.altKey && !event.shiftKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
        event.preventDefault();
        moveBlock(state, blockEl, event.key === 'ArrowUp' ? -1 : 1);
        focusBlock(blockEl);
        return;
    }

    if (event.key === 'Enter' && !event.shiftKey) {
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

    if (event.key === 'ArrowUp' && !event.shiftKey && isCaretAtStart(content)) {
        const previous = blockEl.previousElementSibling;
        if (previous) {
            event.preventDefault();
            focusBlockAtEnd(previous);
        }
        return;
    }
    if (event.key === 'ArrowDown' && !event.shiftKey && isCaretAtEnd(content)) {
        const next = blockEl.nextElementSibling;
        if (next) {
            event.preventDefault();
            focusBlockAtStart(next);
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
        const safeUrl = safeRichTextHref(url);
        if (safeUrl) { toggleInlineTag('a', { href: safeUrl }); scheduleNotify(state); }
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
    newBlock.indentLevel = blockIndent(blockEl);
    const newEl = createBlockElement(newBlock, state);
    const branch = getBlockBranch(blockEl);
    branch[branch.length - 1].after(newEl);
    const newContent = newEl.querySelector('.wiki-block-content');
    if (newContent) newContent.innerHTML = afterHtml;

    focusBlock(newEl);
    refreshBlockPresentation(state.container);
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
    refreshBlockPresentation(state.container);
    notifyChanged(state);
}

function indentBlock(state, blockEl) {
    const previous = blockEl.previousElementSibling;
    if (!previous) return;
    const current = blockIndent(blockEl);
    if (blockIndent(previous) < current) return;
    changeBranchIndent(blockEl, 1);
    notifyChanged(state);
}

function outdentBlock(state, blockEl) {
    const current = blockIndent(blockEl);
    if (current === 0) return;
    changeBranchIndent(blockEl, -1);
    notifyChanged(state);
}

function blockIndent(blockEl) {
    const indent = Number(blockEl?.dataset.indent || '0');
    return Number.isFinite(indent) ? Math.max(0, indent) : 0;
}

// Sentinel persists hierarchy as a flat, ordered block list with IndentLevel. A branch is a
// block plus every contiguous block that is more deeply indented. Structural editor actions
// must move the entire branch or they silently re-parent descendants on the next round trip.
function getBlockBranch(blockEl) {
    const branch = [blockEl];
    const rootIndent = blockIndent(blockEl);
    let candidate = blockEl.nextElementSibling;
    while (candidate && candidate.classList.contains('wiki-block') && blockIndent(candidate) > rootIndent) {
        branch.push(candidate);
        candidate = candidate.nextElementSibling;
    }
    return branch;
}

function changeBranchIndent(blockEl, delta) {
    for (const branchBlock of getBlockBranch(blockEl)) {
        branchBlock.dataset.indent = String(Math.max(0, blockIndent(branchBlock) + delta));
        applyIndentStyle(branchBlock);
    }
}

function previousPeerRoot(blockEl) {
    const indent = blockIndent(blockEl);
    let candidate = blockEl.previousElementSibling;
    while (candidate) {
        const candidateIndent = blockIndent(candidate);
        if (candidateIndent === indent) return candidate;
        if (candidateIndent < indent) return null;
        candidate = candidate.previousElementSibling;
    }
    return null;
}

function nextPeerRoot(blockEl) {
    const branch = getBlockBranch(blockEl);
    const candidate = branch[branch.length - 1].nextElementSibling;
    return candidate && blockIndent(candidate) === blockIndent(blockEl) ? candidate : null;
}

function canIndentBlock(blockEl) {
    const previous = blockEl.previousElementSibling;
    return Boolean(previous) && blockIndent(previous) >= blockIndent(blockEl);
}

// ---- Slash command menu (same trigger -> async search -> floating dropdown
// -> mousedown-commit template proven by markdownEditor.js's wiki-link autocomplete) -------

function checkSlashTrigger(state, content) {
    const text = content.textContent;
    const match = text.match(/^\/(\w*)$/);
    closeSlashMenu(state);
    if (!match) return;

    const query = match[1].toLowerCase();
    const matches = BLOCK_TYPES.filter(item => `${item.label} ${item.type} ${item.keywords || ''}`
        .toLowerCase()
        .includes(query));
    if (matches.length === 0) return;

    openSuggestionMenu(state, {
        kind: 'slash',
        anchor: content,
        ariaLabel: 'Insert a block',
        items: matches,
        group: item => item.group,
        icon: item => item.icon,
        label: item => item.label,
        description: item => item.description,
        commit: item => convertBlockType(state, content.closest('.wiki-block'), item.type)
    });
}

function convertBlockType(state, blockEl, newType) {
    const block = serializeBlock(blockEl);
    block.type = newType;
    block.richText = [];
    block.props = {};
    const newEl = createBlockElement(block, state);
    blockEl.replaceWith(newEl);
    refreshBlockPresentation(state.container);
    const focusable = newEl.querySelector('.wiki-block-content, input');
    if (focusable) focusable.focus();
    notifyChanged(state);
}

function closeSlashMenu(state) {
    closeSuggestionMenu(state, 'slash');
}

// ---- Wiki-link ([[Page]]) autocomplete, same trigger pattern -------------

function checkWikiLinkTrigger(state, content) {
    const range = getCaretRange(content);
    const requestId = ++state.wikiLinkRequestId;
    closeWikiLinkMenu(state);
    if (!range) return;

    const textBeforeCaret = textBefore(content, range);
    const match = textBeforeCaret.match(/\[\[([^[\]]*)$/);
    if (!match) return;

    const query = match[1];
    // SearchWikiLinkSuggestions returns { id, title } pairs (not just titles) so the chosen
    // page's id is already in hand here - no second round-trip needed to resolve an href.
    state.dotNetRef.invokeMethodAsync('SearchWikiLinkSuggestions', query).then(suggestions => {
        if (requestId !== state.wikiLinkRequestId) return;
        closeWikiLinkMenu(state);
        if (!suggestions || suggestions.length === 0) return;

        openSuggestionMenu(state, {
            kind: 'wikiLink',
            anchor: content,
            ariaLabel: 'Link to a Sentinel page',
            items: suggestions,
            icon: () => '📄',
            label: suggestion => suggestion.title,
            description: () => 'Sentinel page',
            commit: suggestion => insertWikiLink(state, content, query, suggestion.id, suggestion.title)
        });
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
    closeSuggestionMenu(state, 'wikiLink');
}

// ---- Structured @person and @date mentions --------------------------------

function checkMentionTrigger(state, content) {
    const range = getCaretRange(content);
    const requestId = ++state.mentionRequestId;
    closeMentionMenu(state);
    if (!range) return;

    const textBeforeCaret = textBefore(content, range);
    const match = textBeforeCaret.match(/(?:^|\s)@([\w.-]*)$/);
    if (!match) return;

    const query = match[1];
    state.dotNetRef.invokeMethodAsync('SearchMentionSuggestions', query).then(suggestions => {
        if (requestId !== state.mentionRequestId) return;
        closeMentionMenu(state);
        if (!suggestions || suggestions.length === 0) return;

        openSuggestionMenu(state, {
            kind: 'mention',
            anchor: content,
            ariaLabel: 'Mention a person, date, or database row',
            items: suggestions,
            group: suggestion => mentionGroup(suggestion.kind),
            icon: suggestion => mentionIcon(suggestion.kind),
            label: suggestion => suggestion.label,
            description: suggestion => suggestion.description,
            commit: suggestion => insertMention(state, content, query, suggestion)
        });
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
    closeSuggestionMenu(state, 'mention');
}

function mentionGroup(kind) {
    if (kind === 'user') return 'People';
    if (kind === 'date') return 'Dates';
    if (kind === 'row') return 'Database rows';
    return 'Suggestions';
}

function mentionIcon(kind) {
    if (kind === 'user') return '@';
    if (kind === 'date') return '◷';
    if (kind === 'row') return '▦';
    return '•';
}

function suggestionMenuProperty(kind) {
    if (kind === 'wikiLink') return 'wikiLinkMenu';
    if (kind === 'mention') return 'mentionMenu';
    return 'slashMenu';
}

function openSuggestionMenu(state, configuration) {
    closeSuggestionMenu(state, 'slash');
    closeSuggestionMenu(state, 'wikiLink');
    closeSuggestionMenu(state, 'mention');

    const menu = document.createElement('div');
    menu.id = `wiki-editor-menu-${++suggestionMenuSequence}`;
    menu.className = 'wiki-slash-menu wiki-editor-suggestion-menu shadow-sm';
    menu.setAttribute('role', 'listbox');
    menu.setAttribute('aria-label', configuration.ariaLabel);
    positionMenu(menu, configuration.anchor);

    const hint = document.createElement('div');
    hint.className = 'wiki-editor-menu-hint';
    hint.textContent = `${configuration.ariaLabel} · ↑↓ navigate · Enter select`;
    menu.appendChild(hint);

    const options = [];
    let previousGroup = null;
    configuration.items.forEach((item, index) => {
        const group = configuration.group ? configuration.group(item) : null;
        if (group && group !== previousGroup) {
            const heading = document.createElement('div');
            heading.className = 'wiki-editor-menu-group';
            heading.textContent = group;
            heading.setAttribute('role', 'presentation');
            menu.appendChild(heading);
            previousGroup = group;
        }

        const option = document.createElement('button');
        option.type = 'button';
        option.id = `${menu.id}-option-${index}`;
        option.className = 'wiki-editor-menu-item';
        option.setAttribute('role', 'option');
        option.setAttribute('aria-selected', 'false');

        const icon = document.createElement('span');
        icon.className = 'wiki-editor-menu-icon';
        icon.textContent = configuration.icon ? configuration.icon(item) : '•';
        icon.setAttribute('aria-hidden', 'true');

        const copy = document.createElement('span');
        copy.className = 'wiki-editor-menu-copy';
        const label = document.createElement('span');
        label.className = 'wiki-editor-menu-label';
        label.textContent = configuration.label(item);
        const description = document.createElement('span');
        description.className = 'wiki-editor-menu-description';
        description.textContent = configuration.description ? configuration.description(item) : '';
        copy.append(label, description);
        option.append(icon, copy);

        option.addEventListener('mouseenter', () => setActiveSuggestion(state, index));
        option.addEventListener('mousedown', event => {
            event.preventDefault();
            commitSuggestion(state, index);
        });
        menu.appendChild(option);
        options.push(option);
    });

    document.body.appendChild(menu);
    state[suggestionMenuProperty(configuration.kind)] = menu;
    state.activeSuggestionMenu = {
        kind: configuration.kind,
        menu,
        anchor: configuration.anchor,
        items: configuration.items,
        options,
        activeIndex: 0,
        commit: configuration.commit
    };
    configuration.anchor.setAttribute('aria-autocomplete', 'list');
    configuration.anchor.setAttribute('aria-controls', menu.id);
    configuration.anchor.setAttribute('aria-expanded', 'true');
    setActiveSuggestion(state, 0);
}

function setActiveSuggestion(state, index) {
    const active = state.activeSuggestionMenu;
    if (!active || active.options.length === 0) return;
    const nextIndex = (index + active.options.length) % active.options.length;
    active.activeIndex = nextIndex;
    active.options.forEach((option, optionIndex) => {
        const selected = optionIndex === nextIndex;
        option.classList.toggle('is-active', selected);
        option.setAttribute('aria-selected', selected ? 'true' : 'false');
    });
    const selected = active.options[nextIndex];
    active.anchor.setAttribute('aria-activedescendant', selected.id);
    selected.scrollIntoView({ block: 'nearest' });
}

function commitSuggestion(state, index) {
    const active = state.activeSuggestionMenu;
    if (!active || index < 0 || index >= active.items.length) return;
    const item = active.items[index];
    const commit = active.commit;
    closeSuggestionMenu(state, active.kind);
    commit(item);
}

function handleSuggestionMenuKey(state, event) {
    const active = state.activeSuggestionMenu;
    if (!active) return false;
    if (event.key === 'ArrowDown') {
        event.preventDefault();
        setActiveSuggestion(state, active.activeIndex + 1);
        return true;
    }
    if (event.key === 'ArrowUp') {
        event.preventDefault();
        setActiveSuggestion(state, active.activeIndex - 1);
        return true;
    }
    if (event.key === 'Home') {
        event.preventDefault();
        setActiveSuggestion(state, 0);
        return true;
    }
    if (event.key === 'End') {
        event.preventDefault();
        setActiveSuggestion(state, active.options.length - 1);
        return true;
    }
    if (event.key === 'Enter' || event.key === 'Tab') {
        event.preventDefault();
        commitSuggestion(state, active.activeIndex);
        return true;
    }
    if (event.key === 'Escape') {
        event.preventDefault();
        closeSuggestionMenu(state, active.kind);
        return true;
    }
    return false;
}

function closeSuggestionMenu(state, kind) {
    const property = suggestionMenuProperty(kind);
    const menu = state[property];
    if (menu) menu.remove();
    state[property] = null;

    const active = state.activeSuggestionMenu;
    if (!active || active.kind !== kind) return;
    active.anchor.removeAttribute('aria-autocomplete');
    active.anchor.removeAttribute('aria-controls');
    active.anchor.removeAttribute('aria-expanded');
    active.anchor.removeAttribute('aria-activedescendant');
    state.activeSuggestionMenu = null;
}

function repositionSuggestionMenu(state) {
    const active = state.activeSuggestionMenu;
    if (active) positionMenu(active.menu, active.anchor);
}

function closeFloatingMenus(state, event) {
    if (event && (state.slashMenu?.contains(event.target) || state.wikiLinkMenu?.contains(event.target)
        || state.mentionMenu?.contains(event.target) || state.inlineToolbar?.contains(event.target)
        || state.blockMenu?.contains(event.target))) return;
    closeSlashMenu(state);
    closeWikiLinkMenu(state);
    closeMentionMenu(state);
    closeInlineToolbar(state);
    closeBlockMenu(state);
}

function positionMenu(menu, anchorEl) {
    const rect = anchorEl.getBoundingClientRect();
    const menuWidth = Math.min(352, Math.max(200, window.innerWidth - 16));
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - menuWidth - 8));
    menu.style.position = 'absolute';
    menu.style.left = `${window.scrollX + left}px`;
    menu.style.top = `${window.scrollY + rect.bottom}px`;
    menu.style.zIndex = '2000';
}

// ---- Drag-to-reorder (Pointer Events, matching automation-editor.js) -----

function onHandlePointerDown(state, event) {
    if (event.button !== 0) return;
    const wikiLink = wikiLinkAnchorFromEvent(event);
    if (wikiLink) {
        event.preventDefault();
        event.stopPropagation();
        state.lastWikiLinkPointerNavigation = {
            href: wikiLink.getAttribute('href'),
            at: performance.now()
        };
        navigateToWikiLink(state, wikiLink);
        return;
    }

    const handle = event.target.closest('.wiki-block-handle');
    if (!handle) return;
    const blockEl = handle.closest('.wiki-block');
    if (!blockEl) return;

    const branch = getBlockBranch(blockEl);
    state.drag = { blockEl, branch, pointerId: event.pointerId };
    branch.forEach(element => element.classList.add('is-dragging'));
    handle.setPointerCapture(event.pointerId);
    event.preventDefault();
}

function wikiLinkAnchorFromEvent(event) {
    const pathAnchor = event.composedPath?.()
        .find(element => element.matches?.('a[href^="wikilink:"]'));
    if (pathAnchor) return pathAnchor;

    const targetAnchor = event.target.closest?.('a[href^="wikilink:"]');
    if (targetAnchor) return targetAnchor;

    return Number.isFinite(event.clientX) && Number.isFinite(event.clientY)
        ? document.elementFromPoint(event.clientX, event.clientY)?.closest?.('a[href^="wikilink:"]')
        : null;
}

function navigateToWikiLink(state, anchor) {
    const href = anchor.getAttribute('href') || '';
    const pageId = href.substring('wikilink:'.length);
    if (pageId) state.dotNetRef.invokeMethodAsync('NavigateToWikiPageId', pageId);
}

function onHandlePointerMove(state, event) {
    if (!state.drag || state.drag.pointerId !== event.pointerId) return;
    const dragged = new Set(state.drag.branch);
    const siblings = [...state.container.querySelectorAll('.wiki-block')].filter(el => !dragged.has(el));
    const target = siblings.find(el => {
        const rect = el.getBoundingClientRect();
        return event.clientY >= rect.top && event.clientY <= rect.bottom;
    });
    if (!target) return;

    const peer = peerRootAtIndent(target, blockIndent(state.drag.blockEl));
    if (!peer) return;
    const peerBranch = getBlockBranch(peer).filter(element => !dragged.has(element));
    if (peerBranch.length === 0) return;
    const firstRect = peerBranch[0].getBoundingClientRect();
    const lastRect = peerBranch[peerBranch.length - 1].getBoundingClientRect();
    const insertAfter = event.clientY > firstRect.top + ((lastRect.bottom - firstRect.top) / 2);
    if (insertAfter) peerBranch[peerBranch.length - 1].after(...state.drag.branch);
    else peer.before(...state.drag.branch);
}

function onHandlePointerUp(state, event) {
    if (!state.drag || state.drag.pointerId !== event.pointerId) return;
    state.drag.branch.forEach(element => element.classList.remove('is-dragging'));
    state.drag = null;
    refreshBlockPresentation(state.container);
    notifyChanged(state);
}

function peerRootAtIndent(blockEl, indent) {
    let candidate = blockEl;
    while (candidate) {
        const candidateIndent = blockIndent(candidate);
        if (candidateIndent === indent) return candidate;
        if (candidateIndent < indent) return null;
        candidate = candidate.previousElementSibling;
    }
    return null;
}

// ---- Contextual block actions (⋮ menu, mirrored by keyboard shortcuts) ---

function openBlockMenu(state, blockEl, anchorEl) {
    closeFloatingMenus(state);

    const menu = document.createElement('div');
    menu.className = 'wiki-slash-menu wiki-block-menu list-group shadow-sm';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', 'Block actions');
    positionMenu(menu, anchorEl);

    const items = [
        { label: 'Duplicate', icon: '⧉', action: () => duplicateBlock(state, blockEl) },
        { label: 'Indent', icon: '→', action: () => indentBlock(state, blockEl), disabled: !canIndentBlock(blockEl) },
        { label: 'Outdent', icon: '←', action: () => outdentBlock(state, blockEl), disabled: blockIndent(blockEl) === 0 },
        { label: 'Move up', icon: '↑', action: () => moveBlock(state, blockEl, -1), disabled: !previousPeerRoot(blockEl) },
        { label: 'Move down', icon: '↓', action: () => moveBlock(state, blockEl, 1), disabled: !nextPeerRoot(blockEl) },
        { label: 'Delete', icon: '🗑', action: () => deleteBlockAction(state, blockEl) }
    ];

    for (const item of items) {
        const option = document.createElement('button');
        option.type = 'button';
        option.className = 'list-group-item list-group-item-action py-1 px-2 small d-flex align-items-center gap-2';
        option.setAttribute('role', 'menuitem');
        option.disabled = !!item.disabled;
        option.innerHTML = `<span class="wiki-slash-icon">${item.icon}</span><span>${item.label}</span>`;
        option.addEventListener('mousedown', event => {
            event.preventDefault();
            closeBlockMenu(state);
            item.action();
        });
        menu.appendChild(option);
    }

    document.body.appendChild(menu);
    state.blockMenu = menu;
}

function closeBlockMenu(state) {
    if (state.blockMenu) { state.blockMenu.remove(); state.blockMenu = null; }
}

function duplicateBlock(state, blockEl) {
    const branch = getBlockBranch(blockEl);
    const duplicate = branch.map(element => {
        const block = serializeBlock(element);
        block.id = crypto.randomUUID();
        return createBlockElement(block, state);
    });
    branch[branch.length - 1].after(...duplicate);
    refreshBlockPresentation(state.container);
    focusBlock(duplicate[0]);
    notifyChanged(state);
}

function moveBlock(state, blockEl, direction) {
    const branch = getBlockBranch(blockEl);
    if (direction < 0) {
        const previous = previousPeerRoot(blockEl);
        if (!previous) return;
        previous.before(...branch);
    } else {
        const next = nextPeerRoot(blockEl);
        if (!next) return;
        const nextBranch = getBlockBranch(next);
        nextBranch[nextBranch.length - 1].after(...branch);
    }
    refreshBlockPresentation(state.container);
    notifyChanged(state);
}

function deleteBlockAction(state, blockEl) {
    const branch = getBlockBranch(blockEl);
    const next = branch[branch.length - 1].nextElementSibling || blockEl.previousElementSibling;
    if (state.container.children.length <= branch.length) {
        // Mirrors setBlocks' own invariant: the editor always shows at least one block.
        const empty = createBlockElement(emptyBlock('paragraph'), state);
        blockEl.before(empty);
        branch.forEach(element => element.remove());
        focusBlock(empty);
    } else {
        branch.forEach(element => element.remove());
        if (next) focusBlock(next);
    }
    refreshBlockPresentation(state.container);
    notifyChanged(state);
}

// ---- Serialization ---------------------------------------------------------

function scheduleNotify(state) {
    if (state.notifyTimer) clearTimeout(state.notifyTimer);
    state.notifyTimer = setTimeout(() => notifyChanged(state), 250);
}

// One undo entry per debounced edit burst (typing) or per structural op (add/delete/move/
// indent/duplicate - all of which call this directly, bypassing the 250ms debounce). Bounded
// so a long editing session can't grow the stack unboundedly.
const MAX_UNDO_ENTRIES = 100;

function normalizeHistoryKey(value) {
    const normalized = String(value || '').trim().toLowerCase();
    return normalized && /^[a-z0-9-]{1,80}$/.test(normalized) ? normalized : null;
}

function historyStorageKey(state) {
    return state.historyKey ? `${HISTORY_STORAGE_PREFIX}${state.historyKey}` : null;
}

function draftStorageKey(state) {
    return state.historyKey ? `${DRAFT_STORAGE_PREFIX}${state.historyKey}` : null;
}

function readPersistedDraft(state) {
    const key = draftStorageKey(state);
    if (!key) return null;
    try {
        const value = JSON.parse(localStorage.getItem(key) || 'null');
        if (!value || value.version !== 1 || typeof value.baseSnapshot !== 'string'
            || typeof value.snapshot !== 'string' || typeof value.savedAt !== 'number') {
            return null;
        }
        if (Date.now() - value.savedAt > MAX_DRAFT_AGE_MS) {
            localStorage.removeItem(key);
            return null;
        }
        return value;
    } catch {
        return null;
    }
}

function persistDraft(state, snapshot) {
    const key = draftStorageKey(state);
    if (!key || state.baseSnapshot === undefined) return;
    if (snapshot === state.baseSnapshot) {
        clearPersistedDraft(state);
        return;
    }
    const payload = JSON.stringify({
        version: 1,
        baseSnapshot: state.baseSnapshot,
        snapshot,
        savedAt: Date.now()
    });
    if (payload.length > MAX_PERSISTED_DRAFT_CHARS) return;
    try { localStorage.setItem(key, payload); }
    catch { /* private browsing or a full storage quota must not interrupt editing */ }
}

function clearPersistedDraft(state) {
    const key = draftStorageKey(state);
    if (!key) return;
    try { localStorage.removeItem(key); }
    catch { /* storage may be unavailable */ }
}

function readPersistedHistory(state) {
    const key = historyStorageKey(state);
    if (!key) return null;
    try {
        const value = JSON.parse(sessionStorage.getItem(key) || 'null');
        if (!value || value.version !== 1 || typeof value.lastSnapshot !== 'string'
            || !Array.isArray(value.undoStack) || !Array.isArray(value.redoStack)) {
            return null;
        }
        return {
            lastSnapshot: value.lastSnapshot,
            undoStack: value.undoStack.filter(item => typeof item === 'string').slice(-MAX_UNDO_ENTRIES),
            redoStack: value.redoStack.filter(item => typeof item === 'string').slice(-MAX_UNDO_ENTRIES)
        };
    } catch {
        return null;
    }
}

function persistHistory(state) {
    const key = historyStorageKey(state);
    if (!key || state.lastSnapshot === undefined) return;

    const undoStack = state.undoStack.slice(-MAX_UNDO_ENTRIES);
    const redoStack = state.redoStack.slice(-MAX_UNDO_ENTRIES);
    let payload = JSON.stringify({ version: 1, lastSnapshot: state.lastSnapshot, undoStack, redoStack });
    while (payload.length > MAX_PERSISTED_HISTORY_CHARS && (undoStack.length > 1 || redoStack.length > 0)) {
        if (undoStack.length > 1) undoStack.shift();
        else redoStack.shift();
        payload = JSON.stringify({ version: 1, lastSnapshot: state.lastSnapshot, undoStack, redoStack });
    }

    try { sessionStorage.setItem(key, payload); }
    catch { /* private browsing or a full storage quota must not interrupt editing */ }
}

function notifyChanged(state) {
    if (state.notifyTimer) { clearTimeout(state.notifyTimer); state.notifyTimer = null; }
    const current = getBlocksJson(state.container);
    if (state.lastSnapshot !== undefined && state.lastSnapshot !== current) {
        state.undoStack.push(state.lastSnapshot);
        if (state.undoStack.length > MAX_UNDO_ENTRIES) state.undoStack.shift();
        state.redoStack = [];
    }
    state.lastSnapshot = current;
    persistHistory(state);
    persistDraft(state, current);
    try {
        const pending = state.dotNetRef.invokeMethodAsync('OnBlocksChanged', current);
        pending?.catch?.(() => { /* localStorage remains the durable offline outbox */ });
    }
    catch { /* the Blazor circuit may have disconnected */ }
}

function notifyChangedSilently(state) {
    if (state.notifyTimer) { clearTimeout(state.notifyTimer); state.notifyTimer = null; }
    try {
        const pending = state.dotNetRef.invokeMethodAsync('OnBlocksChanged', getBlocksJson(state.container));
        pending?.catch?.(() => { /* localStorage remains the durable offline outbox */ });
    }
    catch { /* the Blazor circuit may have disconnected */ }
}

function undo(state) {
    if (state.undoStack.length === 0) return;
    const focusedBlockId = document.activeElement?.closest?.('.wiki-block')?.dataset.blockId;
    const current = getBlocksJson(state.container);
    const previous = state.undoStack.pop();
    state.redoStack.push(current);
    renderBlocks(state.container, state, previous);
    state.lastSnapshot = previous;
    persistHistory(state);
    persistDraft(state, previous);
    notifyChangedSilently(state);
    restoreFocusAfterHistory(state, focusedBlockId);
}

function redo(state) {
    if (state.redoStack.length === 0) return;
    const focusedBlockId = document.activeElement?.closest?.('.wiki-block')?.dataset.blockId;
    const current = getBlocksJson(state.container);
    const next = state.redoStack.pop();
    state.undoStack.push(current);
    renderBlocks(state.container, state, next);
    state.lastSnapshot = next;
    persistHistory(state);
    persistDraft(state, next);
    notifyChangedSilently(state);
    restoreFocusAfterHistory(state, focusedBlockId);
}

function restoreFocusAfterHistory(state, blockId) {
    const target = blockId
        ? state.container.querySelector(`:scope > .wiki-block[data-block-id="${cssEscape(blockId)}"]`)
        : null;
    focusBlock(target || state.container.querySelector(':scope > .wiki-block'));
}

function serializeBlock(blockEl) {
    const type = blockEl.dataset.blockType;
    let props = {};
    try { props = JSON.parse(blockEl.dataset.propsJson || '{}'); } catch { props = {}; }
    if (type === 'to_do') props.checked = blockEl.dataset.checked === 'true' ? 'true' : 'false';
    if (type === 'numbered_list_item') {
        const marker = blockEl.querySelector('.wiki-list-marker')?.textContent || '1.';
        props.number = marker.replace(/\D/g, '') || '1';
    }
    if (type === 'toggle') props.open = blockEl.dataset.open === 'true' ? 'true' : 'false';
    if (type === 'table') props.tableJson = JSON.stringify(serializeTableRichText(blockEl));
    if (type === 'columns') {
        props.columnRichTextJson = JSON.stringify(
            [...blockEl.querySelectorAll(':scope > .wiki-block-body .wiki-column-content')]
                .map(column => richTextFromNode(column)));
    }
    if (type === 'image' || type === 'embed') {
        props.url = blockEl.dataset.url || '';
        if (blockEl.dataset.fileName) props.fileName = blockEl.dataset.fileName;
        if (blockEl.dataset.notionBlockId) props.notionBlockId = blockEl.dataset.notionBlockId;
        if (blockEl.dataset.mediaKind) props.mediaKind = blockEl.dataset.mediaKind;
    }
    if (type === 'linked_database' || type === 'inline_database') {
        props.databaseId = blockEl.dataset.databaseId || '';
        props.databaseTitle = blockEl.dataset.databaseTitle || '';
        props.databaseIcon = blockEl.dataset.databaseIcon || '';
    }

    const contentEl = blockEl.querySelector('.wiki-block-content');
    const richText = type === 'table'
        ? [{ text: serializeTable(blockEl) }]
        : type === 'columns'
            ? [{
                text: [...blockEl.querySelectorAll(':scope > .wiki-block-body .wiki-column-content')]
                    .map(column => column.textContent.trim())
                    .join(' ||| ')
            }]
        : contentEl ? richTextFromNode(contentEl) : [];
    return {
        id: blockEl.dataset.blockId,
        type,
        indentLevel: Number(blockEl.dataset.indent || '0'),
        richText,
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
        if (tag === 'span') {
            const textColor = normalizeRichTextColor(child.dataset.wikiTextColor);
            const backgroundColor = normalizeRichTextColor(child.dataset.wikiBackgroundColor);
            if (textColor) nextMarks.textColor = textColor;
            if (backgroundColor) nextMarks.backgroundColor = backgroundColor;
        }
        walkRichText(child, nextMarks, spans);
    }
}

function marksEqual(a, b) {
    return !!a.bold === !!b.bold && !!a.italic === !!b.italic
        && !!a.strikethrough === !!b.strikethrough && !!a.code === !!b.code
        && (a.link || '') === (b.link || '')
        && (a.textColor || '') === (b.textColor || '')
        && (a.backgroundColor || '') === (b.backgroundColor || '');
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
        const textColor = normalizeRichTextColor(span.textColor);
        const backgroundColor = normalizeRichTextColor(span.backgroundColor);
        if (textColor) html = `<span class="wiki-rich-text-color-${textColor}" data-wiki-text-color="${textColor}">${html}</span>`;
        if (backgroundColor) html = `<span class="wiki-rich-text-bg-${backgroundColor}" data-wiki-background-color="${backgroundColor}">${html}</span>`;
        if (span.link) {
            const safeLink = safeRichTextHref(span.link);
            if (safeLink) {
                const mentionClass = /^(user|date|row)mention:/i.test(safeLink) ? ' class="wiki-mention"' : '';
                html = `<a${mentionClass} href="${escapeHtml(safeLink)}">${html}</a>`;
            }
        }
        return html;
    }).join('');
}

function safeRichTextHref(value) {
    const link = String(value || '').trim();
    if (!link) return null;
    if (/^(wikilink|usermention|datemention|rowmention):/i.test(link)) return link;

    try {
        const parsed = new URL(link, window.location.origin);
        return ['http:', 'https:', 'mailto:', 'tel:'].includes(parsed.protocol)
            ? link
            : null;
    } catch {
        return null;
    }
}

function safeMediaHref(value) {
    const link = safeRichTextHref(value);
    if (!link) return null;
    try {
        const parsed = new URL(link, window.location.origin);
        return ['http:', 'https:'].includes(parsed.protocol) ? link : null;
    } catch {
        return null;
    }
}

function normalizeRichTextColor(value) {
    const normalized = String(value || '').trim().toLowerCase();
    return RICH_TEXT_COLORS.includes(normalized) ? normalized : null;
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
        const safeUrl = safeRichTextHref(url);
        if (safeUrl) {
            toggleInlineTag('a', { href: safeUrl });
            scheduleNotify(state);
        }
    });
    toolbar.appendChild(linkButton);

    const commentButton = document.createElement('button');
    commentButton.type = 'button';
    commentButton.textContent = '💬';
    commentButton.title = 'Comment on selection';
    commentButton.setAttribute('aria-label', 'Comment on selection');
    commentButton.addEventListener('mousedown', event => event.preventDefault());
    commentButton.addEventListener('click', () => {
        const selectedText = range.toString().trim();
        const blockEl = content.closest('.wiki-block');
        if (!selectedText || !blockEl) return;
        const prefix = range.cloneRange();
        prefix.selectNodeContents(content);
        prefix.setEnd(range.startContainer, range.startOffset);
        const startOffset = prefix.toString().length;
        try {
            state.dotNetRef.invokeMethodAsync(
                'OpenSelectionDiscussion',
                blockEl.dataset.blockId,
                selectedText.slice(0, 500),
                startOffset,
                startOffset + range.toString().length);
        } catch { /* the Blazor circuit may have disconnected */ }
        closeInlineToolbar(state);
    });
    toolbar.appendChild(commentButton);
    appendColorMenuButton(toolbar, state, range, 'text');
    appendColorMenuButton(toolbar, state, range, 'background');

    document.body.appendChild(toolbar);
    const rect = range.getBoundingClientRect();
    const toolbarRect = toolbar.getBoundingClientRect();
    toolbar.style.left = `${window.scrollX + rect.left + (rect.width - toolbarRect.width) / 2}px`;
    toolbar.style.top = `${window.scrollY + rect.top - toolbarRect.height - 8}px`;
    state.inlineToolbar = toolbar;
}

function appendColorMenuButton(toolbar, state, selectionRange, kind) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = kind === 'text' ? 'wiki-color-menu-toggle' : 'wiki-background-menu-toggle';
    button.textContent = kind === 'text' ? 'A' : '▧';
    button.title = kind === 'text' ? 'Text color' : 'Background color';
    button.setAttribute('aria-label', button.title);
    button.setAttribute('aria-haspopup', 'menu');
    button.addEventListener('mousedown', event => event.preventDefault());
    button.addEventListener('click', event => {
        event.stopPropagation();
        toolbar.querySelectorAll('.wiki-color-menu').forEach(menu => menu.remove());
        const menu = document.createElement('div');
        menu.className = 'wiki-color-menu';
        menu.setAttribute('role', 'menu');
        menu.setAttribute('aria-label', button.title);

        const choices = [{ value: null, label: 'Default' }, ...RICH_TEXT_COLORS.map(value => ({
            value,
            label: `${value[0].toUpperCase()}${value.slice(1)}`
        }))];
        for (const choice of choices) {
            const option = document.createElement('button');
            option.type = 'button';
            option.setAttribute('role', 'menuitem');
            option.setAttribute('aria-label', `${button.title} ${choice.label.toLowerCase()}`);
            const swatch = document.createElement('span');
            swatch.className = `wiki-color-swatch${choice.value ? ` wiki-rich-text-${kind === 'text' ? 'color' : 'bg'}-${choice.value}` : ''}`;
            swatch.textContent = choice.value ? 'A' : '×';
            const label = document.createElement('span');
            label.textContent = choice.label;
            option.append(swatch, label);
            option.addEventListener('mousedown', mouseEvent => mouseEvent.preventDefault());
            option.addEventListener('click', optionEvent => {
                optionEvent.stopPropagation();
                applyInlineColor(selectionRange, kind, choice.value);
                scheduleNotify(state);
                closeInlineToolbar(state);
            });
            menu.appendChild(option);
        }
        button.appendChild(menu);
    });
    toolbar.appendChild(button);
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

function applyInlineColor(savedRange, kind, color) {
    if (!savedRange || savedRange.collapsed) return;
    const range = savedRange.cloneRange();
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);

    const attribute = kind === 'text' ? 'data-wiki-text-color' : 'data-wiki-background-color';
    const classPrefix = kind === 'text' ? 'wiki-rich-text-color-' : 'wiki-rich-text-bg-';
    const fragment = range.extractContents();
    for (const element of fragment.querySelectorAll(`[${attribute}]`)) {
        const previous = element.getAttribute(attribute);
        element.removeAttribute(attribute);
        if (previous) element.classList.remove(`${classPrefix}${previous}`);
    }

    let insertedNode = fragment;
    if (color) {
        const wrapper = document.createElement('span');
        wrapper.setAttribute(attribute, color);
        wrapper.className = `${classPrefix}${color}`;
        wrapper.appendChild(fragment);
        insertedNode = wrapper;
    }
    range.insertNode(insertedNode);

    const nextRange = document.createRange();
    if (insertedNode.nodeType === Node.DOCUMENT_FRAGMENT_NODE) {
        nextRange.setStart(range.startContainer, range.startOffset);
        nextRange.collapse(true);
    } else {
        nextRange.selectNodeContents(insertedNode);
    }
    selection.removeAllRanges();
    selection.addRange(nextRange);
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

function isCaretAtEnd(content) {
    const range = getCaretRange(content);
    if (!range) return false;
    const postRange = range.cloneRange();
    postRange.selectNodeContents(content);
    postRange.setStart(range.endContainer, range.endOffset);
    return postRange.toString().length === 0;
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

function focusBlockAtEnd(blockEl) {
    const target = blockEl.querySelector('.wiki-block-content');
    if (target) placeCaretAtTextOffset(target, target.textContent.length);
    else focusBlock(blockEl);
}

function focusBlockAtStart(blockEl) {
    const target = blockEl.querySelector('.wiki-block-content');
    if (target) placeCaretAtTextOffset(target, 0);
    else focusBlock(blockEl);
}
