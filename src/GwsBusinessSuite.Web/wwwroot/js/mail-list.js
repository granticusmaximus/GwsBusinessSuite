// Mail's message list (06-11-26): swipes, the right-click menu's placement and Gmail's keyboard
// shortcuts. The page (Mail.razor) decides what each one does.
//
// Swipes: a two-finger trackpad swipe (a horizontal wheel) or a touch drag on a row moves it with
// the fingers over a coloured action. Right marks it read or unread, left deletes it, as in Apple
// Mail. Past the threshold it acts when the gesture ends; otherwise it springs back. A swipe that
// starts more up-and-down than sideways is left to scroll the list, and while a row is being
// swiped, the browser's back/forward swipe doesn't fire.

const settleAfterMs = 180;
const reduceMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const handles = new Map();
let nextHandle = 1;

function threshold(item) {
    return Math.min(140, item.offsetWidth * 0.3);
}

function place(gesture) {
    const { item, row } = gesture;
    const width = item.offsetWidth;
    gesture.dx = Math.max(-width, Math.min(width, gesture.dx));
    row.style.transition = 'none';
    row.style.transform = `translateX(${gesture.dx}px)`;
    item.dataset.swipe = gesture.dx > 0 ? 'read' : gesture.dx < 0 ? 'delete' : '';
    item.classList.toggle('mail-item--armed', Math.abs(gesture.dx) >= threshold(item));
    item.classList.add('mail-item--swiping');
}

function reset(item, row) {
    row.style.transition = reduceMotion() ? 'none' : 'transform 160ms ease-out';
    row.style.transform = '';
    const done = () => {
        row.style.transition = '';
        item.classList.remove('mail-item--swiping', 'mail-item--armed');
        delete item.dataset.swipe;
    };
    if (reduceMotion()) done(); else setTimeout(done, 170);
}

async function finish(state, gesture) {
    if (state.active === gesture) state.active = null;
    const { item, row } = gesture;
    if (Math.abs(gesture.dx) < threshold(item)) {
        reset(item, row);
        return;
    }
    const side = gesture.dx > 0 ? 'read' : 'delete';
    state.lastSwipeAt = Date.now();
    if (side === 'delete') {
        row.style.transition = reduceMotion() ? 'none' : 'transform 160ms ease-in';
        row.style.transform = `translateX(${-item.offsetWidth}px)`;
    } else {
        reset(item, row);
    }
    let removed = false;
    try {
        removed = await state.dotnet.invokeMethodAsync('OnSwipe', item.dataset.id, side);
    } catch {
        removed = false;
    }
    // Deleted rows leave the list on the page's next render; one that wasn't (a refusal, or the
    // confirmation for deleting from Trash) comes back.
    if (side === 'delete' && !removed && item.isConnected) reset(item, row);
}

function onWheel(state, e) {
    const item = e.target.closest('.mail-item');
    const scale = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? item?.offsetWidth ?? 400 : 1;
    const dx = e.deltaX * scale;
    const dy = e.deltaY * scale;
    let gesture = state.active;
    if (!gesture || gesture.item !== item) {
        if (!item || Math.abs(dx) <= Math.abs(dy) * 1.2 || Math.abs(dx) < 2) return;
        if (gesture) finish(state, gesture);
        const row = item.querySelector('.mail-item-row');
        if (!row) return;
        gesture = state.active = { item, row, dx: 0, timer: 0 };
    }
    e.preventDefault();
    // With natural scrolling, fingers moving right scroll left (deltaX < 0): the row follows the fingers.
    gesture.dx -= dx;
    place(gesture);
    clearTimeout(gesture.timer);
    gesture.timer = setTimeout(() => finish(state, gesture), settleAfterMs);
}

function onPointerDown(state, e) {
    if (e.pointerType === 'mouse' || !e.isPrimary) return;
    const item = e.target.closest('.mail-item');
    const row = item?.querySelector('.mail-item-row');
    if (!row) return;
    state.touch = { item, row, x: e.clientX, y: e.clientY, dx: 0, locked: false, id: e.pointerId };
}

function onPointerMove(state, e) {
    const touch = state.touch;
    if (!touch || touch.id !== e.pointerId) return;
    const dx = e.clientX - touch.x;
    const dy = e.clientY - touch.y;
    if (!touch.locked) {
        if (Math.abs(dy) > 10 && Math.abs(dy) > Math.abs(dx)) {
            state.touch = null;
            return;
        }
        if (Math.abs(dx) < 10) return;
        touch.locked = true;
        try { touch.row.setPointerCapture(e.pointerId); } catch { /* the row went */ }
    }
    touch.dx = dx;
    place(touch);
}

