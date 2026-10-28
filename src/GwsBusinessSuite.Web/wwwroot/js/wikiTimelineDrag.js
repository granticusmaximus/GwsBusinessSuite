// Drag-to-reschedule for Timeline view bars. Dragging a bar moves it: the start date shifts, and
// so does the end when the view has an end property (TimelineEndPropertyId). With an end
// property, each bar also has an edge handle at either end: dragging one changes just that date,
// never past the other. A row with a start but no end grows an end from its right handle.
//
// A single delegated pointerdown listener on the timeline container (rather than one per bar)
// means this never needs re-binding as Blazor re-renders rows in and out - initialize() is
// idempotent per container via the timelineDragBound marker, called fresh on every
// OnAfterRenderAsync the same way wiki-block-editor.js's initialize is.
let activeDrag = null;

export function initialize(container, dotNetRef) {
    if (!container || container.dataset.timelineDragBound === 'true') return;
    container.dataset.timelineDragBound = 'true';
    container.addEventListener('pointerdown', event => onPointerDown(container, dotNetRef, event));
}

function onPointerDown(container, dotNetRef, event) {
    if (activeDrag || container.dataset.canEdit !== 'true') return;
    const bar = event.target.closest('.sentinel-db-timeline-bar');
    if (!bar) return;
    const track = bar.closest('.sentinel-db-timeline-track');
    const rowId = track?.dataset.rowId;
    const originalDate = track?.dataset.date;
    const scheduleStart = container.dataset.scheduleStart;
    const scheduleEnd = container.dataset.scheduleEnd;
    if (!track || !rowId || !originalDate || !scheduleStart || !scheduleEnd) return;

    const hasEnd = container.dataset.hasEnd === 'true';
    const handle = hasEnd ? event.target.closest('.sentinel-db-timeline-handle') : null;
    const mode = handle?.classList.contains('is-start') ? 'start' : handle ? 'end' : 'move';
    const totalDays = Math.max(1, dayDiff(scheduleStart, scheduleEnd) + 1);
    const trackWidth = track.getBoundingClientRect().width || 1;

    event.preventDefault();
    bar.setPointerCapture(event.pointerId);
    bar.classList.add('is-dragging');

    activeDrag = {
        dotNetRef,
        bar,
        track,
        mode,
        hasEnd,
        rowId,
        originalDate,
        originalEnd: track.dataset.end || '',
        offsetDays: dayDiff(scheduleStart, originalDate),
        totalDays,
        originalStyle: track.getAttribute('style') || '',
        pointerId: event.pointerId,
        dayWidth: trackWidth / totalDays,
        startX: event.clientX,
        deltaDays: 0
    };

    bar.addEventListener('pointermove', onPointerMove);
    bar.addEventListener('pointerup', onPointerUp);
    bar.addEventListener('pointercancel', onPointerCancel);
}

function onPointerMove(event) {
    if (!activeDrag || event.pointerId !== activeDrag.pointerId) return;
    const drag = activeDrag;
    drag.deltaDays = Math.round((event.clientX - drag.startX) / drag.dayWidth);
    if (drag.mode === 'move') {
        drag.bar.style.transform = `translateX(${drag.deltaDays * drag.dayWidth}px)`;
        return;
    }

    // Resizing redraws the bar from the dates it would get, the same way TimelineBarStyle does.
    const { start, end } = rangeAfter(drag);
    const left = (drag.offsetDays + dayDiff(drag.originalDate, start)) * 100 / drag.totalDays;
    const width = Math.max(3, (dayDiff(start, end) + 1) * 100 / drag.totalDays);
    drag.track.style.setProperty('--timeline-left', `${left}%`);
    drag.track.style.setProperty('--timeline-width', `${width}%`);
}

function onPointerUp(event) {
    if (!activeDrag || event.pointerId !== activeDrag.pointerId) return;
    const drag = activeDrag;
    endDrag();
    if (drag.deltaDays === 0) return;
    swallowNextClick(drag.bar);
    const { start, end } = rangeAfter(drag);
    const keepsNoEnd = drag.mode === 'move' && !drag.originalEnd;
    drag.dotNetRef.invokeMethodAsync('UpdateTimelineRowDatesAsync', drag.rowId, start, drag.hasEnd && !keepsNoEnd ? end : null)
        .catch(() => { /* circuit may be gone, or the save failed server-side */ });
}

// The start and end a drag would save. A row with no end is a one-day range for resizing.
function rangeAfter(drag) {
    const end = drag.originalEnd || drag.originalDate;
    if (drag.mode === 'move') {
        return { start: addDays(drag.originalDate, drag.deltaDays), end: addDays(end, drag.deltaDays) };
    }
    if (drag.mode === 'start') {
        const start = addDays(drag.originalDate, Math.min(drag.deltaDays, dayDiff(drag.originalDate, end)));
        return { start, end };
    }
    return { start: drag.originalDate, end: addDays(end, Math.max(drag.deltaDays, -dayDiff(drag.originalDate, end))) };
}

// Letting go of a drag also clicks the bar, which would open the row on top of the timeline.
// Blazor listens for clicks on the document as they bubble, so stopping this one at the bar
// keeps it from Blazor; a plain click (no drag) still opens the row.
function swallowNextClick(bar) {
    const swallow = event => { event.stopPropagation(); event.preventDefault(); };
    bar.addEventListener('click', swallow, { capture: true, once: true });
    setTimeout(() => bar.removeEventListener('click', swallow, { capture: true }), 400);
}

function onPointerCancel() {
    endDrag();
}

function endDrag() {
    if (!activeDrag) return;
    activeDrag.bar.classList.remove('is-dragging');
    activeDrag.bar.style.transform = '';
    // The re-render after the save redraws the bar from the saved dates.
    activeDrag.track.setAttribute('style', activeDrag.originalStyle);
    activeDrag.bar.removeEventListener('pointermove', onPointerMove);
    activeDrag.bar.removeEventListener('pointerup', onPointerUp);
    activeDrag.bar.removeEventListener('pointercancel', onPointerCancel);
    activeDrag = null;
}

function dayDiff(isoA, isoB) {
    return Math.round((new Date(`${isoB}T00:00:00Z`) - new Date(`${isoA}T00:00:00Z`)) / 86400000);
}

function addDays(iso, days) {
    const date = new Date(`${iso}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + days);
    return date.toISOString().slice(0, 10);
}
