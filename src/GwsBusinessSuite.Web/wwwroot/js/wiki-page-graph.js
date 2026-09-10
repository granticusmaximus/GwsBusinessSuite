// A one-hop "local graph" for a single Sentinel page - the page itself, everything that links
// to it, and everything it links to (see SentinelWorkspaceService.GetPageGraphAsync for why this
// is bounded to one hop rather than the whole wiki). Laid out with a small from-scratch
// force simulation, matching the automation canvas's own from-scratch approach elsewhere in
// this app rather than pulling in a graph-drawing library for what is normally a handful of
// nodes.
//
// The layout runs as a fixed number of ticks computed synchronously, then draws once - there is
// no ongoing animation loop to manage or cancel on dispose, which keeps this module simple and
// leak-free for a panel that opens and closes with a page's lifetime.

const SIMULATION_TICKS = 300;
const REPULSION = 2600;
const SPRING_LENGTH = 130;
const SPRING_STRENGTH = 0.02;
const CENTER_PULL = 0.01;
const DAMPING = 0.85;
const NODE_RADIUS = 22;
const CENTER_NODE_RADIUS = 28;

function simulate(nodes, edges, width, height) {
    const byId = new Map(nodes.map(node => [node.pageId, node]));
    const cx = width / 2;
    const cy = height / 2;

    // Deterministic-looking initial placement (a ring around the center) rather than Math.random,
    // so a graph with the same nodes lays out the same way every time it is opened.
    nodes.forEach((node, index) => {
        if (node.isCenter) {
            node.x = cx;
            node.y = cy;
        } else {
            const angle = (2 * Math.PI * index) / Math.max(1, nodes.length - 1);
            node.x = cx + Math.cos(angle) * 160;
            node.y = cy + Math.sin(angle) * 160;
        }
        node.vx = 0;
        node.vy = 0;
    });

    for (let tick = 0; tick < SIMULATION_TICKS; tick++) {
        // Repulsion - every pair of nodes pushes apart, so unrelated nodes don't overlap.
        for (let i = 0; i < nodes.length; i++) {
            for (let j = i + 1; j < nodes.length; j++) {
                const a = nodes[i];
                const b = nodes[j];
                let dx = a.x - b.x;
                let dy = a.y - b.y;
                let distSq = dx * dx + dy * dy;
                if (distSq < 1) { distSq = 1; dx = 1; dy = 0; }
                const force = REPULSION / distSq;
                const dist = Math.sqrt(distSq);
                const fx = (dx / dist) * force;
                const fy = (dy / dist) * force;
                a.vx += fx; a.vy += fy;
                b.vx -= fx; b.vy -= fy;
            }
        }

        // Springs - a linked pair is pulled toward SPRING_LENGTH apart, not zero: at rest an
        // edge should read as a visible line, not two overlapping nodes.
        for (const edge of edges) {
            const a = byId.get(edge.sourcePageId);
            const b = byId.get(edge.targetPageId);
            if (!a || !b) continue;
            const dx = b.x - a.x;
            const dy = b.y - a.y;
            const dist = Math.max(1, Math.sqrt(dx * dx + dy * dy));
            const displacement = dist - SPRING_LENGTH;
            const fx = (dx / dist) * displacement * SPRING_STRENGTH;
            const fy = (dy / dist) * displacement * SPRING_STRENGTH;
            a.vx += fx; a.vy += fy;
            b.vx -= fx; b.vy -= fy;
        }

        // A gentle pull toward the canvas center keeps a sparsely-connected graph from drifting
        // off into open space over 300 ticks of pure repulsion.
        for (const node of nodes) {
            node.vx += (cx - node.x) * CENTER_PULL;
            node.vy += (cy - node.y) * CENTER_PULL;
            node.vx *= DAMPING;
            node.vy *= DAMPING;
            node.x += node.vx;
            node.y += node.vy;
        }
    }

    // Keep every node inside the visible canvas after the simulation settles, so a node that
    // drifted near an edge during the physics pass is never clipped or unclickable.
    const margin = CENTER_NODE_RADIUS + 8;
    for (const node of nodes) {
        node.x = Math.min(width - margin, Math.max(margin, node.x));
        node.y = Math.min(height - margin, Math.max(margin, node.y));
    }
}

