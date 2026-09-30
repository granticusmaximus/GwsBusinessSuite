// Page selection belongs to the Appearance toolbar. Keep links and forms from leaving
// this read-only preview or performing a live action while retaining widget interactions.
document.addEventListener('click', function (event) {
    if (event.target.closest('a')) event.preventDefault();
}, true);
document.addEventListener('submit', function (event) { event.preventDefault(); }, true);
