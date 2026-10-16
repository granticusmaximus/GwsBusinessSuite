const form = document.getElementById("clip");
const status = document.getElementById("status");
const titleInput = document.getElementById("title");
const targetSelect = document.getElementById("target");
const clipButton = document.getElementById("clip-button");
let page = null;
let settings = null;

function show(message, kind) {
  status.textContent = message;
  status.className = kind ?? "";
}

function openSettings(event) {
  event?.preventDefault();
  ext.runtime.openOptionsPage();
  window.close();
}
document.getElementById("open-settings").addEventListener("click", openSettings);
document.getElementById("setup-button").addEventListener("click", openSettings);

// Runs inside the page. Scripts, styles and other non-content parts are dropped before the
// HTML is sent, which keeps big pages under the server's size limit.
function capturePage() {
  const selection = getSelection();
  let selectionHtml = "";
  if (selection && selection.rangeCount > 0 && !selection.isCollapsed) {
    const holder = document.createElement("div");
    for (let index = 0; index < selection.rangeCount; index++) holder.appendChild(selection.getRangeAt(index).cloneContents());
    selectionHtml = holder.innerHTML;
  }
  const copy = document.documentElement.cloneNode(true);
  copy.querySelectorAll("script, style, noscript, template, iframe, svg, link, meta").forEach(node => node.remove());
  return { title: document.title, url: location.href, html: copy.outerHTML, selectionHtml };
}

async function start() {
  settings = await loadSettings();
  if (!settings.server || !settings.token) {
    document.getElementById("setup").hidden = false;
    return;
  }
  form.hidden = false;
  targetSelect.value = (await ext.storage.local.get("target")).target ?? "page";
  const [tab] = await ext.tabs.query({ active: true, currentWindow: true });
  try {
    const [result] = await ext.scripting.executeScript({ target: { tabId: tab.id }, func: capturePage });
    page = result.result;
  } catch {
    // Browser pages, the extension store and similar can't be read - a link still works.
    page = { title: tab.title ?? "", url: tab.url ?? "", html: "", selectionHtml: "" };
    form.querySelectorAll("input[name=mode]").forEach(radio => {
      radio.disabled = radio.value !== "link";
      radio.checked = radio.value === "link";
    });
    show("This page can't be read, so only the link can be clipped.");
  }
  titleInput.value = page.title;
  if (page.selectionHtml) form.querySelector("input[value=selection]").checked = true;
  else form.querySelector("input[value=selection]").disabled = true;
}

form.addEventListener("submit", async event => {
  event.preventDefault();
  const mode = form.querySelector("input[name=mode]:checked").value;
  clipButton.disabled = true;
  show("Clipping...");
  try {
    await ext.storage.local.set({ target: targetSelect.value });
    const result = await callSentinel(settings, "/api/sentinel/clip", {
      mode,
      url: page.url,
      title: titleInput.value.trim() || null,
      html: mode === "article" ? page.html : mode === "selection" ? page.selectionHtml : null,
      note: document.getElementById("note").value.trim() || null,
      target: targetSelect.value,
      tags: document.getElementById("tags").value.split(/[,\s]+/).map(tag => tag.replace(/^#/, "")).filter(Boolean)
    });
    status.className = "ok";
    status.textContent = result.target === "today" ? "Added to today's daily note. " : "Saved. ";
    const link = document.createElement("a");
    link.href = result.url;
    link.target = "_blank";
    link.rel = "noopener";
    link.textContent = "Open in Sentinel";
    status.appendChild(link);
  } catch (error) {
    show(error.message, "error");
  } finally {
    clipButton.disabled = false;
  }
});

start().catch(error => show(error.message, "error"));
