// Boards (.claude/plans/kanban-boards-2026-10.md): drag and drop and keyboard moves for a board's
// cards. Blazor owns the cards' DOM, so nothing here moves a card element: a drag shows a floating
// copy and a drop line, and the drop goes to .NET (MoveCard: card, column, the card it lands
// before), which saves it and re-renders the board. Pointer events: a mouse drags a card from
// anywhere on it; touch drags from the card's grip (touch-action: none), so a swipe elsewhere still
// scrolls. Arrow keys move between cards, [ and ] move a card a column, Alt+arrows reorder it.
// Motion (K7): the dragged copy lifts and tilts with the pointer, the cards below the drop point
// open a gap for it, and when the board re-renders every card that moved glides from where it was
// (FLIP) - the dropped one settling into place. Reduced motion keeps the plain drop line.

const DRAG_THRESHOLD = 5;
const EDGE = 64;
const GLIDE = 'cubic-bezier(.2, .8, .2, 1)';
const calm = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

export function init(root, dotnet) {
    let press = null;   // { card, x, y, pointerId }
    let drag = null;    // { card, ghost, line, offsetX, offsetY, x, y, vx, tilt, gap, target, frame }
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
        drag.vx = event.clientX - drag.x;
        drag.x = event.clientX;
        drag.y = event.clientY;
        paint();
        locate();
    }

    // The lifted copy leans into the way it's moving and straightens as it slows.
    function paint() {
        const still = calm();
        const lean = still ? 0 : Math.max(-9, Math.min(9, drag.vx * 0.8));
        drag.tilt += (lean - drag.tilt) * 0.25;
        drag.ghost.style.transform = `translate(${drag.x - drag.offsetX}px, ${drag.y - drag.offsetY}px) rotate(${(still ? 2 : drag.tilt).toFixed(2)}deg) scale(${still ? 1 : 1.03})`;
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
        drag = { card, ghost, line, offsetX: event.clientX - rect.left, offsetY: event.clientY - rect.top, x: event.clientX, y: event.clientY, vx: 0, tilt: 2, gap: calm() ? 0 : rect.height + 8, target: null, frame: 0 };
        try { card.setPointerCapture(event.pointerId); } catch { /* the pointer may be gone */ }
        drag.frame = requestAnimationFrame(autoScroll);
    }

    // The drop target under the pointer - a column, or with swimlanes a lane's cell - or the nearest
    // one; and the card the drop would land before.
    function locate() {
        const all = columns().filter(c => { const r = c.getBoundingClientRect(); return r.width > 0 && r.height > 0; });
        if (all.length === 0) return;
        const distance = r => Math.hypot(Math.max(r.left - drag.x, 0, drag.x - r.right), Math.max(r.top - drag.y, 0, drag.y - r.bottom));
        let column = all[0];
        let best = Infinity;
        for (const c of all) {
            const d = distance(c.getBoundingClientRect());
            if (d < best) { best = d; column = c; }
        }
        const others = cardsIn(column).filter(c => c !== drag.card);
        const list = column.querySelector('[data-kb-cards]');
        const listRect = list.getBoundingClientRect();
        // Where each card sits in the list's layout - offsetTop ignores the gap's transform, even
        // halfway through its slide, so the drop point doesn't jump about.
        const top = c => listRect.top + c.offsetTop;
        const before = others.find(c => drag.y < top(c) + c.offsetHeight / 2) || null;
        all.forEach(c => c.classList.toggle('kb-drop-target', c === column));
        drag.target = { column, before };
        openGap(others, before);

        let y;
        if (before) y = top(before) - 5;
        else if (others.length) y = top(others[others.length - 1]) + others[others.length - 1].offsetHeight + 3;
        else y = listRect.top + 4;
        const slot = drag.gap > 0 && before;
        drag.line.classList.toggle('is-slot', !!slot);
        Object.assign(drag.line.style, {
            left: listRect.left + 6 + 'px', width: listRect.width - 12 + 'px', top: y + 'px', display: 'block',
            height: slot ? drag.gap - 6 + 'px' : ''
        });
    }

    // The cards from the drop point down slide aside to make room; every other card settles back.
    function openGap(others, before) {
        if (!drag.gap) return;
        const from = before ? others.indexOf(before) : -1;
        const moving = new Set(from < 0 ? [] : others.slice(from));
        for (const c of root.querySelectorAll('[data-kb-shifted]')) {
            if (!moving.has(c)) { c.removeAttribute('data-kb-shifted'); c.style.transform = ''; }
        }
        for (const c of moving) {
            if (c.hasAttribute('data-kb-shifted')) continue;
            c.setAttribute('data-kb-shifted', '');
            c.style.transform = `translateY(${drag.gap}px)`;
        }
    }

    function closeGap(instant) {
        for (const c of root.querySelectorAll('[data-kb-shifted]')) {
            c.removeAttribute('data-kb-shifted');
            if (instant) {
                c.style.transition = 'none';
                c.style.transform = '';
                requestAnimationFrame(() => { c.style.transition = ''; });
            } else {
                c.style.transform = '';
            }
        }
    }

    // Where every card is on screen now, by id - the "first" of FLIP.
    function snapshot() {
        const rects = new Map();
        for (const c of root.querySelectorAll('[data-kb-card]')) rects.set(c.getAttribute('data-kb-card'), c.getBoundingClientRect());
        return rects;
    }

    // Plays the move once Blazor's next render lands: each card that moved glides from its old
    // place to its new one; `landed` (a dropped card) settles in from where it was let go.
    function glideAfterRender(before, landed) {
        if (calm()) return;
        const observer = new MutationObserver(() => {
            observer.disconnect();
            clearTimeout(timer);
            requestAnimationFrame(() => glide(before, landed));
        });
        const timer = setTimeout(() => observer.disconnect(), 3000);
        observer.observe(root, { childList: true, subtree: true });
    }

    function glide(before, landed) {
        for (const card of root.querySelectorAll('[data-kb-card]')) {
            const id = card.getAttribute('data-kb-card');
            const was = before.get(id);
            if (!was) continue;
            const now = card.getBoundingClientRect();
            const dx = was.left - now.left;
            const dy = was.top - now.top;
            if (id === landed) {
                card.animate([
                    { transform: `translate(${dx}px, ${dy}px) rotate(1.5deg) scale(1.03)`, boxShadow: '0 18px 40px rgba(0, 0, 0, .35)' },
                    { transform: 'translate(0, 0) scale(1.012)', offset: 0.8 },
                    { transform: 'none' }
                ], { duration: 340, easing: GLIDE });
            } else if (Math.abs(dx) >= 1 || Math.abs(dy) >= 1) {
                card.animate([{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }], { duration: 240, easing: GLIDE });
            }
        }
    }

    function autoScroll() {
        if (!drag) return;
        const board = scroller();
        if (board) {
            const r = board.getBoundingClientRect();
            if (drag.x < r.left + EDGE) board.scrollLeft -= Math.ceil((r.left + EDGE - drag.x) / 4);
            else if (drag.x > r.right - EDGE) board.scrollLeft += Math.ceil((drag.x - (r.right - EDGE)) / 4);
        }
        // A column scrolls as a whole (K7); with swimlanes the board itself scrolls down.
        const box = drag.target && drag.target.column.scrollHeight > drag.target.column.clientHeight ? drag.target.column : board;
        if (box && box.scrollHeight > box.clientHeight) {
            const r = box.getBoundingClientRect();
            if (drag.y < r.top + EDGE / 2) box.scrollTop -= 8;
            else if (drag.y > r.bottom - EDGE / 2) box.scrollTop += 8;
        }
        drag.vx *= 0.8;
        paint();
        locate();
        drag.frame = requestAnimationFrame(autoScroll);
    }

    function end(commit) {
        const current = drag;
        press = null;
        drag = null;
        if (!current) return;
        cancelAnimationFrame(current.frame);
        // What the eye sees at the drop - the open gap, and the copy where it was let go - is
        // where the glide starts from.
        const before = snapshot();
        before.set(current.card.getAttribute('data-kb-card'), current.ghost.getBoundingClientRect());
        current.ghost.remove();
        current.line.remove();
        current.card.classList.remove('kb-dragging');
        root.classList.remove('kb-is-dragging');
        columns().forEach(c => c.classList.remove('kb-drop-target'));
        // The pointerup that ends a drag also clicks the card; that click must not open it.
        window.addEventListener('click', swallowClick, { capture: true, once: true });
        setTimeout(() => window.removeEventListener('click', swallowClick, { capture: true }), 0);
        if (!commit || !current.target) { closeGap(false); return; }
        const { column, before: anchor } = current.target;
        const sameColumn = column.contains(current.card);
        const next = cardsIn(column).filter(c => c !== current.card)[cardsIn(column).indexOf(current.card)] || null;
        if (sameColumn && anchor === next) { closeGap(false); return; } // dropped where it was
        closeGap(true);
        move(current.card, column, anchor, before);
    }

    function move(card, column, before, from) {
        const id = card.getAttribute('data-kb-card');
        glideAfterRender(from || snapshot(), from ? id : null);
        return dotnet.invokeMethodAsync('MoveCard', id, column.getAttribute('data-kb-column'), before ? before.getAttribute('data-kb-card') : '', column.getAttribute('data-kb-lane'))
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

    // ⇧/⌘/Ctrl-click selects instead of opening (⇧ selects the range from the last one picked).
    function onClickCapture(event) {
        if (!(event.metaKey || event.ctrlKey || event.shiftKey)) return;
        const card = event.target.closest('[data-kb-card]');
        if (!card || event.target.closest('a, button, input, select, textarea')) return;
        event.preventDefault();
        event.stopPropagation();
        dotnet.invokeMethodAsync('ToggleSelect', card.getAttribute('data-kb-card'), event.shiftKey);
    }

    const typing = target => target instanceof Element && target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]');
    // The card drawer (Sentinel's page panel) has the keyboard while it's open.
    const drawerOpen = () => !!document.querySelector('.sentinel-row-page-host');

    function onKeyDown(event) {
        if (drag && event.key === 'Escape') { event.preventDefault(); end(false); return; }
        if (event.metaKey || event.ctrlKey || typing(event.target) || drawerOpen() || !root.isConnected) return;
        const card = event.target.closest && event.target.closest('[data-kb-card]');
        if (event.key === '?') { event.preventDefault(); dotnet.invokeMethodAsync('ToggleHelp'); return; }
        if (event.key === 'Escape') { dotnet.invokeMethodAsync('ClearSelection'); return; }
        if (event.key === 'c' && !event.altKey) {
            const column = (card && card.closest('[data-kb-column]')) || columns()[0];
            if (column) { event.preventDefault(); dotnet.invokeMethodAsync('QuickAddIn', column.getAttribute('data-kb-column')); }
            return;
        }
        if (!card || !root.contains(card) || event.target !== card) return;
        const id = card.getAttribute('data-kb-card');
        if (event.key === 'e') { event.preventDefault(); dotnet.invokeMethodAsync('OpenCardFromKeys', id, true); return; }
        if (event.key === 'x') { event.preventDefault(); dotnet.invokeMethodAsync('ToggleSelect', id, event.shiftKey); return; }
        if (event.key === 'b') { event.preventDefault(); dotnet.invokeMethodAsync('ToggleBlocked', id).then(() => refocus(id)); return; }
        if (/^[0-4]$/.test(event.key) && !event.altKey) {
            event.preventDefault();
            dotnet.invokeMethodAsync('SetPriorityByNumber', id, Number(event.key)).then(() => refocus(id));
            return;
        }
        const column = card.closest('[data-kb-column]');
        // With swimlanes, sideways moves stay in the card's own lane.
        const lane = column.getAttribute('data-kb-lane');
        const all = columns().filter(c => c.getAttribute('data-kb-lane') === lane);
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
    root.addEventListener('click', onClickCapture, true);
    window.addEventListener('pointermove', onPointerMove, { passive: false });
    window.addEventListener('pointerup', onPointerUp);
    window.addEventListener('pointercancel', onPointerCancel);
    document.addEventListener('keydown', onKeyDown);

    return {
        // Live updates (K6) wait for a drag to finish before the board re-renders.
        isDragging() { return drag !== null; },
        // A live update is about to re-render the board: its cards glide to where they land.
        prepareGlide() { glideAfterRender(snapshot(), null); },
        // A card reached a Done column (K7): a check bursts over it once it has settled. It lives
        // on <body>, outside the cards Blazor renders.
        celebrate(id) {
            if (calm()) return;
            const card = root.querySelector(`[data-kb-card="${CSS.escape(id)}"]`);
            if (!card) return;
            setTimeout(() => {
                const r = card.getBoundingClientRect();
                if (r.width === 0) return;
                const burst = document.createElement('div');
                burst.className = 'kb-burst';
                burst.setAttribute('aria-hidden', 'true');
                burst.style.left = r.left + r.width / 2 + 'px';
                burst.style.top = r.top + r.height / 2 + 'px';
                for (let i = 0; i < 12; i++) {
                    const spark = document.createElement('span');
                    spark.style.setProperty('--a', i * 30 + 'deg');
                    burst.appendChild(spark);
                }
                const check = document.createElement('i');
                check.className = 'bi bi-check-lg';
                burst.appendChild(check);
                document.body.appendChild(burst);
                setTimeout(() => burst.remove(), 1100);
            }, 260);
        },
        dispose() {
            end(false);
            document.querySelectorAll('.kb-burst').forEach(burst => burst.remove());
            root.removeEventListener('pointerdown', onPointerDown);
            root.removeEventListener('click', onClickCapture, true);
            window.removeEventListener('pointermove', onPointerMove);
            window.removeEventListener('pointerup', onPointerUp);
            window.removeEventListener('pointercancel', onPointerCancel);
            document.removeEventListener('keydown', onKeyDown);
        }
    };
}
