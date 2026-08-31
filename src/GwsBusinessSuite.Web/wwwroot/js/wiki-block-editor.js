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
const SENTINEL_PAGE_DRAG_TYPE = 'application/x-gws-sentinel-page';
let suggestionMenuSequence = 0;
let tabEditorSequence = 0;

// Grouped and labeled to match Notion's own "+"//" menu taxonomy (Basic Blocks / Media Blocks /
// Database Inline / Full Page / Advanced & Inline Blocks) rather than this editor's earlier,
// looser grouping - see the "+" button feature request this mirrors.
const BLOCK_TYPES = [
    { type: 'paragraph', label: 'Text', icon: '¶', group: 'Basic blocks', description: 'Start writing with plain text.', keywords: 'paragraph' },
    { type: '__create_page', label: 'Page', icon: '📄', group: 'Basic blocks', description: 'Create a new nested sub-page and open it.', keywords: 'page subpage new document' },
    { type: 'to_do', label: 'To-do list', icon: '☑', group: 'Basic blocks', description: 'Text with a clickable checkbox next to it.', keywords: 'task checkbox' },
    { type: 'heading_1', label: 'Heading 1', icon: 'H1', group: 'Basic blocks', description: 'Large section heading.', keywords: 'title' },
    { type: 'heading_2', label: 'Heading 2', icon: 'H2', group: 'Basic blocks', description: 'Medium section heading.', keywords: 'subtitle' },
    { type: 'heading_3', label: 'Heading 3', icon: 'H3', group: 'Basic blocks', description: 'Small section heading.', keywords: 'subtitle' },
    { type: 'table', label: 'Table', icon: '▦', group: 'Basic blocks', description: 'A simple, standalone text table layout.', keywords: 'grid rows columns' },
    { type: 'bulleted_list_item', label: 'Bulleted list', icon: '•', group: 'Basic blocks', description: 'Create a simple bulleted list.', keywords: 'unordered' },
    { type: 'numbered_list_item', label: 'Numbered list', icon: '1.', group: 'Basic blocks', description: 'Create an ordered list.', keywords: 'ordered' },
    { type: 'toggle', label: 'Toggle list', icon: '▸', group: 'Basic blocks', description: 'Arrows that expand or collapse nested content.', keywords: 'details collapse' },
    { type: 'quote', label: 'Quote', icon: '❝', group: 'Basic blocks', description: 'Large text offset with a vertical accent line.', keywords: 'blockquote' },
    { type: 'divider', label: 'Divider', icon: '—', group: 'Basic blocks', description: 'A thin horizontal line to separate visual space.', keywords: 'rule separator' },
    { type: '__link_to_page', label: 'Link to page', icon: '🔗', group: 'Basic blocks', description: 'Creates a shortcut link to another existing page.', keywords: 'link page shortcut wikilink' },
    { type: 'callout', label: 'Callout', icon: '💡', group: 'Basic blocks', description: 'Text boxed within a light background banner with a custom icon.', keywords: 'notice aside' },
    { type: 'image', label: 'Image', icon: '🖼', group: 'Media', description: 'Uploads or embeds pictures, GIFs, or stock imagery.', keywords: 'photo picture' },
    { type: 'embed', label: 'Web bookmark', icon: '🔖', group: 'Media', description: 'Creates a neat preview card for web links.', keywords: 'embed url bookmark' },
    { type: 'video', label: 'Video', icon: '🎬', group: 'Media', description: 'Uploads or embeds video streams like YouTube or Vimeo.', keywords: 'movie mp4 youtube vimeo' },
    { type: 'audio', label: 'Audio', icon: '🎧', group: 'Media', description: 'Uploads or links to audio recordings or Spotify playlists.', keywords: 'music sound mp3 podcast spotify' },
    { type: 'pdf', label: 'PDF', icon: '📕', group: 'Media', description: 'Embeds a viewable PDF document.', keywords: 'document viewer' },
    { type: 'file', label: 'File', icon: '📎', group: 'Media', description: 'Uploads and saves data downloads directly into your workspace.', keywords: 'attachment download upload' },
    { type: 'code', label: 'Code', icon: '</>', group: 'Media', description: 'A dedicated block for formatting code snippets across languages.', keywords: 'preformatted' },
    { type: 'inline_database', label: 'Table view', icon: '▦', group: 'Database inline / full page', description: 'Data displayed in a grid of rows and columns.', keywords: 'database data collection table grid' },
    { type: 'linked_database', label: 'Linked database', icon: '▤', group: 'Database inline / full page', description: 'Show an existing database view.', keywords: 'database data view' },
    { type: '__create_database', label: 'New database', icon: '🗄️', group: 'Database inline / full page', description: 'Create a new database nested here and open it.', keywords: 'database table collection new board gallery list calendar timeline' },
    { type: 'table_of_contents', label: 'Table of contents', icon: '☷', group: 'Advanced & inline blocks', description: 'Auto-generates a list of jump links using your page headings.', keywords: 'outline headings' },
    { type: 'equation', label: 'Block equation', icon: '∑', group: 'Advanced & inline blocks', description: 'Centers standard LaTeX scientific formulas.', keywords: 'math formula latex' },
    { type: 'synced_block', label: 'Synced block', icon: '↻', group: 'Advanced & inline blocks', description: 'Edits here update every duplicated copy of this block.', keywords: 'reusable' },
    { type: 'button', label: 'Button', icon: '▣', group: 'Advanced & inline blocks', description: 'Creates automatic action macro scripts when clicked.', keywords: 'action link automation' },
    { type: '__mention_person', label: 'Mention a person', icon: '@', group: 'Advanced & inline blocks', description: 'Inline flag for a coworker.', keywords: 'mention person user' },
    { type: 'columns', label: 'Columns', icon: '▥', group: 'Advanced & inline blocks', description: 'Lay content out side by side.', keywords: 'layout' },
    { type: 'tab', label: 'Tabs', icon: '▤', group: 'Advanced & inline blocks', description: 'Organize content into switchable tabs.', keywords: 'tabbed container panes' },
    { type: 'breadcrumb', label: 'Breadcrumb', icon: '›', group: 'Advanced & inline blocks', description: 'Show this page’s location.', keywords: 'navigation path' }
];
// Pseudo block types handled by their own commit branch in commitBlockPickerItem rather than
// convertBlockType - they don't change the current block's type, they navigate away to a newly
// created page/database (__create_*), open a second search menu in place (__link_to_page,
// __mention_person), or splice in reusable content (dynamic __template_<id> entries added to
// the menu at open time from GetSuggestedBlockTemplates).
const CREATE_MENU_TYPES = new Set(['__create_page', '__create_database']);
const MEDIA_TYPES = new Set(['image', 'embed', 'video', 'audio', 'pdf', 'file']);
const TEXTLESS_TYPES = new Set(['divider', 'page_link', 'linked_database', 'inline_database', 'breadcrumb', 'table_of_contents', ...MEDIA_TYPES]);
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
        // Cross-block text selection - see "---- Cross-block selection ----" below. Native
        // Selection/Range can't span separate contentEditable elements (each block is its own),
        // so a drag/shift-click/shift-arrow that crosses a block boundary switches from the
        // browser's native in-block selection to this synthetic whole-block highlight instead.
        blockDragSelect: null,
        blockSelection: null,
        lastSnapshot: undefined,
        baseSnapshot: undefined,
        historyKey: normalizeHistoryKey(historyKey),
        lastCursorKey: null,
        isOffline: !navigator.onLine,
        offlineBanner: null,
        // Populated once per editor session (not re-fetched per keystroke) and merged into the
        // +//slash menu's "Suggested" group - see GetSuggestedBlockTemplates.
        suggestedBlockTemplates: []
    };
    states.set(container, state);
    setBlocks(container, initialBlocksJson, historyKey);
    dotNetRef.invokeMethodAsync('GetSuggestedBlockTemplates').then(templates => {
        state.suggestedBlockTemplates = (templates || []).map(template => ({
            type: `__template_${template.id}`,
            label: template.name,
            icon: '🧩',
            group: 'Suggested',
            description: `${template.blockCount} block${template.blockCount === 1 ? '' : 's'} · ${template.preview}`,
            keywords: `template suggested ${template.name}`
        }));
    }).catch(() => { /* circuit may be gone, or no templates yet - menu just skips the group */ });

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
    container.addEventListener('mousedown', event => onBlockMouseDown(state, event));
    document.addEventListener('mousemove', state.blockSelectMoveHandler = event => onBlockMouseMove(state, event));
    document.addEventListener('mouseup', state.blockSelectUpHandler = () => { state.blockDragSelect = null; });
    container.addEventListener('copy', event => onBlockSelectionCopy(state, event));
    container.addEventListener('cut', event => onBlockSelectionCut(state, event));
    document.addEventListener('dragstart', state.externalPageDragStartHandler = event => beginExternalPageDrag(event));
    container.addEventListener('dragenter', state.externalPageDragEnterHandler = event => onExternalPageDragOver(state, event));
    container.addEventListener('dragover', state.externalPageDragOverHandler = event => onExternalPageDragOver(state, event));
    container.addEventListener('dragleave', state.externalPageDragLeaveHandler = event => onExternalPageDragLeave(state, event));
    container.addEventListener('drop', state.externalPageDropHandler = event => onExternalPageDrop(state, event));
    document.addEventListener('dragend', state.externalPageDragEndHandler = () => container.classList.remove('wiki-page-drop-active'));
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

    // The old .wiki-block elements are about to be discarded - any reference to them in an
    // in-progress or finalized cross-block selection would be dangling after this.
    state.blockDragSelect = null;
    state.blockSelection = null;
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
    if (state.blockSelectMoveHandler) document.removeEventListener('mousemove', state.blockSelectMoveHandler);
    if (state.blockSelectUpHandler) document.removeEventListener('mouseup', state.blockSelectUpHandler);
    if (state.externalPageDragStartHandler) document.removeEventListener('dragstart', state.externalPageDragStartHandler);
    if (state.externalPageDragEnterHandler) container.removeEventListener('dragenter', state.externalPageDragEnterHandler);
    if (state.externalPageDragOverHandler) container.removeEventListener('dragover', state.externalPageDragOverHandler);
    if (state.externalPageDragLeaveHandler) container.removeEventListener('dragleave', state.externalPageDragLeaveHandler);
    if (state.externalPageDropHandler) container.removeEventListener('drop', state.externalPageDropHandler);
    if (state.externalPageDragEndHandler) document.removeEventListener('dragend', state.externalPageDragEndHandler);
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
    if (block.type === 'page_link') {
        el.dataset.pageId = (block.props && block.props.pageId) || '';
        el.dataset.pageTitle = (block.props && block.props.pageTitle) || block.richText?.[0]?.text || 'Untitled';
        el.dataset.pageIcon = (block.props && block.props.pageIcon) || '📄';
    }
    if (MEDIA_TYPES.has(block.type)) {
        el.dataset.url = (block.props && block.props.url) || '';
        el.dataset.fileName = (block.props && block.props.fileName) || '';
        el.dataset.notionBlockId = (block.props && block.props.notionBlockId) || '';
        el.dataset.mediaKind = (block.props && block.props.mediaKind) || '';
    }
    if (block.type === 'linked_database' || block.type === 'inline_database') {
        el.dataset.databaseId = (block.props && block.props.databaseId) || '';
        el.dataset.databaseTitle = (block.props && block.props.databaseTitle) || '';
        el.dataset.databaseIcon = (block.props && block.props.databaseIcon) || '';
        el.dataset.databaseViewId = (block.props && block.props.databaseViewId) || '';
        el.dataset.databaseViewName = (block.props && block.props.databaseViewName) || '';
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
        // Without this, the same mousedown that opens the menu below keeps bubbling up to the
        // document-level "click outside closes floating menus" listener (see
        // state.outsideClickHandler / closeFloatingMenus), which then immediately closes the
        // menu this handler just opened - the picker would render for a single frame and vanish.
        // The "/" trigger never had this problem because it fires from a text `input` event, not
        // a mousedown.
        event.stopPropagation();
        closeInlineToolbar(state);
        closeBlockMenu(state);
        const block = emptyBlock('paragraph');
        block.indentLevel = blockIndent(el);
        const created = createBlockElement(block, state);
        const branch = getBlockBranch(el);
        branch[branch.length - 1].after(created);
        refreshBlockPresentation(state.container);
        focusBlock(created);
        notifyChanged(state);
        // Notion's "+" opens the same searchable type/create/suggestions menu as typing "/" -
        // previously this just silently inserted a blank paragraph with no way to pick a type
        // from the button itself (you had to already know to type "/").
        const newContent = created.querySelector('.wiki-block-content');
        if (newContent) {
            openSuggestionMenu(state, {
                kind: 'slash',
                anchor: newContent,
                ariaLabel: 'Insert a block',
                items: blockPickerItems(state),
                group: item => item.group,
                icon: item => item.icon,
                label: item => item.label,
                description: item => item.description,
                commit: item => commitBlockPickerItem(state, newContent.closest('.wiki-block'), item)
            });
        }
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

    if (block.type === 'tab') {
        body.appendChild(createTabsBody(block, state));
        return body;
    }

    if (block.type === 'breadcrumb' || block.type === 'table_of_contents') {
        const placeholder = document.createElement('div');
        placeholder.className = `wiki-${block.type.replaceAll('_', '-')}`;
        placeholder.textContent = block.type === 'breadcrumb' ? 'Workspace / Parent page / Current page' : 'Table of contents';
        body.appendChild(placeholder);
        return body;
    }

    if (MEDIA_TYPES.has(block.type)) {
        body.appendChild(createMediaBody(block, state));
        return body;
    }

    if (block.type === 'page_link') {
        body.appendChild(createPageLinkBody(block));
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
        // Belt-and-suspenders: this checkbox lives inside the page's <EditForm>, so if focus
        // ever lands here (a stray Tab, or a future bug like the one primaryFocusTarget() above
        // fixes), Enter must not be allowed to trigger the browser's native implicit form
        // submission - Notion's own to-do checkbox has no such trap since Enter never reaches it.
        checkbox.addEventListener('keydown', event => {
            if (event.key === 'Enter') event.preventDefault();
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

    const content = createContentEditable(block, state);
    body.appendChild(content);
    if (block.type === 'equation' || block.type === 'code') {
        attachRichPreview(body, content, block);
    }
    return body;
}

// Equation/code blocks show a rendered KaTeX/highlight.js preview whenever the block isn't
// focused, and fall back to the plain contentEditable text while it is - two separate DOM nodes
// (see the .wiki-has-rich-preview rules in app.css) so re-rendering the preview never touches
// the editable element itself and can't disturb its cursor position, even while live-updating on
// every keystroke.
function attachRichPreview(body, content, block) {
    const preview = document.createElement('div');
    preview.className = block.type === 'equation' ? 'wiki-equation-preview' : 'wiki-code-preview';
    if (block.type === 'equation') preview.dataset.placeholder = placeholderFor('equation');
    body.classList.add('wiki-has-rich-preview');
    body.appendChild(preview);

    const render = () => {
        const text = content.textContent || '';
        if (block.type === 'equation') renderKatexInto(preview, text);
        else renderHighlightInto(preview, text, block.props && block.props.language);
    };
    preview.addEventListener('mousedown', event => {
        event.preventDefault();
        content.focus();
    });
    content.addEventListener('focus', () => body.classList.add('is-editing-rich'));
    content.addEventListener('blur', () => body.classList.remove('is-editing-rich'));
    content.addEventListener('input', render);
    render();
}

function renderKatexInto(el, latex) {
    if (!latex.trim()) {
        el.textContent = '';
        el.classList.remove('has-error');
        return;
    }
    if (!window.katex) {
        el.textContent = latex;
        return;
    }
    try {
        window.katex.render(latex, el, { throwOnError: false, displayMode: true });
        el.classList.remove('has-error');
    } catch {
        el.textContent = latex;
        el.classList.add('has-error');
    }
}

function renderHighlightInto(el, code, language) {
    if (!code.trim()) {
        el.replaceChildren();
        return;
    }
    if (!window.hljs) {
        el.textContent = code;
        return;
    }
    try {
        const result = language && window.hljs.getLanguage(language)
            ? window.hljs.highlight(code, { language })
            : window.hljs.highlightAuto(code);
        const codeEl = document.createElement('code');
        codeEl.className = `language-${result.language || language || 'plaintext'} hljs`;
        codeEl.innerHTML = result.value;
        el.replaceChildren(codeEl);
    } catch {
        el.textContent = code;
    }
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

function createTabsBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-tabs-editor';

    const tabList = document.createElement('div');
    tabList.className = 'wiki-tab-list';
    tabList.setAttribute('role', 'tablist');
    tabList.setAttribute('aria-label', 'Tabs');

    const panels = document.createElement('div');
    panels.className = 'wiki-tab-editor-panels';

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'wiki-tab-add';
    add.setAttribute('aria-label', 'Add tab');
    add.textContent = '+ Add tab';

    const parseTabs = () => {
        try {
            const parsed = JSON.parse((block.props && block.props.tabsJson) || '[]');
            if (Array.isArray(parsed) && parsed.length > 0) {
                return parsed.map((tab, index) => ({
                    title: String(tab && tab.title || `Tab ${index + 1}`),
                    richText: Array.isArray(tab && tab.richText) ? tab.richText : []
                }));
            }
        } catch { /* use the plain-text fallback below */ }

        const text = (block.richText || []).map(span => span.text || '').join('');
        const fallback = text
            ? text.split('|||').map(value => value.trim())
            : ['', ''];
        return fallback.map((value, index) => ({
            title: `Tab ${index + 1}`,
            richText: value ? [{ text: value }] : []
        }));
    };

    const activateTab = tabId => {
        for (const trigger of tabList.querySelectorAll(':scope > .wiki-tab-trigger')) {
            const active = trigger.dataset.tabId === tabId;
            trigger.classList.toggle('is-active', active);
            trigger.setAttribute('aria-selected', active ? 'true' : 'false');
            trigger.tabIndex = active ? 0 : -1;
        }
        for (const panel of panels.querySelectorAll(':scope > .wiki-tab-editor-panel')) {
            const active = panel.dataset.tabId === tabId;
            panel.classList.toggle('is-active', active);
            panel.hidden = !active;
        }
        wrapper.dataset.activeTabId = tabId;
    };

    const renderTab = value => {
        // The editor can also run in a non-secure about:blank test/document context where
        // crypto.randomUUID is unavailable, so use a module-local DOM identity here. The id
        // is transient UI state; persisted tab identity is its ordered title/content pair.
        const tabId = `tab-${++tabEditorSequence}`;
        const triggerId = `wiki-tab-${block.id}-${tabId}`;
        const panelId = `${triggerId}-panel`;

        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'wiki-tab-trigger';
        trigger.dataset.tabId = tabId;
        trigger.id = triggerId;
        trigger.setAttribute('role', 'tab');
        trigger.setAttribute('aria-controls', panelId);
        trigger.addEventListener('click', () => activateTab(tabId));
        trigger.addEventListener('keydown', event => {
            if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
            event.preventDefault();
            const triggers = [...tabList.querySelectorAll(':scope > .wiki-tab-trigger')];
            const currentIndex = triggers.indexOf(trigger);
            const nextIndex = event.key === 'ArrowLeft'
                ? (currentIndex - 1 + triggers.length) % triggers.length
                : (currentIndex + 1) % triggers.length;
            activateTab(triggers[nextIndex].dataset.tabId);
            triggers[nextIndex].focus();
        });

        const panel = document.createElement('section');
        panel.className = 'wiki-tab-editor-panel';
        panel.dataset.tabId = tabId;
        panel.id = panelId;
        panel.setAttribute('role', 'tabpanel');
        panel.setAttribute('aria-labelledby', triggerId);

        const toolbar = document.createElement('div');
        toolbar.className = 'wiki-tab-controls';
        const title = document.createElement('input');
        title.type = 'text';
        title.className = 'wiki-tab-title';
        title.setAttribute('aria-label', 'Tab name');
        title.maxLength = 80;
        title.value = value.title || '';
        title.addEventListener('input', () => {
            refreshTabControls(wrapper);
            scheduleNotify(state);
        });

        const moveLeft = document.createElement('button');
        moveLeft.type = 'button';
        moveLeft.setAttribute('aria-label', 'Move tab left');
        moveLeft.title = 'Move tab left';
        moveLeft.textContent = '←';
        moveLeft.addEventListener('click', () => {
            const previousTrigger = trigger.previousElementSibling;
            const previousPanel = panel.previousElementSibling;
            if (!previousTrigger?.classList.contains('wiki-tab-trigger')
                || !previousPanel?.classList.contains('wiki-tab-editor-panel')) return;
            tabList.insertBefore(trigger, previousTrigger);
            panels.insertBefore(panel, previousPanel);
            refreshTabControls(wrapper);
            notifyChanged(state);
        });

        const moveRight = document.createElement('button');
        moveRight.type = 'button';
        moveRight.setAttribute('aria-label', 'Move tab right');
        moveRight.title = 'Move tab right';
        moveRight.textContent = '→';
        moveRight.addEventListener('click', () => {
            const nextTrigger = trigger.nextElementSibling;
            const nextPanel = panel.nextElementSibling;
            if (!nextTrigger?.classList.contains('wiki-tab-trigger')
                || !nextPanel?.classList.contains('wiki-tab-editor-panel')) return;
            tabList.insertBefore(nextTrigger, trigger);
            panels.insertBefore(nextPanel, panel);
            refreshTabControls(wrapper);
            notifyChanged(state);
        });

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.setAttribute('aria-label', 'Remove tab');
        remove.title = 'Remove tab';
        remove.textContent = '×';
        remove.addEventListener('click', () => {
            const triggers = [...tabList.querySelectorAll(':scope > .wiki-tab-trigger')];
            if (triggers.length <= 2) return;
            const wasActive = wrapper.dataset.activeTabId === tabId;
            const index = triggers.indexOf(trigger);
            trigger.remove();
            panel.remove();
            refreshTabControls(wrapper);
            if (wasActive) {
                const remaining = [...tabList.querySelectorAll(':scope > .wiki-tab-trigger')];
                activateTab(remaining[Math.min(index, remaining.length - 1)].dataset.tabId);
            }
            notifyChanged(state);
        });

        const content = document.createElement('div');
        content.className = 'wiki-block-content wiki-tab-content';
        content.contentEditable = 'true';
        content.dataset.placeholder = 'Type in this tab';
        content.innerHTML = htmlFromRichText(Array.isArray(value.richText) ? value.richText : []);
        content.addEventListener('input', () => scheduleNotify(state));

        toolbar.append(title, moveLeft, moveRight, remove);
        panel.append(toolbar, content);
        tabList.insertBefore(trigger, add);
        panels.appendChild(panel);
        return tabId;
    };

    add.addEventListener('click', () => {
        if (tabList.querySelectorAll(':scope > .wiki-tab-trigger').length >= 8) return;
        const count = tabList.querySelectorAll(':scope > .wiki-tab-trigger').length;
        const tabId = renderTab({ title: `Tab ${count + 1}`, richText: [] });
        refreshTabControls(wrapper);
        activateTab(tabId);
        panels.querySelector(`:scope > .wiki-tab-editor-panel[data-tab-id="${tabId}"] .wiki-tab-title`)?.focus();
        notifyChanged(state);
    });

    tabList.appendChild(add);
    wrapper.append(tabList, panels);
    const values = parseTabs();
    while (values.length < 2) values.push({ title: `Tab ${values.length + 1}`, richText: [] });
    const firstTabId = values.map(renderTab)[0];
    refreshTabControls(wrapper);
    activateTab(firstTabId);
    return wrapper;
}

function refreshTabControls(wrapper) {
    const tabList = wrapper.querySelector(':scope > .wiki-tab-list');
    const panels = wrapper.querySelector(':scope > .wiki-tab-editor-panels');
    if (!tabList || !panels) return;

    const triggers = [...tabList.querySelectorAll(':scope > .wiki-tab-trigger')];
    triggers.forEach((trigger, index) => {
        const panel = panels.querySelector(`:scope > .wiki-tab-editor-panel[data-tab-id="${trigger.dataset.tabId}"]`);
        const title = panel?.querySelector('.wiki-tab-title');
        trigger.textContent = title?.value.trim() || `Tab ${index + 1}`;
        panel?.querySelector('[aria-label="Move tab left"]')?.toggleAttribute('disabled', index === 0);
        panel?.querySelector('[aria-label="Move tab right"]')?.toggleAttribute('disabled', index === triggers.length - 1);
        panel?.querySelector('[aria-label="Remove tab"]')?.toggleAttribute('disabled', triggers.length <= 2);
    });
    tabList.querySelector(':scope > .wiki-tab-add')?.toggleAttribute('disabled', triggers.length >= 8);
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

function mediaUrlLabel(type) {
    switch (type) {
        case 'image': return 'Image';
        case 'video': return 'Video';
        case 'audio': return 'Audio';
        case 'pdf': return 'PDF';
        case 'file': return 'File';
        default: return 'Embed';
    }
}

function mediaUrlPlaceholder(type) {
    switch (type) {
        case 'image': return 'Paste an image URL and press Enter';
        case 'video': return 'Paste a video URL and press Enter';
        case 'audio': return 'Paste an audio URL and press Enter';
        case 'pdf': return 'Paste a PDF URL and press Enter';
        case 'file': return 'Paste a file URL and press Enter';
        default: return 'Paste a link and press Enter';
    }
}

function createMediaBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-media-block';
    const url = (block.props && block.props.url) || '';
    wrapper.classList.toggle('has-source', Boolean(url));

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'form-control form-control-sm';
    input.placeholder = mediaUrlPlaceholder(block.type);
    input.value = url;
    input.setAttribute('aria-label', `${mediaUrlLabel(block.type)} URL`);

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

function icon(bootstrapIconClass) {
    const el = document.createElement('i');
    el.className = `bi ${bootstrapIconClass}`;
    el.setAttribute('aria-hidden', 'true');
    return el;
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

    // type is 'video'/'audio' for real Video/Audio blocks (Phase 5.4); mediaKind is the older
    // fallback still set on Embed blocks imported from Notion before that split existed (see
    // NotionMapping.MapBlock and WikiBlockHtmlRenderer.RenderEmbed's own mediaKind fallback).
    const playerKind = type === 'video' || type === 'audio' ? type : (mediaKind === 'video' || mediaKind === 'audio' ? mediaKind : null);
    if (playerKind) {
        const player = document.createElement(playerKind);
        player.className = 'wiki-embed-media';
        player.src = safeUrl;
        player.controls = true;
        player.preload = 'metadata';
        preview.appendChild(player);
        return;
    }

    if (type === 'pdf') {
        const wrapper = document.createElement('div');
        wrapper.className = 'wiki-pdf-block';
        const frame = document.createElement('iframe');
        frame.className = 'wiki-pdf-viewer';
        frame.src = safeUrl;
        frame.title = 'PDF document';
        frame.loading = 'lazy';
        const link = document.createElement('a');
        link.href = safeUrl;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.append(icon('bi-file-earmark-pdf'), document.createTextNode(' Open PDF'));
        wrapper.append(frame, link);
        preview.appendChild(wrapper);
        return;
    }

    if (type === 'file') {
        const link = document.createElement('a');
        link.className = 'wiki-file-block';
        link.href = safeUrl;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.setAttribute('download', '');
        const label = document.createElement('span');
        label.textContent = fileName || safeUrl;
        link.append(icon('bi-file-earmark-arrow-down'), label);
        preview.appendChild(link);
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

function inlineViewIcon(type) {
    switch (type) {
        case 'board': return 'bi-kanban';
        case 'calendar': return 'bi-calendar3';
        case 'gallery': return 'bi-grid';
        case 'timeline': return 'bi-calendar-range';
        case 'chart': return 'bi-bar-chart';
        case 'form': return 'bi-ui-checks';
        case 'list': return 'bi-list-ul';
        default: return 'bi-table';
    }
}

function createPageLinkBody(block) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-block-content wiki-page-link-card';

    const pageId = String((block.props && block.props.pageId) || '').trim();
    const title = String((block.props && block.props.pageTitle)
        || block.richText?.[0]?.text
        || 'Untitled').trim() || 'Untitled';
    const pageIcon = String((block.props && block.props.pageIcon) || '📄').trim() || '📄';

    const icon = document.createElement('span');
    icon.className = 'wiki-page-link-icon';
    icon.setAttribute('aria-hidden', 'true');
    icon.textContent = pageIcon;

    const titleElement = document.createElement(isUuid(pageId) ? 'a' : 'span');
    if (titleElement instanceof HTMLAnchorElement) titleElement.href = `wikilink:${pageId}`;
    titleElement.className = 'wiki-page-link-title';
    titleElement.textContent = title;

    const arrow = document.createElement('span');
    arrow.className = 'wiki-page-link-arrow';
    arrow.setAttribute('aria-hidden', 'true');
    arrow.textContent = '›';

    wrapper.append(icon, titleElement, arrow);
    return wrapper;
}

function createLinkedDatabaseBody(block, state) {
    const wrapper = document.createElement('div');
    wrapper.className = 'wiki-linked-database-editor';
    const isInline = block.type === 'inline_database';
    wrapper.classList.toggle('is-inline', isInline);
    wrapper.classList.toggle('is-live-view', !isInline);
    let databaseId = (block.props && block.props.databaseId) || '';
    let databaseTitle = (block.props && block.props.databaseTitle) || '';
    let databaseIcon = (block.props && block.props.databaseIcon) || '';
    let databaseViewId = (block.props && block.props.databaseViewId) || '';
    let databaseViewName = (block.props && block.props.databaseViewName) || '';
    let searchGeneration = 0;

    const syncBlockDataset = () => {
        const blockEl = wrapper.closest('.wiki-block');
        if (!blockEl) return;
        blockEl.dataset.databaseId = databaseId;
        blockEl.dataset.databaseTitle = databaseTitle;
        blockEl.dataset.databaseIcon = databaseIcon;
        blockEl.dataset.databaseViewId = databaseViewId;
        blockEl.dataset.databaseViewName = databaseViewName;
    };

    const clearSource = () => {
        databaseId = '';
        databaseTitle = '';
        databaseIcon = '';
        databaseViewId = '';
        databaseViewName = '';
        syncBlockDataset();
        render();
        notifyChanged(state);
    };

    const clearView = () => {
        databaseViewId = '';
        databaseViewName = '';
        syncBlockDataset();
        render();
        notifyChanged(state);
    };

    const renderViewChooser = () => {
        wrapper.innerHTML = '<div class="wiki-inline-database-loading">Loading saved views…</div>';
        state.dotNetRef.invokeMethodAsync('GetInlineDatabase', databaseId).then(snapshot => {
            wrapper.innerHTML = '';
            if (!snapshot) {
                const unavailable = document.createElement('div');
                unavailable.className = 'wiki-inline-database-error';
                unavailable.textContent = 'This database is unavailable or you no longer have access.';
                const changeSource = document.createElement('button');
                changeSource.type = 'button';
                changeSource.className = 'wiki-linked-database-change';
                changeSource.textContent = 'Change source';
                changeSource.addEventListener('click', clearSource);
                wrapper.append(unavailable, changeSource);
                return;
            }

            const chooser = document.createElement('div');
            chooser.className = 'wiki-linked-view-chooser';
            const heading = document.createElement('div');
            heading.className = 'wiki-linked-view-chooser-heading';
            const label = document.createElement('strong');
            label.textContent = `Choose a view from ${snapshot.title}`;
            const changeSource = document.createElement('button');
            changeSource.type = 'button';
            changeSource.className = 'wiki-linked-database-change';
            changeSource.textContent = 'Change source';
            changeSource.addEventListener('click', clearSource);
            heading.append(label, changeSource);
            chooser.appendChild(heading);

            const views = document.createElement('div');
            views.className = 'wiki-linked-view-options';
            for (const view of snapshot.views || []) {
                const option = document.createElement('button');
                option.type = 'button';
                option.className = 'wiki-linked-view-option';
                option.innerHTML = `<i class="bi ${inlineViewIcon(view.type)}" aria-hidden="true"></i>`;
                const name = document.createElement('span');
                name.textContent = view.name || view.type;
                option.appendChild(name);
                option.addEventListener('click', () => {
                    databaseViewId = view.id;
                    databaseViewName = view.name || view.type;
                    syncBlockDataset();
                    render();
                    notifyChanged(state);
                });
                views.appendChild(option);
            }
            if (!snapshot.views || snapshot.views.length === 0) {
                const empty = document.createElement('span');
                empty.className = 'wiki-linked-database-empty';
                empty.textContent = 'This database has no saved views.';
                views.appendChild(empty);
            }
            chooser.appendChild(views);
            wrapper.appendChild(chooser);
        }).catch(() => {
            wrapper.innerHTML = '<div class="wiki-inline-database-error">Unable to load saved views.</div>';
        });
    };

    const renderLinkedView = () => {
        wrapper.innerHTML = '<div class="wiki-inline-database-loading">Loading linked view…</div>';
        state.dotNetRef.invokeMethodAsync('GetLinkedDatabase', databaseId, databaseViewId).then(snapshot => {
            if (!snapshot) {
                wrapper.innerHTML = '';
                const unavailable = document.createElement('div');
                unavailable.className = 'wiki-inline-database-error';
                unavailable.textContent = `${databaseViewName || 'This view'} is no longer available.`;
                const actions = document.createElement('div');
                actions.className = 'wiki-inline-database-actions';
                const changeView = document.createElement('button');
                changeView.type = 'button';
                changeView.textContent = 'Choose another view';
                changeView.addEventListener('click', clearView);
                const changeSource = document.createElement('button');
                changeSource.type = 'button';
                changeSource.textContent = 'Change source';
                changeSource.addEventListener('click', clearSource);
                actions.append(changeView, changeSource);
                wrapper.append(unavailable, actions);
                return;
            }

            const selectedView = (snapshot.views || [])[0];
            if (selectedView) {
                databaseViewName = selectedView.name || selectedView.type;
                syncBlockDataset();
            }
            renderInlineDatabaseSnapshot(wrapper, state, snapshot, clearSource, selectedView?.id || databaseViewId, {
                isLinked: true,
                allowCreate: false,
                changeView: clearView,
                saveCell: async (rowId, propertyId, nextValue) => {
                    const saved = await state.dotNetRef.invokeMethodAsync(
                        'SaveInlineDatabaseCell', databaseId, rowId, propertyId, nextValue);
                    return saved
                        ? state.dotNetRef.invokeMethodAsync('GetLinkedDatabase', databaseId, databaseViewId)
                        : null;
                },
                moveRow: async (rowId, groupByPropertyId, optionId, newSortOrder) => {
                    const moved = await state.dotNetRef.invokeMethodAsync(
                        'MoveInlineDatabaseRow', databaseId, rowId, groupByPropertyId, optionId || null, newSortOrder);
                    return moved
                        ? state.dotNetRef.invokeMethodAsync('GetLinkedDatabase', databaseId, databaseViewId)
                        : null;
                }
            });
        }).catch(() => {
            wrapper.innerHTML = '<div class="wiki-inline-database-error">Unable to load this linked view.</div>';
        });
    };

    const render = () => {
        wrapper.innerHTML = '';
        if (databaseId) {
            wrapper.classList.add('has-database');
            if (isInline) {
                renderInlineDatabase(wrapper, state, databaseId, clearSource);
                return;
            }
            if (!databaseViewId) {
                renderViewChooser();
                return;
            }
            renderLinkedView();
            return;
        }

        wrapper.classList.remove('has-database');
        const chooser = document.createElement('div');
        chooser.className = 'wiki-linked-database-chooser';
        const input = document.createElement('input');
        input.type = 'search';
        input.className = 'form-control form-control-sm';
        input.placeholder = isInline ? 'Search databases to show inline…' : 'Search databases for a linked view…';
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
                        databaseViewId = '';
                        databaseViewName = '';
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

function renderInlineDatabaseSnapshot(wrapper, state, snapshot, resetDatabase, selectedViewId = null, options = {}) {
    wrapper.innerHTML = '';
    const views = snapshot.views || [];
    const activeView = views.find(view => view.id === selectedViewId) || views[0] || null;
    const canEdit = snapshot.canEdit !== false;
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
    if (options.isLinked && activeView) {
        const viewName = document.createElement('span');
        viewName.className = 'wiki-linked-view-badge';
        viewName.textContent = activeView.name || activeView.type;
        identity.appendChild(viewName);
    }
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
    headerActions.appendChild(open);
    if (options.changeView) {
        const changeView = document.createElement('button');
        changeView.type = 'button';
        changeView.textContent = 'Change view';
        changeView.addEventListener('click', options.changeView);
        headerActions.appendChild(changeView);
    }
    headerActions.appendChild(change);
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
            renderInlineDatabaseSnapshot(wrapper, state, snapshot, resetDatabase, view.id, options));
        viewTabs.appendChild(tab);
    }

    const scroller = document.createElement('div');
    scroller.className = 'wiki-inline-database-scroller';
    const isBoard = activeView?.type === 'board' && boardGroupByPropertyId;
    if (isBoard) {
        const moveRow = options.moveRow || (async (rowId, propertyId, optionId, newSortOrder) =>
            state.dotNetRef.invokeMethodAsync(
                'MoveInlineDatabaseRow', snapshot.id, rowId, propertyId, optionId || null, newSortOrder));
        scroller.appendChild(createInlineBoard(
            snapshot,
            boardGroupByPropertyId,
            rowId => state.dotNetRef.invokeMethodAsync('OpenLinkedDatabaseRow', snapshot.id, rowId),
            async (rowId, optionId, newSortOrder) => {
                const updated = await moveRow(rowId, boardGroupByPropertyId, optionId, newSortOrder);
                if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id, options);
            },
            options.allowCreate === false || !canEdit ? null : async (optionId, title) => {
                const updated = await state.dotNetRef.invokeMethodAsync(
                    'AddInlineBoardTask',
                    snapshot.id,
                    boardGroupByPropertyId,
                    optionId || null,
                    title);
                if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id, options);
            },
            canEdit));
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
                cell.appendChild(createInlineCellEditor(
                    state,
                    snapshot.id,
                    row.id,
                    property,
                    value,
                    updated => {
                        if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id, options);
                    },
                    options.saveCell,
                    canEdit));
                tableRow.appendChild(cell);
            }
            tbody.appendChild(tableRow);
        }
        table.appendChild(tbody);
        scroller.appendChild(table);
    }

    wrapper.append(header);
    if (views.length > 0 && !options.isLinked) wrapper.append(viewTabs);
    wrapper.append(scroller);
    if (!isBoard && options.allowCreate !== false && canEdit) {
        const footer = document.createElement('div');
        footer.className = 'wiki-inline-database-footer';
        const addRow = document.createElement('button');
        addRow.type = 'button';
        addRow.innerHTML = '<span>+</span> New row';
        addRow.addEventListener('click', async () => {
            addRow.disabled = true;
            try {
                const updated = await state.dotNetRef.invokeMethodAsync('AddInlineDatabaseRow', snapshot.id);
                if (updated) renderInlineDatabaseSnapshot(wrapper, state, updated, resetDatabase, activeView?.id, options);
            } finally {
                addRow.disabled = false;
            }
        });
        footer.appendChild(addRow);
        wrapper.append(footer);
    }
}

