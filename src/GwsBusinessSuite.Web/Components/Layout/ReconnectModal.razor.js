// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// Most reconnects (e.g. coming back to a tab) finish within a second or two: only show the
// modal when one is still going after a short grace, so a quick one doesn't flash it.
const showDelayMs = 2500;
let showTimer = null;

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        if (!reconnectModal.open && showTimer === null) {
            showTimer = setTimeout(() => {
                showTimer = null;
                reconnectModal.showModal();
            }, showDelayMs);
        }
        return;
    }
    if (showTimer !== null) {
        clearTimeout(showTimer);
        showTimer = null;
    }
    if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        // Failed inside the grace period: still show the modal, for its Retry button.
        if (!reconnectModal.open) reconnectModal.showModal();
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "paused") {
        // A paused session (Blazor sends "show" then "paused" straight away, which cancelled the
        // grace timer above) ignores every click until it's resumed, so this must not be silent:
        // show the modal with its Resume button, and resume by itself when the page is next shown
        // (e.g. switching back to the Mac app, which keeps pages open across server restarts).
        if (!reconnectModal.open) reconnectModal.showModal();
        document.addEventListener("visibilitychange", resumeWhenDocumentBecomesVisible);
    } else if (event.detail.state === "resume-failed") {
        location.reload();
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    document.removeEventListener("visibilitychange", resumeWhenDocumentBecomesVisible);
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        location.reload();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}

async function resumeWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await resume();
    }
}
