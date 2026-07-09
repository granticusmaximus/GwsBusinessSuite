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
            console.debug(`gwsSshTerminal.init: element #${elementId} not found in DOM yet; aborting init.`);
            return;
        }

        const term = new Terminal({ cols, rows, cursorBlink: true, convertEol: true });
        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);
        term.open(el);
        fitAddon.fit();

        // xterm's cursor blinks whether or not it's focused, so without this a connected
        // terminal looks alive but silently drops every keystroke until the user finds and
        // clicks its (off-screen, opacity:0) helper textarea. Focus it immediately instead,
        // matching a native terminal window that's ready to type in as soon as it opens.
        term.focus();

        // Blazor auto-marshals a C# byte[] parameter to/from a JS Uint8Array over the
        // circuit - pass Uint8Array directly both ways rather than converting to a plain
        // number array, which would silently switch to a slower/incorrect marshalling path.
        term.onData((data) => {
            dotNetHelper.invokeMethodAsync("OnTerminalInput", new TextEncoder().encode(data))
                .catch((err) => console.error("gwsSshTerminal: OnTerminalInput failed.", err));
        });

        const resizeObserver = new ResizeObserver(() => {
            fitAddon.fit();
            dotNetHelper.invokeMethodAsync("OnTerminalResize", term.cols, term.rows);
        });
        resizeObserver.observe(el);

        instances[elementId] = { term, fitAddon, resizeObserver };
        console.debug(`gwsSshTerminal.init: terminal attached to #${elementId}.`);
    }

    function write(elementId, byteArray) {
        const inst = instances[elementId];
        if (inst) {
            inst.term.write(byteArray);
        } else {
            console.debug(`gwsSshTerminal.write: no terminal instance for #${elementId}; ${byteArray.length} bytes dropped.`);
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

    // Mirrors DigitalOcean's droplet console: a separate, resizable popup window rather
    // than an inline panel. Sizing/positioning here is just the initial size - the window
    // chrome (resizable=yes) lets the user resize it afterward, and the terminal inside
    // re-fits itself via the ResizeObserver wired up in init().
    function openWindow() {
        const width = Math.min(1000, window.screen.availWidth - 100);
        const height = Math.min(700, window.screen.availHeight - 100);
        const left = Math.max(0, (window.screen.availWidth - width) / 2);
        const top = Math.max(0, (window.screen.availHeight - height) / 2);
        window.open(
            '/admin/ssh-terminal-window',
            'gws-ssh-terminal',
            `width=${width},height=${height},left=${left},top=${top},resizable=yes,scrollbars=yes`
        );
    }

    return { init, write, destroy, openWindow };
})();
