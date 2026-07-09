// Wraps xterm.js (vendored under wwwroot/lib/xterm, @xterm/xterm 6.0.0 +
// @xterm/addon-fit 0.11.0) so SshTerminalPanel.razor can host an interactive terminal
// over a plain <div>, without Blazor ever diffing into that element's children - all
// rendering is owned by xterm.js itself once init() runs.
window.gwsSshTerminal = (function () {
    const instances = {};

    function init(elementId, dotNetHelper, cols, rows) {
        if (instances[elementId]) {
            return;
        }

        const el = document.getElementById(elementId);
        if (!el) {
            return;
        }

        const term = new Terminal({ cols, rows, cursorBlink: true, convertEol: true });
        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);
        term.open(el);
        fitAddon.fit();

        // Blazor auto-marshals a C# byte[] parameter to/from a JS Uint8Array over the
        // circuit - pass Uint8Array directly both ways rather than converting to a plain
        // number array, which would silently switch to a slower/incorrect marshalling path.
        term.onData((data) => {
            dotNetHelper.invokeMethodAsync("OnTerminalInput", new TextEncoder().encode(data));
        });

        const resizeObserver = new ResizeObserver(() => {
            fitAddon.fit();
            dotNetHelper.invokeMethodAsync("OnTerminalResize", term.cols, term.rows);
        });
        resizeObserver.observe(el);

        instances[elementId] = { term, fitAddon, resizeObserver };
    }

    function write(elementId, byteArray) {
        const inst = instances[elementId];
        if (inst) {
            inst.term.write(byteArray);
        }
    }

    function destroy(elementId) {
        const inst = instances[elementId];
        if (!inst) {
            return;
        }

        inst.resizeObserver.disconnect();
        inst.term.dispose();
        delete instances[elementId];
    }

    return { init, write, destroy };
})();
