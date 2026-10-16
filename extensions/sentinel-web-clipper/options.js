const form = document.getElementById("settings");
const serverInput = document.getElementById("server");
const tokenInput = document.getElementById("token");
const status = document.getElementById("status");

function show(message, kind) {
  status.textContent = message;
  status.className = kind ?? "";
}

loadSettings().then(settings => {
  serverInput.value = settings.server;
  tokenInput.value = settings.token;
});

form.addEventListener("submit", async event => {
  event.preventDefault();
  const server = normalizeServer(serverInput.value);
  const token = tokenInput.value.trim();
  if (!server) return show("Enter the server as an https address.", "error");
  if (!token.startsWith("gwsclip_")) return show("A clip token starts with gwsclip_.", "error");
  // The popup calls the server directly, which needs permission for that one site.
  // Must be asked for in this click handler, before any other await.
  let granted = false;
  try {
    granted = await ext.permissions.request({ origins: [server + "/*"] });
  } catch (error) {
    return show("Couldn't ask for access to the server: " + error.message, "error");
  }
  if (!granted) return show("The clipper needs permission to reach " + server + ".", "error");
  await ext.storage.local.set({ server, token });
  serverInput.value = server;
  try {
    const me = await callSentinel({ server, token }, "/api/sentinel/clip/whoami");
    show(`Saved. Connected as ${me.username}.`, "ok");
  } catch (error) {
    show("Saved, but the test failed: " + error.message, "error");
  }
});
