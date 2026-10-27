// Boards (.claude/plans/kanban-boards-2026-10.md): drag and drop and keyboard moves for a board's
// cards. Blazor owns the cards' DOM, so nothing here moves a card element: a drag shows a floating
// copy and a drop line, and the drop goes to .NET (MoveCard: card, column, the card it lands
// before), which saves it and re-renders the board. Pointer events: a mouse drags a card from
// anywhere on it; touch drags from the card's grip (touch-action: none), so a swipe elsewhere still
// scrolls. Arrow keys move between cards, [ and ] move a card a column, Alt+arrows reorder it.

const DRAG_THRESHOLD = 5;
const EDGE = 64;

export function init(root, dotnet) {
    let press = null;   // { card, x, y, pointerId }
    let drag = null;    // { card, ghost, line, offsetX, offsetY, x, y, target, frame }
    const swallowClick = event => { event.stopPropagation(); event.preventDefault(); };

    const columns = () => [...root.querySelectorAll('[data-kb-column]')];
    const cardsIn = column => [...column.querySelectorAll('[data-kb-card]')];
    const scroller = () => root.querySelector('[data-kb-scroller]');

    function onPointerDown(event) {
        if (event.button !== 0 || drag) return;
        const card = event.target.closest('[data-kb-card]');
        if (!card || !root.contains(card) || event.target.closest('a, button, input, select, textarea')) return;
        if (event.pointerType === 'touch' && !event.target.closest('[data-kb-grip]')) return;
        press = { card, x: event.clientX, y: event.clientY, pointerId: event.pointerId };
    }

    function onPointerMove(event) {
        if (press && !drag && event.pointerId === press.pointerId
            && Math.hypot(event.clientX - press.x, event.clientY - press.y) > DRAG_THRESHOLD) {
            begin(event);
        }
        if (!drag) return;
        event.preventDefault();
        drag.x = event.clientX;
        drag.y = event.clientY;
        drag.ghost.style.transform = `translate(${drag.x - drag.offsetX}px, ${drag.y - drag.offsetY}px) rotate(2deg)`;
        locate();
    }

    function begin(event) {
        const card = press.card;
        const rect = card.getBoundingClientRect();
        const ghost = card.cloneNode(true);
        ghost.removeAttribute('data-kb-card');
        ghost.removeAttribute('tabindex');
        ghost.classList.add('kb-ghost');
        ghost.style.width = rect.width + 'px';
        ghost.setAttribute('aria-hidden', 'true');
        document.body.appendChild(ghost);
        const line = document.createElement('div');
        line.className = 'kb-drop-line';
        line.setAttribute('aria-hidden', 'true');
        document.body.appendChild(line);
        card.classList.add('kb-dragging');
        root.classList.add('kb-is-dragging');
        drag = { card, ghost, line, offsetX: event.clientX - rect.left, offsetY: event.clientY - rect.top, x: event.clientX, y: event.clientY, target: null, frame: 0 };
        try { card.setPointerCapture(event.pointerId); } catch { /* the pointer may be gone */ }
        drag.frame = requestAnimationFrame(autoScroll);
    }

    // The column under the pointer (or the nearest), and the card the drop would land before.
    function locate() {
        const all = columns();
        if (all.length === 0) return;
        let column = all.find(c => { const r = c.getBoundingClientRect(); return drag.x >= r.left && drag.x <= r.right; });
        if (!column) {
            column = all.reduce((best, c) => {
                const r = c.getBoundingClientRect();
                const d = Math.min(Math.abs(drag.x - r.left), Math.abs(drag.x - r.right));
                return !best || d < best.d ? { c, d } : best;
            }, null).c;
        }
        const others = cardsIn(column).filter(c => c !== drag.card);
        const before = others.find(c => { const r = c.getBoundingClientRect(); return drag.y < r.top + r.height / 2; }) || null;
        all.forEach(c => c.classList.toggle('kb-drop-target', c === column));
        drag.target = { column, before };

        const list = column.querySelector('[data-kb-cards]');
        const listRect = list.getBoundingClientRect();
        let y;
        if (before) y = before.getBoundingClientRect().top - 5;
        else if (others.length) y = others[others.length - 1].getBoundingClientRect().bottom + 3;
        else y = listRect.top + 4;
        Object.assign(drag.line.style, { left: listRect.left + 6 + 'px', width: listRect.width - 12 + 'px', top: y + 'px', display: 'block' });
    }

    function autoScroll() {
        if (!drag) return;
        const board = scroller();
        if (board) {
            const r = board.getBoundingClientRect();
            if (drag.x < r.left + EDGE) board.scrollLeft -= Math.ceil((r.left + EDGE - drag.x) / 4);
            else if (drag.x > r.right - EDGE) board.scrollLeft += Math.ceil((drag.x - (r.right - EDGE)) / 4);
        }
        const list = drag.target && drag.target.column.querySelector('[data-kb-cards]');
        if (list) {
            const r = list.getBoundingClientRect();
            if (drag.y < r.top + EDGE / 2) list.scrollTop -= 8;
            else if (drag.y > r.bottom - EDGE / 2) list.scrollTop += 8;
        }
        locate();
        drag.frame = requestAnimationFrame(autoScroll);
    }

    function end(commit) {
        const current = drag;
        press = null;
        drag = null;
        if (!current) return;
        cancelAnimationFrame(current.frame);
        current.ghost.remove();
        current.line.remove();
        current.card.classList.remove('kb-dragging');
        root.classList.remove('kb-is-dragging');
        columns().forEach(c => c.classList.remove('kb-drop-target'));
        // The pointerup that ends a drag also clicks the card; that click must not open it.
        window.addEventListener('click', swallowClick, { capture: true, once: true });
        setTimeout(() => window.removeEventListener('click', swallowClick, { capture: true }), 0);
        if (!commit || !current.target) return;
        const { column, before } = current.target;
        const sameColumn = column.contains(current.card);
        const next = cardsIn(column).filter(c => c !== current.card)[cardsIn(column).indexOf(current.card)] || null;
        if (sameColumn && before === next) return; // dropped where it was
        move(current.card, column, before);
    }

    function move(card, column, before) {
        const id = card.getAttribute('data-kb-card');
        return dotnet.invokeMethodAsync('MoveCard', id, column.getAttribute('data-kb-column'), before ? before.getAttribute('data-kb-card') : '')
            .then(() => refocus(id))
            .catch(() => { /* .NET reports a failed move itself */ });
    }

    function refocus(id) {
        const card = root.querySelector(`[data-kb-card="${CSS.escape(id)}"]`);
        if (card) card.focus({ preventScroll: false });
    }

    function onPointerUp(event) {
        if (drag && event.pointerId !== undefined) end(true);
        press = null;
    }

    function onPointerCancel() { end(false); }

    function onKeyDown(event) {
        if (drag && event.key === 'Escape') { event.preventDefault(); end(false); return; }
        const card = event.target.closest && event.target.closest('[data-kb-card]');
        if (!card || !root.contains(card) || event.target !== card) return;
        const column = card.closest('[data-kb-column]');
        const all = columns();
        const here = cardsIn(column);
        const index = here.indexOf(card);
        const at = all.indexOf(column);
        const nearest = (list, i) => list[Math.min(i, list.length - 1)];
        let handled = true;
        if (event.key === 'Enter' || event.key === ' ') {
            card.click();
        } else if (event.altKey && event.key === 'ArrowUp') {
            if (index > 0) move(card, column, here[index - 1]);
        } else if (event.altKey && event.key === 'ArrowDown') {
            if (index < here.length - 1) move(card, column, here[index + 2] || null);
        } else if (event.key === '[' || event.key === ']') {
            const target = all[at + (event.key === '[' ? -1 : 1)];
            if (target) move(card, target, cardsIn(target)[0] || null);
        } else if (event.key === 'ArrowDown' || event.key === 'j') {
            (here[index + 1] || card).focus();
        } else if (event.key === 'ArrowUp' || event.key === 'k') {
            (here[index - 1] || card).focus();
        } else if (event.key === 'ArrowLeft' || event.key === 'h' || event.key === 'ArrowRight' || event.key === 'l') {
            const step = event.key === 'ArrowLeft' || event.key === 'h' ? -1 : 1;
            for (let i = at + step; i >= 0 && i < all.length; i += step) {
                const cards = cardsIn(all[i]);
                if (cards.length) { nearest(cards, index).focus(); break; }
            }
        } else {
            handled = false;
        }
        if (handled) event.preventDefault();
    }

    root.addEventListener('pointerdown', onPointerDown);
    window.addEventListener('pointermove', onPointerMove, { passive: false });
    window.addEventListener('pointerup', onPointerUp);
    window.addEventListener('pointercancel', onPointerCancel);
    document.addEventListener('keydown', onKeyDown);

    return {
        dispose() {
            end(false);
            root.removeEventListener('pointerdown', onPointerDown);
            window.removeEventListener('pointermove', onPointerMove);
            window.removeEventListener('pointerup', onPointerUp);
            window.removeEventListener('pointercancel', onPointerCancel);
            document.removeEventListener('keydown', onKeyDown);
        }
    };
}
