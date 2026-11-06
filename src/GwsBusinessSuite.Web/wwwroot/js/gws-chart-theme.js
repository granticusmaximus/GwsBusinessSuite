// One theme for every Chart.js chart in the admin (T5, 06-11-26). Colours come from the same CSS
// tokens as the rest of the app, so light and dark both look right, and charts are told to redraw
// when the theme changes. Hovering anywhere over a chart shows every series for that point, with a
// thin vertical guide (the crosshair). comparison() makes a "period before" series: dashed and muted.

export function cssVar(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

export function palette() {
    return {
        primary: cssVar('--bs-primary', '#0d6efd'),
        text: cssVar('--bs-secondary-color', '#6c757d'),
        strong: cssVar('--bs-body-color', '#212529'),
        grid: cssVar('--bs-border-color', '#dee2e6'),
        background: cssVar('--bs-body-bg', '#ffffff'),
        muted: cssVar('--gws-text-soft', '#8a8f98'),
        info: cssVar('--gws-status-info-text', '#0b6bcb'),
        success: cssVar('--gws-status-success-text', '#1a7f37'),
        warning: cssVar('--gws-status-warning-text', '#9a6700'),
        danger: cssVar('--gws-status-danger-text', '#cf222e')
    };
}

const crosshair = {
    id: 'gwsCrosshair',
    afterDatasetsDraw(chart) {
        const active = chart.tooltip && chart.tooltip.getActiveElements ? chart.tooltip.getActiveElements() : [];
        if (!active.length || chart.config.options.indexAxis === 'y') return;
        const x = active[0].element.x;
        const { top, bottom } = chart.chartArea;
        const context = chart.ctx;
        context.save();
        context.beginPath();
        context.moveTo(x, top);
        context.lineTo(x, bottom);
        context.lineWidth = 1;
        context.strokeStyle = palette().muted;
        context.setLineDash([3, 3]);
        context.stroke();
        context.restore();
    }
};

let registered = false;

// Sets Chart.js's defaults from the tokens; call before drawing (again after a theme change).
export function apply(Chart) {
    const colors = palette();
    Chart.defaults.color = colors.text;
    Chart.defaults.borderColor = colors.grid;
    Chart.defaults.font.family = getComputedStyle(document.body).fontFamily || Chart.defaults.font.family;
    Chart.defaults.interaction.mode = 'index';
    Chart.defaults.interaction.intersect = false;
    Chart.defaults.plugins.tooltip.backgroundColor = colors.strong;
    Chart.defaults.plugins.tooltip.titleColor = colors.background;
    Chart.defaults.plugins.tooltip.bodyColor = colors.background;
    if (!registered) {
        Chart.register(crosshair);
        registered = true;
    }
    return colors;
}

// A "period before" series drawn under the current one.
export function comparison(label, values) {
    return {
        label,
        data: values,
        borderColor: palette().muted,
        backgroundColor: 'transparent',
        borderDash: [5, 4],
        borderWidth: 2,
        pointRadius: 0,
        tension: 0.3,
        order: 2
    };
}

// Runs callback when the light/dark theme changes: the data-theme attribute, or the system setting
// while no theme is chosen. Returns a function that stops listening.
export function onThemeChange(callback) {
    const observer = new MutationObserver(() => callback());
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-bs-theme'] });
    const media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    media?.addEventListener?.('change', callback);
    return () => {
        observer.disconnect();
        media?.removeEventListener?.('change', callback);
    };
}
