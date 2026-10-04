// Turns every Bootstrap .table into a stacked card list on phones (see gws-responsive.css,
// "Tables → stacked cards"). Each body cell gets data-label = its column heading so the card
// shows "Status: Active" instead of a bare value. Runs on load and whenever Blazor re-renders
// (MutationObserver), so tables that appear later are handled too. Opt a table out with
// .gws-table-keep (e.g. a dense grid that genuinely needs columns).
(function () {
  function label(table) {
    if (table.classList.contains('gws-table-keep')) return;
    var head = table.tHead && table.tHead.rows[table.tHead.rows.length - 1];
    if (!head) return;
    var names = Array.prototype.map.call(head.cells, function (cell) {
      return (cell.innerText || cell.textContent || '').trim();
    });
    Array.prototype.forEach.call(table.tBodies, function (body) {
      Array.prototype.forEach.call(body.rows, function (row) {
        var column = 0;
        Array.prototype.forEach.call(row.cells, function (cell) {
          if (!cell.hasAttribute('data-label')) cell.setAttribute('data-label', names[column] || '');
          column += cell.colSpan || 1;
        });
      });
    });
    table.classList.add('gws-table-stack');
    // Keep header-row controls (select-all checkboxes etc.) reachable once the headings hide.
    table.classList.toggle('gws-head-controls', !!table.tHead.querySelector('input, button, select'));
  }

  function scan(root) {
    if (!root || !root.querySelectorAll) return;
    if (root.matches && root.matches('table.table')) label(root);
    Array.prototype.forEach.call(root.querySelectorAll('table.table'), label);
  }

  var pending = false;
  function schedule() {
    if (pending) return;
    pending = true;
    window.requestAnimationFrame(function () {
      pending = false;
      scan(document.body);
    });
  }

  function start() {
    scan(document.body);
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