function truncate(text, max) {
    if (!text) return '';
    return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

export function render(canvas, graphJson) {
    const graph = typeof graphJson === 'string' ? JSON.parse(graphJson) : graphJson;
    const nodes = graph.nodes ?? [];
    const edges = graph.edges ?? [];

    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(320, rect.width || canvas.clientWidth || 640);
    const height = Math.max(280, rect.height || canvas.clientHeight || 420);
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;

    const ctx = canvas.getContext('2d');
    ctx.scale(dpr, dpr);

    if (nodes.length === 0) {
        ctx.clearRect(0, 0, width, height);
        return { nodes: [] };
    }

    simulate(nodes, edges, width, height);

    const styles = getComputedStyle(document.documentElement);
    const edgeColor = styles.getPropertyValue('--gws-panel-border').trim() || 'rgba(148,163,184,.5)';
    const nodeFill = styles.getPropertyValue('--gws-panel-bg').trim() || '#1c1917';
    const centerFill = '#f59e0b';
    const textColor = styles.getPropertyValue('--gws-sidebar-text').trim() || '#e7e5e4';

    ctx.clearRect(0, 0, width, height);

    ctx.strokeStyle = edgeColor;
    ctx.lineWidth = 1.5;
    for (const edge of edges) {
        const a = nodes.find(node => node.pageId === edge.sourcePageId);
        const b = nodes.find(node => node.pageId === edge.targetPageId);
        if (!a || !b) continue;
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();
    }

    ctx.font = '11px -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (const node of nodes) {
        const radius = node.isCenter ? CENTER_NODE_RADIUS : NODE_RADIUS;
        ctx.beginPath();
        ctx.arc(node.x, node.y, radius, 0, Math.PI * 2);
        ctx.fillStyle = node.isCenter ? centerFill : nodeFill;
        ctx.fill();
        ctx.strokeStyle = node.isCenter ? centerFill : edgeColor;
        ctx.lineWidth = 2;
        ctx.stroke();

        ctx.fillStyle = node.isCenter ? '#1c1917' : textColor;
        ctx.fillText(node.icon || '📄', node.x, node.y - 2);
    }

    // Labels drawn as a second pass, below each node, so long titles never get clipped by a
    // neighboring node drawn on top of them.
    ctx.fillStyle = textColor;
    for (const node of nodes) {
        const radius = node.isCenter ? CENTER_NODE_RADIUS : NODE_RADIUS;
        ctx.fillText(truncate(node.title, 18), node.x, node.y + radius + 12);
    }

    return { nodes };
}

export function initialize(canvas, dotNetRef, graphJson) {
    const layout = render(canvas, graphJson);
    const hitTest = event => {
        const rect = canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;
        return layout.nodes.find(node => {
            const radius = node.isCenter ? CENTER_NODE_RADIUS : NODE_RADIUS;
            const dx = node.x - x;
            const dy = node.y - y;
            return dx * dx + dy * dy <= radius * radius;
        });
    };

    const onClick = event => {
        const hit = hitTest(event);
        if (hit && !hit.isCenter) {
            dotNetRef.invokeMethodAsync('NavigateToWikiPageId', hit.pageId);
        }
    };
    const onMove = event => {
        canvas.style.cursor = hitTest(event) ? 'pointer' : 'default';
    };

    canvas.addEventListener('click', onClick);
    canvas.addEventListener('mousemove', onMove);
    canvas._wikiPageGraphCleanup = () => {
        canvas.removeEventListener('click', onClick);
        canvas.removeEventListener('mousemove', onMove);
    };
}

export function dispose(canvas) {
    canvas?._wikiPageGraphCleanup?.();
}
