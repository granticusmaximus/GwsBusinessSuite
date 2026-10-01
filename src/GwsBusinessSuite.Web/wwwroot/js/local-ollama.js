// Talks to the Ollama running on the machine this browser is on (not the server's), so
// Content Studio's heavy generation never touches the droplet. The server builds the prompt and
// saves the result; this module only carries the model call. See BrowserLocalOllamaService.cs.
//
// For a page served from https://admin.gwsapp.net to reach it, the local Ollama must allow that
// origin: `launchctl setenv OLLAMA_ORIGINS "https://admin.gwsapp.net"`, then restart Ollama.

const active = new Map();

// Blazor Server caps a single browser->server message at 32 KB, and one interop call per token
// would flood the circuit, so streamed text is batched by time and size before it is sent up.
const flushIntervalMs = 100;
const maxBatchChars = 8000;

function describeFailure(baseUrl, error) {
    if (error && error.name === "AbortError") {
        return "Local generation was cancelled.";
    }

    // fetch reports a refused connection and a CORS rejection identically ("Failed to fetch"),
    // so the message has to name both causes.
    if (error instanceof TypeError) {
        return `Couldn't reach Ollama at ${baseUrl}. Make sure Ollama is running on this computer and ` +
            `allows this site (set OLLAMA_ORIGINS to include ${window.location.origin}, then restart Ollama).`;
    }

    return error && error.message ? error.message : String(error);
}

export async function probe(baseUrl) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 4000);
    try {
        const response = await fetch(`${baseUrl}/api/tags`, { signal: controller.signal });
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

        const response = await fetch(`${baseUrl}/api/generate`, {
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
