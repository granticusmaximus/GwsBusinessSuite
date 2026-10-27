// A stand-in for window.prompt. In the Mac app, pressing Cancel on a prompt() crashed the whole
// app: MAUI's web view answers WebKit's text-input panel through a completion handler that throws
// when handed the null a cancelled prompt means (27-10-26, MauiWebViewUIDelegate
// .RunJavaScriptTextInputPanel). This draws the dialog in the page instead, with prompt()'s
// contract: it resolves with the text on OK or Enter, and with null on Cancel or Escape.
// Loaded by the admin (App.razor) and by the public site's edit mode (CmsBlockHtmlRenderer).
(function () {
    'use strict';
    if (window.gwsPrompt) return;

    // Self-contained styles (the public site doesn't load app.css); the admin's theme tokens are
    // used where they exist.
    var STYLE = [
        '.gws-prompt{border:1px solid var(--gws-panel-border,#d6d3d1);border-radius:12px;padding:16px;width:min(420px,calc(100vw - 32px));',
        'background:var(--gws-panel-bg,#fff);color:var(--gws-text-strong,#1c1917);box-shadow:0 12px 32px rgba(0,0,0,.35);font:14px/1.4 system-ui,-apple-system,sans-serif}',
        '.gws-prompt::backdrop{background:rgba(0,0,0,.45)}',
        '.gws-prompt-label{display:block;margin:0 0 8px;font-weight:600}',
        '.gws-prompt-input{box-sizing:border-box;width:100%;min-height:40px;padding:6px 10px;border:1px solid var(--gws-panel-border,#a8a29e);border-radius:8px;',
        'background:var(--gws-panel-bg-2,#fff);color:inherit;font:inherit}',
        '.gws-prompt-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:12px}',
        '.gws-prompt-actions button{min-height:36px;padding:0 14px;border-radius:8px;font:inherit;font-weight:600;cursor:pointer}',
        '.gws-prompt-cancel{border:1px solid var(--gws-panel-border,#a8a29e);background:transparent;color:inherit}',
        '.gws-prompt-ok{border:0;background:var(--gws-sidebar-accent,#f59e0b);color:var(--gws-accent-contrast,#1c1917)}',
        '@media (pointer: coarse){.gws-prompt-actions button,.gws-prompt-input{min-height:44px}}'
    ].join('');

    var count = 0;

    function ensureStyle() {
        if (document.getElementById('gws-prompt-style')) return;
        var style = document.createElement('style');
        style.id = 'gws-prompt-style';
        style.textContent = STYLE;
        document.head.appendChild(style);
    }

    function ask(message, defaultValue) {
        ensureStyle();
        return new Promise(function (resolve) {
            var id = 'gws-prompt-input-' + (++count);
            var dialog = document.createElement('dialog');
            dialog.className = 'gws-prompt';
            var form = document.createElement('form');
            var label = document.createElement('label');
            label.className = 'gws-prompt-label';
            label.htmlFor = id;
            label.textContent = message == null ? '' : String(message);
            var input = document.createElement('input');
            input.id = id;
            input.type = 'text';
            input.className = 'gws-prompt-input';
            input.autocomplete = 'off';
            input.value = defaultValue == null ? '' : String(defaultValue);
            var actions = document.createElement('div');
            actions.className = 'gws-prompt-actions';
            var cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'gws-prompt-cancel';
            cancel.textContent = 'Cancel';
            var ok = document.createElement('button');
            ok.type = 'submit';
            ok.className = 'gws-prompt-ok';
            ok.textContent = 'OK';
            actions.append(cancel, ok);
            form.append(label, input, actions);
            dialog.appendChild(form);

            var settled = false;
            function finish(value) {
                if (settled) return;
                settled = true;
                if (dialog.open) dialog.close();
                dialog.remove();
                resolve(value);
            }
            form.addEventListener('submit', function (event) { event.preventDefault(); finish(input.value); });
            cancel.addEventListener('click', function () { finish(null); });
            // Escape fires "cancel" on a modal dialog.
            dialog.addEventListener('cancel', function (event) { event.preventDefault(); finish(null); });
            // A page change can take the dialog away with the page; answer as Cancel.
            dialog.addEventListener('close', function () { finish(null); });

            document.body.appendChild(dialog);
            dialog.showModal();
            input.focus();
            input.select();
        });
    }

    // For editors: prompt() kept the text selection (it blocks); a dialog takes focus, so the
    // selection is saved first and put back - with focus on its editable - before resolving, so a
    // caller can apply its command straight after awaiting.
    function askKeepingSelection(message, defaultValue) {
        var selection = window.getSelection();
        var range = selection && selection.rangeCount ? selection.getRangeAt(0).cloneRange() : null;
        var node = range ? range.commonAncestorContainer : null;
        var element = node ? (node.nodeType === 1 ? node : node.parentElement) : null;
        var editable = element ? element.closest('[contenteditable="true"], [contenteditable=""]') : null;
        return ask(message, defaultValue).then(function (value) {
            if (editable) editable.focus({ preventScroll: true });
            if (range && selection) {
                selection.removeAllRanges();
                selection.addRange(range);
            }
            return value;
        });
    }

    window.gwsPrompt = { ask: ask, askKeepingSelection: askKeepingSelection };
})();
