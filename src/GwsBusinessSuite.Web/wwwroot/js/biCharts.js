// Business Intelligence charts (BusinessIntelligence.razor). Chart.js is vendored under
// /lib/chartjs (served from 'self', so it also works in the Mac app and offline) and loaded
// once, on first use, rather than on every admin page.
let chartJsLoad;
const charts = new Map();

function loadChartJs() {
    if (window.Chart) return Promise.resolve();
    chartJsLoad ??= new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = '/lib/chartjs/chart.umd.min.js';
        script.onload = () => resolve();
        script.onerror = () => { chartJsLoad = undefined; reject(new Error('Chart.js failed to load.')); };
        document.head.appendChild(script);
    });
    return chartJsLoad;
}

function cssVar(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function formatValue(value, format) {
    if (format === 'Currency') return value.toLocaleString(undefined, { style: 'currency', currency: 'USD' });
    if (format === 'Rating') return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
    return value.toLocaleString(undefined, { maximumFractionDigits: 0 });
}

// type: "Bar" | "Line". Re-rendering the same canvas id replaces its previous chart.
// dotNetRef (optional) receives OnPointClicked(canvasId, label) for drill-down.
export async function render(canvasId, type, labels, values, valueFormat, metricLabel, dotNetRef) {
    await loadChartJs();
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    charts.get(canvasId)?.destroy();
    window.Chart.getChart(canvas)?.destroy(); // Blazor may hand this id a reused canvas element

    const primary = cssVar('--bs-primary', '#0d6efd');
    const text = cssVar('--bs-secondary-color', '#6c757d');
    const grid = cssVar('--bs-border-color', '#dee2e6');
    const isBar = type === 'Bar';
    const valueTicks = { color: text, precision: valueFormat === 'Rating' ? 1 : 0, callback: value => formatValue(value, valueFormat) };
    // Horizontal bars grow with the number of rows so long label lists stay readable.
    if (isBar) canvas.parentElement.style.height = `${Math.max(180, labels.length * 26 + 40)}px`;

    charts.set(canvasId, new window.Chart(canvas, {
        type: isBar ? 'bar' : 'line',
        data: {
            labels,
            datasets: [{
                label: metricLabel,
                data: values,
                backgroundColor: isBar ? primary : 'transparent',
                borderColor: primary,
                borderWidth: isBar ? 0 : 3,
                borderRadius: isBar ? 4 : 0,
                tension: 0.3,
                pointRadius: isBar ? 0 : 3
            }]
        },
        options: {
            indexAxis: isBar ? 'y' : 'x',
            maintainAspectRatio: false,
            animation: false,
            onClick: (_event, elements) => {
                if (dotNetRef && elements.length) dotNetRef.invokeMethodAsync('OnPointClicked', canvasId, labels[elements[0].index]);
            },
            onHover: (event, elements) => {
                if (dotNetRef) event.native.target.style.cursor = elements.length ? 'pointer' : 'default';
            },
            plugins: {
                legend: { display: false },
                tooltip: { callbacks: { label: ctx => `${metricLabel}: ${formatValue(ctx.parsed[isBar ? 'x' : 'y'], valueFormat)}` } }
            },
            scales: {
                // The value axis is x for horizontal bars, y for lines; counts tick in whole numbers.
                x: { ticks: isBar ? valueTicks : { color: text }, grid: { color: grid }, beginAtZero: true },
                y: { ticks: isBar ? { color: text, autoSkip: false } : valueTicks, grid: { color: grid }, beginAtZero: true }
            }
        }
    }));
}

export function dispose(canvasId) {
    charts.get(canvasId)?.destroy();
    charts.delete(canvasId);
}

// Saves the chart as a PNG on a solid background (a transparent PNG is unreadable in dark viewers).
export function downloadPng(canvasId, fileName) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    const copy = document.createElement('canvas');
    copy.width = canvas.width;
    copy.height = canvas.height;
    const context = copy.getContext('2d');
    context.fillStyle = cssVar('--bs-body-bg', '#ffffff');
    context.fillRect(0, 0, copy.width, copy.height);
    context.drawImage(canvas, 0, 0);
    const link = document.createElement('a');
    link.href = copy.toDataURL('image/png');
    link.download = fileName;
    link.click();
}