function onPointerUp(state, e) {
    const touch = state.touch;
    if (!touch || touch.id !== e.pointerId) return;
    state.touch = null;
    if (touch.locked) finish(state, touch);
}

// A tap that ended a swipe isn't also a click on the row.
function onClick(state, e) {
    if (Date.now() - state.lastSwipeAt < 400 && e.target.closest('.mail-item')) {
        e.preventDefault();
        e.stopPropagation();
    }
}

export function attach(list, dotnet) {
    const state = { dotnet, active: null, touch: null, lastSwipeAt: 0 };
    const listeners = [
        ['wheel', e => onWheel(state, e), { passive: false }],
        ['pointerdown', e => onPointerDown(state, e)],
        ['pointermove', e => onPointerMove(state, e)],
        ['pointerup', e => onPointerUp(state, e)],
        ['pointercancel', e => onPointerUp(state, e)],
        ['click', e => onClick(state, e), true]
    ];
    for (const [name, handler, options] of listeners) list.addEventListener(name, handler, options);
    const handle = nextHandle++;
    handles.set(handle, () => {
        for (const [name, handler, options] of listeners) list.removeEventListener(name, handler, options);
    });
    return handle;
}

export function detach(handle) {
    handles.get(handle)?.();
    handles.delete(handle);
}

// A menu: at the pointer, kept on screen, or under its anchor when there's no pointer position (a
// button's menu, or a right-click menu opened from the keyboard; rowId picks the row in the list).
// Focus goes to its first item; arrows move, Home/End jump.
export function showMenu(menu, x, y, anchor, rowId) {
    if (!menu) return;
    if (rowId && anchor) anchor = [...anchor.querySelectorAll('.mail-item')].find(item => item.dataset.id === rowId) ?? anchor;
    if ((!x && !y) && anchor) {
        const box = anchor.getBoundingClientRect();
        x = box.left + (rowId ? 24 : 0);
        y = box.bottom + (rowId ? -4 : 4);
    }
    const width = menu.offsetWidth;
    const height = menu.offsetHeight;
    const left = Math.max(8, Math.min(x, window.innerWidth - width - 8));
    const top = y + height + 8 > window.innerHeight ? Math.max(8, y - height) : y;
    menu.style.left = `${left}px`;
    menu.style.top = `${top}px`;
    menu.classList.add('is-placed');
    menu.querySelector('[role="menuitem"]:not([disabled])')?.focus();
    if (!menu.dataset.keys) {
        menu.dataset.keys = '1';
        menu.addEventListener('keydown', e => {
            const items = [...menu.querySelectorAll('[role="menuitem"]:not([disabled])')];
            const at = items.indexOf(document.activeElement);
            const go = index => { e.preventDefault(); items[(index + items.length) % items.length]?.focus(); };
            if (e.key === 'ArrowDown') go(at + 1);
            else if (e.key === 'ArrowUp') go(at - 1);
            else if (e.key === 'Home') go(0);
            else if (e.key === 'End') go(items.length - 1);
            else if (e.key === 'Tab') e.preventDefault();
        });
    }
}

export function focusRow(list, id) {
    const button = [...list.querySelectorAll('.mail-item')].find(item => item.dataset.id === id)?.querySelector('.mail-item-open');
    if (!button) return;
    button.focus({ preventScroll: true });
    button.scrollIntoView({ block: 'nearest' });
}

function focusSearch() {
    document.querySelector('.mail-search input')?.focus();
}

// Gmail's shortcuts, while nothing is being typed. The page gets the key: j k o u x s e # I U r a f c ? Escape, Enter.
const shortcutKeys = new Set(['j', 'k', 'o', 'u', 'x', 's', 'e', '#', 'I', 'U', 'r', 'a', 'f', 'c', '?', 'Escape', 'Enter', '/']);

export function listenForShortcuts(dotnet) {
    const handler = e => {
        if (e.metaKey || e.ctrlKey || e.altKey || e.isComposing) return;
        const target = e.target;
        if (target.closest?.('input, textarea, select, [contenteditable="true"], .mail-compose')) {
            if (e.key === 'Escape' && target.closest('.mail-search')) target.blur();
            return;
        }
        // Enter on a button or link is that control's own click.
        if (e.key === 'Enter' && target.closest('button, a')) return;
        if (target.closest('.mail-menu')) return;
        const key = e.key.length === 1 && !e.shiftKey ? e.key.toLowerCase() : e.key;
        if (!shortcutKeys.has(key)) return;
        e.preventDefault();
        if (key === '/') {
            focusSearch();
            return;
        }
        dotnet.invokeMethodAsync('OnShortcut', key);
    };
    document.addEventListener('keydown', handler);
    const handle = nextHandle++;
    handles.set(handle, () => document.removeEventListener('keydown', handler));
    return handle;
}
