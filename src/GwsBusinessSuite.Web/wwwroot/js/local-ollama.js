// Talks to the Ollama running on the machine this browser is on (not the server's), so
// Content Studio's heavy generation never touches the droplet. The server builds the prompt and
// saves the result; this module only carries the model call. See BrowserLocalOllamaService.cs.
//
// In the Mac app the request goes through the app (LocalOllamaBridge.cs): browsers block a secure
// https page from fetching plain-http localhost (WebKit as mixed content; Chrome asks for local
// network permission). In a browser it falls back to fetch, which also needs the local Ollama to
// allow this origin: `launchctl setenv OLLAMA_ORIGINS "https://admin.gwsapp.net"`, then restart Ollama.

const active = new Map();

// ---- The Mac app's bridge: same shape as fetch's Response (ok, status, body stream, text, json). ----
const nativeRequests = new Map();
let nativeCounter = 0;

function nativeBridge() {
    return window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.gwsOllama;
}

// Called by the app with the response status, then base64 chunks, then end (or fail).
window.gwsOllamaNative = {
    head(id, status) {
        const entry = nativeRequests.get(id);
        if (!entry) return;
        entry.resolve({
            ok: status >= 200 && status < 300,
            status,
            body: entry.stream,
            async text() {
                const reader = entry.stream.getReader();
                const decoder = new TextDecoder();
                let text = "";
                for (;;) {
                    const { value, done } = await reader.read();
                    if (done) return text + decoder.decode();
                    text += decoder.decode(value, { stream: true });
                }
            },
            async json() { return JSON.parse(await this.text()); }
        });
    },
    chunk(id, base64) {
        const entry = nativeRequests.get(id);
        if (!entry) return;
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        entry.controller.enqueue(bytes);
    },
    end(id) {
        const entry = nativeRequests.get(id);
        if (!entry) return;
        nativeRequests.delete(id);
        entry.controller.close();
    },
    fail(id, message) {
        const entry = nativeRequests.get(id);
        if (!entry) return;
        nativeRequests.delete(id);
        const error = new Error(message);
        entry.reject(error);
        try { entry.controller.error(error); } catch { /* already closed */ }
    }
};

function localFetch(url, init = {}) {
    const bridge = nativeBridge();
    if (!bridge) {
        return fetch(url, init);
    }

    const target = new URL(url);
    const id = `ollama-${++nativeCounter}`;
    return new Promise((resolve, reject) => {
        const entry = { resolve, reject };
        entry.stream = new ReadableStream({ start(controller) { entry.controller = controller; } });
        nativeRequests.set(id, entry);
        if (init.signal) {
            init.signal.addEventListener("abort", () => {
                if (!nativeRequests.has(id)) return;
                nativeRequests.delete(id);
                bridge.postMessage({ op: "abort", id });
                const error = new DOMException("The request was aborted.", "AbortError");
                reject(error);
                try { entry.controller.error(error); } catch { /* already closed */ }
            }, { once: true });
        }
        bridge.postMessage({
            op: "fetch",
            id,
            path: target.pathname,
            method: init.method || "GET",
            port: Number(target.port || 80),
            body: typeof init.body === "string" ? init.body : null
        });
    });
}

// Blazor Server caps a single browser->server message at 32 KB, and one interop call per token
// would flood the circuit, so streamed text is batched by time and size before it is sent up.
const flushIntervalMs = 100;
const maxBatchChars = 8000;

function describeFailure(baseUrl, error) {
    if (error && error.name === "AbortError") {
        return "Local generation was cancelled.";
    }

    // fetch reports a refused connection, a CORS rejection and a browser block identically
    // ("Failed to fetch"), so the message names the causes. (The Mac app's bridge gives its own.)
    if (error instanceof TypeError) {
        return `Couldn't reach Ollama at ${baseUrl}. Use the Mac app, where this works directly. In a browser, ` +
            `Ollama must be running and allow this site (OLLAMA_ORIGINS including ${window.location.origin}), ` +
            `and the browser must allow it: Chrome asks for permission to reach local devices; Safari blocks it.`;
    }

    return error && error.message ? error.message : String(error);
}

