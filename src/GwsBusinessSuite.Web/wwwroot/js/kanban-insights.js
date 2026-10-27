// Boards' flow charts (KanbanInsights.razor, K5): one tooltip for every chart. A mark's hit area
// carries data-kbi-tip-text - a title line, then "value<TAB>label<TAB>colour" rows - and, on the
// cumulative-flow chart, data-kbi-x for the crosshair to snap to. Labels come from people's data,
// so every piece of text goes in with textContent.

export function init(root) {
    const tip = root.querySelector('[data-kbi-tip]');

    function show(target, clientX, clientY) {
        const text = target.getAttribute('data-kbi-tip-text');
        if (!text || !tip) return;
        const [title, ...rows] = text.split('\n');
        tip.replaceChildren();
        const head = document.createElement('div');
        head.className = 'kbi-tip-title';
        head.textContent = title;
        tip.appendChild(head);
        for (const row of rows) {
            const [value, label, color] = row.split('\t');
            const line = document.createElement('div');
            line.className = 'kbi-tip-row';
            if (color) {
                const key = document.createElement('span');
                key.className = 'kbi-tip-key';
                key.style.setProperty('--tone', color);
                line.appendChild(key);
            }
            const strong = document.createElement('strong');
            strong.textContent = value;
            line.appendChild(strong);
            if (label) {
                const name = document.createElement('span');
                name.textContent = label;
                line.appendChild(name);
            }
            tip.appendChild(line);
        }
        tip.hidden = false;
        const box = root.getBoundingClientRect();
        const width = tip.offsetWidth;
        const height = tip.offsetHeight;
        let left = clientX - box.left + 14;
        if (left + width > box.width) left = clientX - box.left - width - 14;
        tip.style.left = Math.max(0, left) + 'px';
        tip.style.top = Math.max(0, clientY - box.top - height - 12) + 'px';

        const svg = target.closest('svg');
        const crosshair = svg && svg.querySelector('[data-kbi-crosshair]');
        const x = target.getAttribute('data-kbi-x');
        if (crosshair && x) {
            crosshair.setAttribute('x1', x);
            crosshair.setAttribute('x2', x);
            crosshair.setAttribute('visibility', 'visible');
        }
        target.classList.add('is-hover');
    }

    function hide(target) {
        if (tip) tip.hidden = true;
        root.querySelectorAll('[data-kbi-crosshair]').forEach(line => line.setAttribute('visibility', 'hidden'));
        if (target) target.classList.remove('is-hover');
    }

    let current = null;
    function onMove(event) {
        const target = event.target.closest && event.target.closest('[data-kbi-tip-text]');
        if (target !== current && current) hide(current);
        current = target;
        if (target) show(target, event.clientX, event.clientY);
    }
    function onLeave() { if (current) hide(current); current = null; }
    function onFocus(event) {
        const target = event.target.closest && event.target.closest('[data-kbi-tip-text]');
        if (!target) return;
        const box = target.getBoundingClientRect();
        show(target, box.left + box.width / 2, box.top);
    }
    function onBlur(event) { hide(event.target); }
    function onKey(event) {
        if ((event.key === 'Enter' || event.key === ' ') && event.target.matches && event.target.matches('[data-kbi-tip-text][role="button"]')) {
            event.preventDefault();
            event.target.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        }
    }

    root.addEventListener('pointermove', onMove);
    root.addEventListener('pointerleave', onLeave);
    root.addEventListener('focusin', onFocus);
    root.addEventListener('focusout', onBlur);
    root.addEventListener('keydown', onKey);

    return {
        dispose() {
            root.removeEventListener('pointermove', onMove);
            root.removeEventListener('pointerleave', onLeave);
            root.removeEventListener('focusin', onFocus);
            root.removeEventListener('focusout', onBlur);
            root.removeEventListener('keydown', onKey);
        }
    };
}
