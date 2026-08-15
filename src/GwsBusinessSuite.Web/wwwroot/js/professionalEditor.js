window.gwsProfessionalEditor = (() => {
    const instances = new Map();
    const allowedTags = new Set(["P", "DIV", "BR", "STRONG", "B", "EM", "I", "U", "S", "DEL", "H1", "H2", "H3", "UL", "OL", "LI", "BLOCKQUOTE", "PRE", "CODE", "A", "HR"]);

    function escapeHtml(value) {
        const el = document.createElement("div");
        el.textContent = value ?? "";
        return el.innerHTML;
    }

    function inlineMarkdown(value) {
        let html = escapeHtml(value);
        html = html.replace(/`([^`\n]+)`/g, "<code>$1</code>");
        html = html.replace(/\[([^\]]+)]\((https?:\/\/[^\s)]+|mailto:[^\s)]+)\)/g, '<a href="$2">$1</a>');
        html = html.replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>");
        html = html.replace(/__([^_\n]+)__/g, "<strong>$1</strong>");
        html = html.replace(/~~([^~\n]+)~~/g, "<s>$1</s>");
        html = html.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
        return html;
    }

    function markdownToHtml(markdown) {
        const lines = (markdown ?? "").replace(/\r\n?/g, "\n").split("\n");
        const result = [];
        let list = null;
        let paragraph = [];
        let code = [];
        let inCode = false;

        const flushParagraph = () => {
            if (paragraph.length) result.push(`<p>${inlineMarkdown(paragraph.join(" "))}</p>`);
            paragraph = [];
        };
        const closeList = () => {
            if (list) result.push(`</${list}>`);
            list = null;
        };

        for (const line of lines) {
            if (/^```/.test(line.trim())) {
                flushParagraph(); closeList();
                if (inCode) { result.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`); code = []; }
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.push(line); continue; }
            if (!line.trim()) { flushParagraph(); closeList(); continue; }
            let match;
            if ((match = line.match(/^(#{1,3})\s+(.+)$/))) {
                flushParagraph(); closeList();
                result.push(`<h${match[1].length}>${inlineMarkdown(match[2])}</h${match[1].length}>`);
            } else if ((match = line.match(/^>\s?(.*)$/))) {
                flushParagraph(); closeList(); result.push(`<blockquote>${inlineMarkdown(match[1])}</blockquote>`);
            } else if ((match = line.match(/^[-*+]\s+(.+)$/))) {
                flushParagraph();
                if (list !== "ul") { closeList(); list = "ul"; result.push("<ul>"); }
                result.push(`<li>${inlineMarkdown(match[1].replace(/^\[[ xX]]\s*/, ""))}</li>`);
            } else if ((match = line.match(/^\d+[.)]\s+(.+)$/))) {
                flushParagraph();
                if (list !== "ol") { closeList(); list = "ol"; result.push("<ol>"); }
                result.push(`<li>${inlineMarkdown(match[1])}</li>`);
            } else if (/^([-*_])\1\1+$/.test(line.trim())) {
                flushParagraph(); closeList(); result.push("<hr>");
            } else {
                closeList(); paragraph.push(line.trim());
            }
        }
        if (inCode && code.length) result.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
        flushParagraph(); closeList();
        return sanitize(result.join("") || "");
    }

    function sanitize(html) {
        const template = document.createElement("template");
        template.innerHTML = html ?? "";
        const walk = (node) => {
            [...node.children].forEach((child) => {
                if (!allowedTags.has(child.tagName)) {
                    walk(child);
                    child.replaceWith(...child.childNodes);
                    return;
                }
                [...child.attributes].forEach((attribute) => {
                    if (child.tagName !== "A" || !["href", "title"].includes(attribute.name.toLowerCase())) child.removeAttribute(attribute.name);
                });
                if (child.tagName === "A") {
                    const href = child.getAttribute("href") ?? "";
                    if (!/^(https?:|mailto:|\/|#)/i.test(href)) child.removeAttribute("href");
                    else { child.setAttribute("target", "_blank"); child.setAttribute("rel", "noopener noreferrer"); }
                }
                walk(child);
            });
        };
        walk(template.content);
        return template.innerHTML;
    }

    function textOf(node) { return (node.textContent ?? "").replace(/\u00a0/g, " "); }
    function serialize(node, depth = 0) {
        if (node.nodeType === Node.TEXT_NODE) return textOf(node);
        if (node.nodeType !== Node.ELEMENT_NODE) return "";
        const tag = node.tagName;
        const inner = [...node.childNodes].map(child => serialize(child, depth)).join("");
        switch (tag) {
            case "STRONG": case "B": return `**${inner}**`;
            case "EM": case "I": return `*${inner}*`;
            case "S": case "DEL": return `~~${inner}~~`;
            case "U": return inner;
            case "CODE": return node.parentElement?.tagName === "PRE" ? inner : `\`${inner}\``;
            case "A": return node.getAttribute("href") ? `[${inner}](${node.getAttribute("href")})` : inner;
            case "BR": return "\n";
            case "H1": return `# ${inner}\n\n`;
            case "H2": return `## ${inner}\n\n`;
            case "H3": return `### ${inner}\n\n`;
            case "BLOCKQUOTE": return textOf(node).split("\n").map(line => `> ${line}`).join("\n") + "\n\n";
            case "PRE": return `\`\`\`\n${textOf(node).trimEnd()}\n\`\`\`\n\n`;
            case "HR": return "---\n\n";
            case "UL": case "OL": return [...node.children].map((li, index) => `${tag === "OL" ? `${index + 1}.` : "-"} ${serializeListItem(li, depth + 1)}`).join("\n") + "\n\n";
            case "LI": return inner;
            case "P": case "DIV": return `${inner}\n\n`;
            default: return inner;
        }
    }

    function serializeListItem(item, depth) {
        return [...item.childNodes].filter(node => !(node.nodeType === Node.ELEMENT_NODE && ["UL", "OL"].includes(node.tagName)))
            .map(node => serialize(node, depth)).join("").trim();
    }

    function htmlToMarkdown(editor) {
        return [...editor.childNodes].map(node => serialize(node)).join("")
            .replace(/[ \t]+\n/g, "\n").replace(/\n{3,}/g, "\n\n").trim();
    }

    function updateCount(instance) {
        const words = textOf(instance.editor).trim().match(/\S+/g)?.length ?? 0;
        instance.count.textContent = `${words} ${words === 1 ? "word" : "words"}`;
    }

    function notify(instance) {
        updateCount(instance);
        clearTimeout(instance.timer);
        instance.timer = setTimeout(() => instance.dotNet.invokeMethodAsync("OnEditorChanged", htmlToMarkdown(instance.editor)), 120);
    }

    function currentBlock() {
        const selection = window.getSelection();
        let node = selection?.anchorNode;
        if (!node) return null;
        if (node.nodeType === Node.TEXT_NODE) node = node.parentElement;
        return node?.closest("p,div,h1,h2,h3,li,blockquote") ?? node;
    }

    function replaceBlockShortcut(instance, event) {
        if (event.key !== " " || event.ctrlKey || event.metaKey || event.altKey) return;
        const block = currentBlock();
        if (!block || !instance.editor.contains(block)) return;
        const marker = textOf(block);
        const commands = { "#": "h1", "##": "h2", "###": "h3", ">": "blockquote", "-": "insertUnorderedList", "*": "insertUnorderedList", "1.": "insertOrderedList" };
        const command = commands[marker];
        if (!command) return;
        event.preventDefault();
        block.textContent = "";
        execute(instance, command);
        notify(instance);
    }

    function replaceInlineMarkdown(instance) {
        const selection = window.getSelection();
        if (!selection || !selection.rangeCount || !selection.isCollapsed) return;
        const node = selection.anchorNode;
        if (!node || node.nodeType !== Node.TEXT_NODE) return;
        const before = node.textContent.slice(0, selection.anchorOffset);
        const patterns = [
            { re: /\*\*([^*]+)\*\*$/, tag: "strong" }, { re: /__([^_]+)__$/, tag: "strong" },
            { re: /~~([^~]+)~~$/, tag: "s" }, { re: /`([^`]+)`$/, tag: "code" },
            { re: /\*([^*]+)\*$/, tag: "em" }
        ];
        const found = patterns.map(item => ({ ...item, match: before.match(item.re) })).find(item => item.match);
        if (!found) return;
        const start = selection.anchorOffset - found.match[0].length;
        const range = document.createRange();
        range.setStart(node, start); range.setEnd(node, selection.anchorOffset);
        const element = document.createElement(found.tag); element.textContent = found.match[1];
        range.deleteContents(); range.insertNode(element);
        range.setStartAfter(element); range.collapse(true); selection.removeAllRanges(); selection.addRange(range);
    }

    function execute(instance, command) {
        instance.editor.focus();
        if (["paragraph", "h1", "h2", "h3", "blockquote"].includes(command)) {
            document.execCommand("formatBlock", false, command === "paragraph" ? "p" : command);
        } else if (command === "code") {
            document.execCommand("formatBlock", false, "pre");
        } else if (command === "link") {
            const href = window.prompt("Link address (https://…)");
            if (href && /^(https?:\/\/|mailto:|\/|#)/i.test(href)) document.execCommand("createLink", false, href);
        } else {
            document.execCommand(command, false);
        }
        notify(instance);
    }

    function init(rootId, editorId, countId, dotNet, initialHtml) {
        if (instances.has(rootId)) return;
        const root = document.getElementById(rootId);
        const editor = document.getElementById(editorId);
        const count = document.getElementById(countId);
        if (!root || !editor || !count) return;
        editor.innerHTML = sanitize(initialHtml);
        const instance = { root, editor, count, dotNet, timer: null, handlers: [] };
        const on = (element, event, handler) => { element.addEventListener(event, handler); instance.handlers.push([element, event, handler]); };
        root.querySelectorAll("[data-command]").forEach(button => {
            on(button, "mousedown", event => event.preventDefault());
            on(button, "click", () => execute(instance, button.dataset.command));
        });
        on(editor, "keydown", event => replaceBlockShortcut(instance, event));
        on(editor, "input", () => { replaceInlineMarkdown(instance); notify(instance); });
        on(editor, "paste", event => {
            const text = event.clipboardData?.getData("text/plain");
            if (text == null) return;
            event.preventDefault();
            document.execCommand("insertHTML", false, markdownToHtml(text));
            notify(instance);
        });
        on(editor, "drop", event => event.preventDefault());
        updateCount(instance);
        instances.set(rootId, instance);
    }

    function setHtml(rootId, html) {
        const instance = instances.get(rootId);
        if (!instance || instance.editor.matches(":focus")) return;
        instance.editor.innerHTML = sanitize(html);
        updateCount(instance);
    }

    function insertMarkdown(rootId, markdown) {
        const instance = instances.get(rootId);
        if (!instance) return;
        instance.editor.focus();
        document.execCommand("insertHTML", false, markdownToHtml(markdown));
        notify(instance);
    }

    function insertText(rootId, value) {
        const instance = instances.get(rootId);
        if (!instance) return;
        instance.editor.focus();
        document.execCommand("insertText", false, value ?? "");
        notify(instance);
    }

    function focus(rootId) { instances.get(rootId)?.editor.focus(); }
    function destroy(rootId) {
        const instance = instances.get(rootId);
        if (!instance) return;
        clearTimeout(instance.timer);
        instance.handlers.forEach(([element, event, handler]) => element.removeEventListener(event, handler));
        instances.delete(rootId);
    }

    return { init, setHtml, insertMarkdown, insertText, focus, destroy, markdownToHtml };
})();