export async function probe(baseUrl) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 4000);
    try {
        const response = await localFetch(`${baseUrl}/api/tags`, { signal: controller.signal });
        if (!response.ok) {
            return { ok: false, models: [], error: `Ollama answered HTTP ${response.status}.` };
        }

        const body = await response.json();
        const models = (body.models || []).map(model => model.name).filter(Boolean);
        return { ok: true, models, error: null };
    } catch (error) {
        const message = error && error.name === "AbortError"
            ? `Ollama at ${baseUrl} didn't answer within 4 seconds.`
            : describeFailure(baseUrl, error);
        return { ok: false, models: [], error: message };
    } finally {
        clearTimeout(timer);
    }
}

// Returns as soon as the request is started: Blazor Server times out a .NET->JS call after a
// minute, and a full article takes far longer. Progress, completion and failure all come back
// through the receiver's [JSInvokable] callbacks instead.
export function startGenerate(requestId, baseUrl, request, receiver) {
    const controller = new AbortController();
    active.set(requestId, controller);
    run(requestId, baseUrl, request, receiver, controller).finally(() => active.delete(requestId));
}

export function abort(requestId) {
    const controller = active.get(requestId);
    if (controller) {
        controller.abort();
    }
}

async function run(requestId, baseUrl, request, receiver, controller) {
    let pending = "";
    let lastFlush = Date.now();
    // Each callback awaits the previous one so fragments always reach .NET in order.
    let delivery = Promise.resolve();
    const send = (method, ...args) => {
        delivery = delivery.then(() => receiver.invokeMethodAsync(method, requestId, ...args));
        return delivery;
    };
    const flush = () => {
        if (pending.length > 0) {
            const text = pending;
            pending = "";
            send("OnChunk", text);
        }
        lastFlush = Date.now();
    };

    try {
        const payload = {
            model: request.model,
            system: request.system || "",
            prompt: request.prompt,
            stream: true,
            keep_alive: "30m"
        };
        if (request.numPredict) {
            payload.options = { num_predict: request.numPredict };
        }
        if (Array.isArray(request.images) && request.images.length > 0) {
            payload.images = request.images;
        }
        if (request.format) {
            payload.format = request.format;
        }

        const response = await localFetch(`${baseUrl}/api/generate`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
            signal: controller.signal
        });

        if (!response.ok) {
            const detail = await response.text().catch(() => "");
            throw new Error(response.status === 404
                ? `Model '${request.model}' isn't installed in this computer's Ollama. Install it (the Mac app's model manager or \`ollama pull\`) and try again.`
                : `Local Ollama answered HTTP ${response.status}. ${detail}`.trim());
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffered = "";
        let finished = false;

        while (!finished) {
            const { value, done } = await reader.read();
            if (done) {
                break;
            }

            buffered += decoder.decode(value, { stream: true });
            let newline;
            while ((newline = buffered.indexOf("\n")) >= 0) {
                const line = buffered.slice(0, newline).trim();
                buffered = buffered.slice(newline + 1);
                if (!line) {
                    continue;
                }

                const record = JSON.parse(line);
                if (record.error) {
                    throw new Error(`Local Ollama: ${record.error}`);
                }
                if (record.response) {
                    pending += record.response;
                }
                if (record.done) {
                    finished = true;
                    break;
                }
            }

            if (pending.length >= maxBatchChars || Date.now() - lastFlush >= flushIntervalMs) {
                flush();
            }
        }

        if (!finished) {
            throw new Error("The local Ollama stream ended before the model finished.");
        }

        flush();
        await send("OnCompleted");
    } catch (error) {
        flush();
        try {
            await send("OnFailed", describeFailure(baseUrl, error));
        } catch {
            // The circuit is gone (tab closed or disconnected) - there is no one left to tell.
        }
    }
}
