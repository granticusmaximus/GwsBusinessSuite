// Drag-to-reschedule for Timeline view bars (Phase 5.1). Each row currently carries a single
// Date property (see WikiDatabaseTimelineItem/BuildTimelineSchedule), not a start+end range, so
// dragging a bar moves the whole marker to a new day rather than stretching its width - the
// bar's rendered width is already just a minimum-visibility affordance (TimelineBarStyle), not
// derived from any end date.
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

    const totalDays = Math.max(1, dayDiff(scheduleStart, scheduleEnd) + 1);
    const trackWidth = track.getBoundingClientRect().width || 1;

    event.preventDefault();
    bar.setPointerCapture(event.pointerId);
    bar.classList.add('is-dragging');

    activeDrag = {
        dotNetRef,
        bar,
        rowId,
        originalDate,
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
    activeDrag.deltaDays = Math.round((event.clientX - activeDrag.startX) / activeDrag.dayWidth);
    activeDrag.bar.style.transform = `translateX(${activeDrag.deltaDays * activeDrag.dayWidth}px)`;
}

function onPointerUp(event) {
    if (!activeDrag || event.pointerId !== activeDrag.pointerId) return;
    const drag = activeDrag;
    endDrag();
    if (drag.deltaDays !== 0) {
        const newDate = addDays(drag.originalDate, drag.deltaDays);
        drag.dotNetRef.invokeMethodAsync('UpdateTimelineRowDateAsync', drag.rowId, newDate)
            .catch(() => { /* circuit may be gone, or the save failed server-side */ });
    }
}

function onPointerCancel() {
    endDrag();
}

function endDrag() {
    if (!activeDrag) return;
    activeDrag.bar.classList.remove('is-dragging');
    activeDrag.bar.style.transform = '';
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