function createInlineBoard(snapshot, groupByPropertyId, openRow, moveRow, addTask, canEdit = true) {
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
        if (canEdit) {
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
        }

        for (const row of rows) {
            const card = document.createElement('button');
            card.type = 'button';
            card.className = 'wiki-inline-board-card';
            card.draggable = canEdit;
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

        if (addTask) {
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
        }
        board.appendChild(column);
    }
    return board;
}

function createInlineCellEditor(state, databaseId, rowId, property, value, onSaved, saveCell = null, canEdit = true) {
    const commit = async nextValue => {
        try {
            const updated = saveCell
                ? await saveCell(rowId, property.id, nextValue)
                : await state.dotNetRef.invokeMethodAsync(
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
    if (!canEdit || property.isReadOnly || property.type === 'relation' || property.type === 'person' || property.type === 'files') {
        const readOnly = document.createElement('span');
        readOnly.className = 'wiki-inline-cell-readonly';
        readOnly.textContent = value;
        if (!canEdit) {
            readOnly.title = 'You have view-only access to this database.';
        } else if (!property.isReadOnly) {
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
        checkMarkdownShortcut(state, content);
        scheduleNotify(state);
        reportCursor(state, content);
    });
    content.addEventListener('keyup', () => reportCursor(state, content));
    content.addEventListener('paste', event => {
        event.preventDefault();
        const text = (event.clipboardData || window.clipboardData).getData('text/plain');
        if (looksLikeMarkdown(text)) {
            pasteMarkdownAsBlocks(state, content, text);
            return;
        }
        document.execCommand('insertText', false, text);
    });

    return content;
}

// A conservative heuristic on purpose - false positives (reformatting text the user just
// wanted pasted verbatim, e.g. "Fix bug #123" or a filesystem path) are far more annoying than
// false negatives (an obvious markdown paste landing as one plain-text block, still editable
// afterward). Requires an actual block-level marker at the START of some line, or a clearly
// intentional inline marker (bold/italic/inline-code/link) - a stray "#" or "-" mid-sentence
// doesn't count.
const MARKDOWN_PASTE_LINE_PATTERN = /^(#{1,6}\s|[-*+]\s|\d+[.)]\s|>\s|```|\|.+\|)/;
const MARKDOWN_PASTE_INLINE_PATTERN = /\*\*[^*\n]+\*\*|__[^_\n]+__|`[^`\n]+`|\[[^\]\n]+\]\([^)\n]+\)/;

function looksLikeMarkdown(text) {
    if (!text || !text.trim()) return false;
    if (text.split(/\r?\n/).some(line => MARKDOWN_PASTE_LINE_PATTERN.test(line.trim()))) return true;
    return MARKDOWN_PASTE_INLINE_PATTERN.test(text);
}

async function pasteMarkdownAsBlocks(state, content, markdownText) {
    let parsedBlocks;
    try {
        const blocksJson = await state.dotNetRef.invokeMethodAsync('ParseMarkdownToBlocksJson', markdownText);
        parsedBlocks = JSON.parse(blocksJson || '[]');
    } catch {
        parsedBlocks = null;
    }

    if (!parsedBlocks || parsedBlocks.length === 0) {
        // The circuit may be gone, or the parse produced nothing usable - the paste shouldn't
        // just silently vanish, so fall back to the plain-text behavior this replaced.
        document.execCommand('insertText', false, markdownText);
        return;
    }

    const blockEl = content.closest('.wiki-block');
    const wasEmpty = content.textContent.trim().length === 0;
    const created = parsedBlocks.map(block => createBlockElement(block, state));

    if (wasEmpty) {
        blockEl.replaceWith(...created);
    } else {
        blockEl.after(...created);
    }
    refreshBlockPresentation(state.container);
    focusBlock(created[created.length - 1]);
    notifyChanged(state);
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

    if (state.blockSelection) {
        if (event.key === 'Escape') {
            event.preventDefault();
            clearBlockSelection(state);
            return;
        }
        if (event.key === 'Backspace' || event.key === 'Delete') {
            event.preventDefault();
            deleteBlockSelection(state);
            return;
        }
        if (event.shiftKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
            event.preventDefault();
            extendBlockSelectionByArrow(state, event.key === 'ArrowUp' ? -1 : 1);
            return;
        }
        const isCopyOrCut = (event.ctrlKey || event.metaKey) && (event.key.toLowerCase() === 'c' || event.key.toLowerCase() === 'x');
        if (!isCopyOrCut && !event.ctrlKey && !event.metaKey && !event.altKey) {
            // Any other unmodified key (typing, a plain arrow, Enter, Tab...) isn't a selection
            // action - drop back to a normal caret in whichever block still has DOM focus (the
            // one the drag/shift-click started from) rather than silently editing only that one
            // block while the rest of the highlighted range stays selected and stale-looking.
            clearBlockSelection(state);
        }
    }

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
    // Native Shift+ArrowUp/Down already extends the selection across wrapped lines within this
    // one contentEditable just fine - it's only once the caret is at this block's own start/end
    // edge that the browser has nowhere further to extend into (each block is a separate
    // contentEditable), which is exactly where cross-block selection needs to take over.
    if (event.key === 'ArrowUp' && event.shiftKey && isCaretAtStart(content)) {
        const previous = blockEl.previousElementSibling;
        if (previous) {
            event.preventDefault();
            window.getSelection()?.removeAllRanges();
            applyBlockRangeSelection(state, blockEl, previous);
        }
        return;
    }
    if (event.key === 'ArrowDown' && event.shiftKey && isCaretAtEnd(content)) {
        const next = blockEl.nextElementSibling;
        if (next) {
            event.preventDefault();
            window.getSelection()?.removeAllRanges();
            applyBlockRangeSelection(state, blockEl, next);
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
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'u') {
        event.preventDefault();
        toggleInlineTag('u');
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

function blockPickerItems(state) {
    // Suggested templates first (Notion surfaces suggestions above the generic block list too),
    // then every real block type, then the Create-a-page/Create-a-database pseudo-entries.
    return [...state.suggestedBlockTemplates, ...BLOCK_TYPES];
}

// Shared commit handler for both the "/" trigger and the "+" button - most items just convert
// the current block's type, but Page/Database/Link-to-page/Mention/Suggested-template entries
// don't touch the current block's type at all (they navigate to a new page/database, open a
// second search menu in place, or splice in reusable content), so they need their own branches
// instead of falling into convertBlockType.
function commitBlockPickerItem(state, blockEl, item) {
    if (CREATE_MENU_TYPES.has(item.type)) {
        const method = item.type === '__create_page' ? 'CreateChildPageFromEditor' : 'CreateChildDatabaseFromEditor';
        state.dotNetRef.invokeMethodAsync(method, '').catch(() => { /* circuit may be gone */ });
        return;
    }
    if (item.type === 'synced_block') {
        convertToNewSyncedBlock(state, blockEl);
        return;
    }
    const content = blockEl.querySelector('.wiki-block-content');
    if (item.type === '__link_to_page') {
        if (content) openLinkToPagePicker(state, content);
        return;
    }
    if (item.type === '__mention_person') {
        if (content) openMentionPersonPicker(state, content);
        return;
    }
    if (item.type.startsWith('__template_')) {
        const templateId = item.type.slice('__template_'.length);
        state.dotNetRef.invokeMethodAsync('InsertBlockTemplateById', templateId).catch(() => { /* circuit may be gone */ });
        return;
    }
    convertBlockType(state, blockEl, item.type);
}

// Inserts at the current caret position with no backwards deletion - unlike insertWikiLink/
// insertMention (triggered by typing "[[query"/"@query", which must delete that typed text
// first), these pickers are opened directly from the block menu with nothing typed yet to erase.
function insertAtCaret(state, content, node) {
    const range = getCaretRange(content);
    if (!range) return;
    range.deleteContents();
    range.insertNode(node);
    node.after(document.createTextNode(' '));
    placeCaretAtTextOffset(content, content.textContent.length);
    scheduleNotify(state);
}

function openLinkToPagePicker(state, content) {
    state.dotNetRef.invokeMethodAsync('ListAllWikiLinkSuggestions').then(suggestions => {
        if (!suggestions || suggestions.length === 0) return;
        openSuggestionMenu(state, {
            kind: 'slash',
            anchor: content,
            ariaLabel: 'Link to a Sentinel page',
            items: suggestions,
            icon: suggestion => suggestion.icon || '📄',
            label: suggestion => suggestion.title,
            description: () => 'Sentinel page',
            commit: suggestion => replaceWithPageLinkBlock(state, content.closest('.wiki-block'), suggestion)
        });
    }).catch(() => { /* circuit may be gone */ });
}

function pageLinkBlock(page) {
    const pageId = String(page?.id || '').trim();
    const pageTitle = String(page?.title || '').trim() || 'Untitled';
    const pageIcon = String(page?.icon || '📄').trim() || '📄';
    return {
        id: crypto.randomUUID(),
        type: 'page_link',
        indentLevel: 0,
        richText: [{ text: pageTitle, link: `wikilink:${pageId}` }],
        props: { pageId, pageTitle, pageIcon }
    };
}

function replaceWithPageLinkBlock(state, blockEl, page) {
    if (!blockEl || !isUuid(page?.id)) return;
    const block = pageLinkBlock(page);
    block.indentLevel = blockIndent(blockEl);
    const created = createBlockElement(block, state);
    blockEl.replaceWith(created);

    let next = created.nextElementSibling;
    if (!next) {
        const trailingBlock = emptyBlock('paragraph');
        trailingBlock.indentLevel = block.indentLevel;
        next = createBlockElement(trailingBlock, state);
        created.after(next);
    }

    refreshBlockPresentation(state.container);
    focusBlock(next);
    notifyChanged(state);
}

function openMentionPersonPicker(state, content) {
    state.dotNetRef.invokeMethodAsync('SearchMentionSuggestions', '').then(suggestions => {
        const people = (suggestions || []).filter(item => item.kind === 'user');
        if (people.length === 0) return;
        openSuggestionMenu(state, {
            kind: 'mention',
            anchor: content,
            ariaLabel: 'Mention a person',
            items: people,
            icon: () => '@',
            label: suggestion => suggestion.label,
            description: suggestion => suggestion.description,
            commit: suggestion => {
                const anchor = document.createElement('a');
                anchor.href = `${suggestion.kind}mention:${suggestion.value}`;
                anchor.className = 'wiki-mention';
                anchor.textContent = suggestion.label;
                insertAtCaret(state, content, anchor);
            }
        });
    }).catch(() => { /* circuit may be gone */ });
}

function checkSlashTrigger(state, content) {
    const text = content.textContent;
    const match = text.match(/^\/(\w*)$/);
    closeSlashMenu(state);
    if (!match) return;

    const query = match[1].toLowerCase();
    const matches = blockPickerItems(state).filter(item => `${item.label} ${item.type} ${item.keywords || ''}`
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
        commit: item => commitBlockPickerItem(state, content.closest('.wiki-block'), item)
    });
}

// A synced block's content lives server-side (WikiSyncedBlockSource), not in this block's own
// props - every instance sharing the same sourceId re-hydrates from it on the next load, and
// every edit to any instance is written back to it on save (WikiService.GetPageAsync/
// SavePageAsync). Converting a block into one therefore needs a real source id from the server
// first, unlike every other entry in convertBlockType which is purely local and synchronous.
// A second (or third...) instance is created the ordinary way, via "Duplicate" on the block's
// ⋮ menu - duplicateBlock() already clones props verbatim, which carries the sourceId along.
function convertToNewSyncedBlock(state, blockEl) {
    state.dotNetRef.invokeMethodAsync('CreateSyncedBlockSource').then(sourceId => {
        const block = serializeBlock(blockEl);
        block.type = 'synced_block';
        block.richText = [];
        block.props = { sourceId };
        const newEl = createBlockElement(block, state);
        blockEl.replaceWith(newEl);
        refreshBlockPresentation(state.container);
        const focusable = primaryFocusTarget(newEl);
        if (focusable) focusable.focus();
        notifyChanged(state);
    }).catch(() => { /* circuit may be gone */ });
}

function convertBlockType(state, blockEl, newType) {
    const block = serializeBlock(blockEl);
    block.type = newType;
    block.richText = [];
    block.props = {};
    const newEl = createBlockElement(block, state);
    blockEl.replaceWith(newEl);
    refreshBlockPresentation(state.container);
    const focusable = primaryFocusTarget(newEl);
    if (focusable) focusable.focus();
    notifyChanged(state);
    return newEl;
}

// Markdown-style typing shortcuts (e.g. Notion's own "# " -> Heading 1). Longest-prefix
// patterns are listed first (### before ## before #) since a shorter pattern would otherwise
// match before the user finishes typing the longer one. Checked on every input event alongside
// the "/" slash menu, but on the block's *entire* current text - unlike the slash menu, there's
// no separate trigger character, so this only fires once the whole block is exactly the
// shortcut sequence (nothing typed after it yet), the same moment real Notion converts it.
const MARKDOWN_SHORTCUTS = [
    { pattern: /^```$/, type: 'code' },
    { pattern: /^---$/, type: 'divider' },
    { pattern: /^###\s$/, type: 'heading_3' },
    { pattern: /^##\s$/, type: 'heading_2' },
    { pattern: /^#\s$/, type: 'heading_1' },
    { pattern: /^[-*]\s$/, type: 'bulleted_list_item' },
    { pattern: /^\d+[.)]\s$/, type: 'numbered_list_item' },
    { pattern: /^>\s$/, type: 'quote' },
    { pattern: /^\[[xX]\]\s$/, type: 'to_do', checked: true },
    { pattern: /^\[\s?\]\s$/, type: 'to_do', checked: false }
];

function checkMarkdownShortcut(state, content) {
    const blockEl = content.closest('.wiki-block');
    // Typing "```" inside an already-a-code-block should just be three literal backticks, not
    // another conversion.
    if (!blockEl || blockEl.dataset.blockType === 'code') return;

    const text = content.textContent;
    const shortcut = MARKDOWN_SHORTCUTS.find(item => item.pattern.test(text));
    if (!shortcut) return;

    const newEl = convertBlockType(state, blockEl, shortcut.type);
    if (shortcut.type === 'to_do' && shortcut.checked) {
        newEl.dataset.checked = 'true';
        const checkbox = newEl.querySelector('.wiki-todo-checkbox');
        if (checkbox) checkbox.checked = true;
        notifyChanged(state);
    }
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
            event.stopPropagation();
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

// ---- Drag a workspace page into the document ----------------------------

function isUuid(value) {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
        .test(String(value || '').trim());
}

function beginExternalPageDrag(event) {
    const row = event.target instanceof Element
        ? event.target.closest('.wiki-tree-row[data-sentinel-page-id]')
        : null;
    if (!row || !event.dataTransfer) return;

    const page = {
        id: row.dataset.sentinelPageId,
        title: row.dataset.sentinelPageTitle || 'Untitled',
        icon: row.dataset.sentinelPageIcon || '📄'
    };
    if (!isUuid(page.id)) return;

    event.dataTransfer.setData(SENTINEL_PAGE_DRAG_TYPE, JSON.stringify(page));
    event.dataTransfer.setData('text/plain', page.title);
    event.dataTransfer.effectAllowed = 'copyMove';
}

function hasExternalPageDrag(event) {
    return Boolean(event.dataTransfer)
        && [...event.dataTransfer.types].some(type => type.toLowerCase() === SENTINEL_PAGE_DRAG_TYPE);
}

function onExternalPageDragOver(state, event) {
    if (!hasExternalPageDrag(event)) return;
    event.preventDefault();
    event.stopPropagation();
    event.dataTransfer.dropEffect = 'copy';
    state.container.classList.add('wiki-page-drop-active');
}

function onExternalPageDragLeave(state, event) {
    if (event.relatedTarget instanceof Node && state.container.contains(event.relatedTarget)) return;
    state.container.classList.remove('wiki-page-drop-active');
}

function onExternalPageDrop(state, event) {
    if (!hasExternalPageDrag(event)) return;
    event.preventDefault();
    event.stopPropagation();
    state.container.classList.remove('wiki-page-drop-active');

    let page;
    try { page = JSON.parse(event.dataTransfer.getData(SENTINEL_PAGE_DRAG_TYPE)); }
    catch { return; }
    if (!isUuid(page?.id)) return;

    page = {
        id: String(page.id).trim(),
        title: String(page.title || '').trim().slice(0, 500) || 'Untitled',
        icon: String(page.icon || '📄').trim().slice(0, 16) || '📄'
    };
    insertDroppedPageLink(state, event.clientY, page);
}

function insertDroppedPageLink(state, clientY, page) {
    const block = pageLinkBlock(page);
    const created = createBlockElement(block, state);
    const target = topLevelBlockAtY(state.container, clientY);

    if (!target) {
        state.container.appendChild(created);
    } else if (target.dataset.blockType === 'paragraph'
        && (target.querySelector('.wiki-block-content')?.textContent || '').trim().length === 0) {
        block.indentLevel = blockIndent(target);
        created.dataset.indent = String(block.indentLevel);
        applyIndentStyle(created);
        target.replaceWith(created);
    } else {
        block.indentLevel = blockIndent(target);
        created.dataset.indent = String(block.indentLevel);
        applyIndentStyle(created);
        const rect = target.getBoundingClientRect();
        if (clientY > rect.top + (rect.height / 2)) {
            const branch = getBlockBranch(target);
            branch[branch.length - 1].after(created);
        } else {
            target.before(created);
        }
    }

    refreshBlockPresentation(state.container);
    created.scrollIntoView({ block: 'nearest' });
    notifyChanged(state);
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

// ---- Cross-block selection --------------------------------------------------
// Each block is its own separate contentEditable (see createContentEditable), so the browser's
// native Selection/Range can never span a drag across a block boundary. This layer detects that
// crossing and takes over with a synthetic whole-block highlight instead - the same approach
// Notion itself uses. Selecting mid-drag stays native (and cheap) right up until the pointer
// leaves the block it started in.

function topLevelBlockFor(container, node) {
    const el = node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement;
    const block = el?.closest?.('.wiki-block');
    return block && block.parentElement === container ? block : null;
}

function topLevelBlockAtY(container, clientY) {
    const blocks = [...container.querySelectorAll(':scope > .wiki-block')];
    if (blocks.length === 0) return null;
    for (const el of blocks) {
        const rect = el.getBoundingClientRect();
        if (clientY >= rect.top && clientY <= rect.bottom) return el;
    }
    const firstRect = blocks[0].getBoundingClientRect();
    return clientY < firstRect.top ? blocks[0] : blocks[blocks.length - 1];
}

function currentFocusedTopBlock(state) {
    const active = document.activeElement?.closest?.('.wiki-block-content');
    return active ? topLevelBlockFor(state.container, active) : null;
}

function onBlockMouseDown(state, event) {
    if (event.button !== 0) return;
    if (event.target.closest('.wiki-block-handle')) return; // owned by the drag-reorder pointer handlers

    const topBlock = topLevelBlockFor(state.container, event.target);
    if (!topBlock) {
        clearBlockSelection(state);
        return;
    }

    if (event.shiftKey) {
        const anchor = state.blockSelection?.anchorEl || currentFocusedTopBlock(state) || topBlock;
        event.preventDefault(); // native cross-contentEditable selection tends to render as a messy partial highlight
        applyBlockRangeSelection(state, anchor, topBlock);
        return;
    }

    clearBlockSelection(state);
    state.blockDragSelect = { anchorBlockEl: topBlock, active: false };
}

function onBlockMouseMove(state, event) {
    if (!state.blockDragSelect) return;
    const currentBlock = topLevelBlockAtY(state.container, event.clientY);
    if (!currentBlock) return;
    if (currentBlock === state.blockDragSelect.anchorBlockEl && !state.blockDragSelect.active) return;

    state.blockDragSelect.active = true;
    window.getSelection()?.removeAllRanges();
    applyBlockRangeSelection(state, state.blockDragSelect.anchorBlockEl, currentBlock);
}

function applyBlockRangeSelection(state, anchorEl, focusEl) {
    const blocks = [...state.container.querySelectorAll(':scope > .wiki-block')];
    const anchorIndex = blocks.indexOf(anchorEl);
    const focusIndex = blocks.indexOf(focusEl);
    if (anchorIndex === -1 || focusIndex === -1) return;
    const [lo, hi] = anchorIndex <= focusIndex ? [anchorIndex, focusIndex] : [focusIndex, anchorIndex];
    setBlockSelection(state, blocks.slice(lo, hi + 1), anchorEl, focusEl);
}

function extendBlockSelectionByArrow(state, direction) {
    const blocks = [...state.container.querySelectorAll(':scope > .wiki-block')];
    const anchorEl = state.blockSelection?.anchorEl;
    const focusEl = state.blockSelection?.focusEl;
    if (!anchorEl || !focusEl) return;
    const focusIndex = blocks.indexOf(focusEl);
    if (focusIndex === -1) return;
    const nextIndex = Math.min(blocks.length - 1, Math.max(0, focusIndex + direction));
    applyBlockRangeSelection(state, anchorEl, blocks[nextIndex]);
    blocks[nextIndex].scrollIntoView({ block: 'nearest' });
}

function setBlockSelection(state, blockEls, anchorEl, focusEl) {
    clearBlockSelectionClasses(state);
    if (!blockEls || blockEls.length === 0) {
        state.blockSelection = null;
        return;
    }
    for (const el of blockEls) el.classList.add('wiki-block-selected');
    state.blockSelection = {
        blockEls,
        anchorEl: anchorEl || blockEls[0],
        focusEl: focusEl || blockEls[blockEls.length - 1]
    };
}

function clearBlockSelectionClasses(state) {
    state.container.querySelectorAll(':scope > .wiki-block.wiki-block-selected')
        .forEach(el => el.classList.remove('wiki-block-selected'));
}

function clearBlockSelection(state) {
    clearBlockSelectionClasses(state);
    state.blockSelection = null;
}

function blockPlainText(blockEl) {
    return blockEl.querySelector('.wiki-block-content')?.textContent || '';
}

function onBlockSelectionCopy(state, event) {
    if (!state.blockSelection || !event.clipboardData) return;
    event.preventDefault();
    event.clipboardData.setData('text/plain', state.blockSelection.blockEls.map(blockPlainText).join('\n'));
}

function onBlockSelectionCut(state, event) {
    if (!state.blockSelection || !event.clipboardData) return;
    event.preventDefault();
    event.clipboardData.setData('text/plain', state.blockSelection.blockEls.map(blockPlainText).join('\n'));
    deleteBlockSelection(state);
}

// Mirrors deleteBlockAction's own invariant: the editor always shows at least one block.
function deleteBlockSelection(state) {
    const blockEls = state.blockSelection?.blockEls;
    if (!blockEls || blockEls.length === 0) return;

    const allBlocks = [...state.container.querySelectorAll(':scope > .wiki-block')];
    const isFullDocument = blockEls.length === allBlocks.length;
    const focusFallback = isFullDocument ? null : (blockEls[blockEls.length - 1].nextElementSibling || blockEls[0].previousElementSibling);

    let focusTarget = focusFallback;
    if (isFullDocument) {
        focusTarget = createBlockElement(emptyBlock('paragraph'), state);
        blockEls[0].before(focusTarget);
    }

    blockEls.forEach(el => el.remove());
    state.blockSelection = null;

    refreshBlockPresentation(state.container);
    if (focusTarget) focusBlock(focusTarget);
    notifyChanged(state);
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
    if (type === 'tab') {
        props.tabsJson = JSON.stringify(
            [...blockEl.querySelectorAll(':scope > .wiki-block-body .wiki-tab-editor-panel')]
                .map((panel, index) => ({
                    title: panel.querySelector('.wiki-tab-title')?.value.trim() || `Tab ${index + 1}`,
                    richText: richTextFromNode(panel.querySelector('.wiki-tab-content'))
                })));
    }
    if (MEDIA_TYPES.has(type)) {
        props.url = blockEl.dataset.url || '';
        if (blockEl.dataset.fileName) props.fileName = blockEl.dataset.fileName;
        if (blockEl.dataset.notionBlockId) props.notionBlockId = blockEl.dataset.notionBlockId;
        if (blockEl.dataset.mediaKind) props.mediaKind = blockEl.dataset.mediaKind;
    }
    if (type === 'page_link') {
        props.pageId = blockEl.dataset.pageId || '';
        props.pageTitle = blockEl.dataset.pageTitle || 'Untitled';
        props.pageIcon = blockEl.dataset.pageIcon || '📄';
    }
    if (type === 'linked_database' || type === 'inline_database') {
        props.databaseId = blockEl.dataset.databaseId || '';
        props.databaseTitle = blockEl.dataset.databaseTitle || '';
        props.databaseIcon = blockEl.dataset.databaseIcon || '';
        props.databaseViewId = blockEl.dataset.databaseViewId || '';
        props.databaseViewName = blockEl.dataset.databaseViewName || '';
    }

    const contentEl = blockEl.querySelector('.wiki-block-content');
    const richText = type === 'page_link'
        ? [{ text: props.pageTitle, link: `wikilink:${props.pageId}` }]
        : type === 'table'
        ? [{ text: serializeTable(blockEl) }]
        : type === 'columns'
            ? [{
                text: [...blockEl.querySelectorAll(':scope > .wiki-block-body .wiki-column-content')]
                    .map(column => column.textContent.trim())
                    .join(' ||| ')
            }]
        : type === 'tab'
            ? [{
                text: [...blockEl.querySelectorAll(':scope > .wiki-block-body .wiki-tab-editor-panel')]
                    .map((panel, index) => {
                        const title = panel.querySelector('.wiki-tab-title')?.value.trim() || `Tab ${index + 1}`;
                        return `${title}: ${panel.querySelector('.wiki-tab-content')?.textContent.trim() || ''}`;
                    })
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
        else if (tag === 'u') nextMarks.underline = true;
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
        && !!a.strikethrough === !!b.strikethrough && !!a.underline === !!b.underline
        && !!a.code === !!b.code
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
        if (span.underline) html = `<u>${html}</u>`;
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
        { label: 'U', title: 'Underline', tag: 'u', className: 'is-underline' },
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
    appendColorMenuButton(toolbar, state, range);

    document.body.appendChild(toolbar);
    const rect = range.getBoundingClientRect();
    const toolbarRect = toolbar.getBoundingClientRect();
    toolbar.style.left = `${window.scrollX + rect.left + (rect.width - toolbarRect.width) / 2}px`;
    toolbar.style.top = `${window.scrollY + rect.top - toolbarRect.height - 8}px`;
    state.inlineToolbar = toolbar;
}

// One "A" dropdown with two labeled sections (Color / Background) - matching Notion's own
// single-button color menu - rather than two separate buttons each opening their own menu.
function appendColorMenuButton(toolbar, state, selectionRange) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'wiki-color-menu-toggle';
    button.textContent = 'A';
    button.title = 'Color';
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
        for (const kind of ['text', 'background']) {
            const heading = document.createElement('div');
            heading.className = 'wiki-color-menu-heading';
            heading.textContent = kind === 'text' ? 'Color' : 'Background';
            menu.appendChild(heading);
            for (const choice of choices) {
                const option = document.createElement('button');
                option.type = 'button';
                option.setAttribute('role', 'menuitem');
                option.setAttribute('aria-label', `${heading.textContent} ${choice.label.toLowerCase()}`);
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

// A to-do block has both a checkbox <input> and a .wiki-block-content div; querySelector on a
// combined selector list returns whichever matches first in DOM order, which is the checkbox
// (it's appended before the content div in createBlockBody). That silently sent typed text
// nowhere - and, worse, left focus on a checkbox inside the page's <EditForm>, where pressing
// Enter triggers the browser's native implicit form submission instead of splitting the block.
// Every block type that has no .wiki-block-content at all (divider, table, columns, ...) also
// has no <input> to fall back to, so preferring content and only falling back to input when
// content is genuinely absent is correct for every block type, not just to-do.
function primaryFocusTarget(blockEl) {
    return blockEl.querySelector('.wiki-block-content') || blockEl.querySelector('input');
}

function focusBlock(blockEl) {
    const target = primaryFocusTarget(blockEl);
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
