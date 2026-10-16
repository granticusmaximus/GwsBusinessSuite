// Shared by the popup and the settings page. Safari exposes `browser`, Chrome/Edge `chrome`;
// both return promises from the APIs used here (Manifest V3).
const ext = globalThis.browser ?? globalThis.chrome;

async function loadSettings() {
  const stored = await ext.storage.local.get(["server", "token"]);
  return { server: stored.server ?? "", token: stored.token ?? "" };
}

// "https://admin.gwsapp.net/" or "admin.gwsapp.net" -> "https://admin.gwsapp.net"; null if not a URL.
function normalizeServer(value) {
  let text = (value ?? "").trim();
  if (!text) return null;
  if (!/^https?:\/\//i.test(text)) text = "https://" + text;
  try {
    const url = new URL(text);
    if (url.protocol === "http:" && !["localhost", "127.0.0.1"].includes(url.hostname)) return null;
    return url.origin;
  } catch {
    return null;
  }
}

async function callSentinel(settings, path, body) {
  const response = await fetch(settings.server + path, {
    method: body ? "POST" : "GET",
    headers: { "Authorization": "Bearer " + settings.token, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
    credentials: "omit"
  });
  let data = null;
  try { data = await response.json(); } catch { /* not JSON */ }
  if (!response.ok) {
    throw new Error(data?.error ?? (response.status === 429 ? "Too many clips in a minute - wait a moment." : `Sentinel answered ${response.status}.`));
  }
  return data;
}
