using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Content Studio's local Ollama calls inside the Mac app go through the app's message handler
// (LocalOllamaBridge.cs), because WebKit blocks an https page fetching http://localhost. These
// drive local-ollama.js against a fake handler that answers the way the app does.
[Collection("Playwright")]
public sealed class LocalOllamaBridgeBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string ScriptPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/local-ollama.js"));

    [Fact]
    public async Task Probe_ShouldListModels_ThroughTheApp()
    {
        await using var page = await OpenAsync();
        var result = await page.EvaluateAsync<string>("""
            async () => {
              window.fakeReply = (msg) => {
                window.gwsOllamaNative.head(msg.id, 200);
                window.gwsOllamaNative.chunk(msg.id, btoa('{"models":[{"name":"gemma4"},{"name":"llama3.2"}]}'));
                window.gwsOllamaNative.end(msg.id);
              };
              return JSON.stringify(await window.localOllama.probe('http://localhost:11434'));
            }
            """);
        result.Should().Be("""{"ok":true,"models":["gemma4","llama3.2"],"error":null}""");
        (await page.EvaluateAsync<string>("() => JSON.stringify(window.sent[0])"))
            .Should().Be("""{"op":"fetch","id":"ollama-1","path":"/api/tags","method":"GET","port":11434,"body":null}""");
    }

    [Fact]
    public async Task Generate_ShouldStreamTheResponse_EvenWhenChunksSplitLinesAndCharacters()
    {
        await using var page = await OpenAsync();
        var outcome = await page.EvaluateAsync<string>("""
            async () => {
              const ndjson = '{"response":"Caf"}\n{"response":"é au lait"}\n{"response":"!","done":true}\n';
              const bytes = new TextEncoder().encode(ndjson);
              // Split inside the two-byte "é" (everything before it is ASCII, so char index = byte
              // index), then again in the middle of the last line.
              const insideE = ndjson.indexOf('é') + 1;
              const midLine = bytes.length - 10;
              const parts = [bytes.slice(0, insideE), bytes.slice(insideE, midLine), bytes.slice(midLine)];
              const b64 = (part) => btoa(String.fromCharCode(...part));
              window.fakeReply = (msg) => {
                window.lastBody = msg.body;
                window.gwsOllamaNative.head(msg.id, 200);
                parts.forEach(part => window.gwsOllamaNative.chunk(msg.id, b64(part)));
                window.gwsOllamaNative.end(msg.id);
              };
              const calls = [];
              const done = new Promise(resolve => {
                const receiver = { invokeMethodAsync: (method, id, arg) => {
                  calls.push(method === 'OnChunk' ? arg : method);
                  if (method === 'OnCompleted' || method === 'OnFailed') resolve();
                  return Promise.resolve();
                } };
                window.localOllama.startGenerate('r1', 'http://localhost:11434', { model: 'gemma4', prompt: 'Coffee?' }, receiver);
              });
              await done;
              return JSON.stringify({ text: calls.filter(c => c !== 'OnCompleted').join(''), last: calls[calls.length - 1], body: JSON.parse(window.lastBody) });
            }
            """);
        outcome.Should().Contain("\"text\":\"Café au lait!\"").And.Contain("\"last\":\"OnCompleted\"")
            .And.Contain("\"model\":\"gemma4\"").And.Contain("\"stream\":true");
        (await page.EvaluateAsync<string>("() => window.sent[0].path + ' ' + window.sent[0].method")).Should().Be("/api/generate POST");
    }

    [Fact]
    public async Task Abort_And_AppFailures_ShouldReachThePage()
    {
        await using var page = await OpenAsync();
        var outcome = await page.EvaluateAsync<string>("""
            async () => {
              const run = (reply) => new Promise(resolve => {
                window.fakeReply = reply;
                const receiver = { invokeMethodAsync: (method, id, arg) => {
                  if (method === 'OnFailed' || method === 'OnCompleted') resolve(method + ': ' + (arg || ''));
                  return Promise.resolve();
                } };
                window.localOllama.startGenerate('r' + Math.random(), 'http://localhost:11434', { model: 'm', prompt: 'p' }, receiver);
              });
              const failed = await run(msg => window.gwsOllamaNative.fail(msg.id, "Couldn't reach Ollama on this Mac (127.0.0.1:11434). Make sure Ollama is running."));
              const aborted = await run(msg => { window.gwsOllamaNative.head(msg.id, 200); setTimeout(() => window.localOllama.abort(window.lastId), 0); });
              return failed + ' | ' + aborted + ' | ' + window.sent.map(m => m.op).join(',');
            }
            """);
        outcome.Should().Be("OnFailed: Couldn't reach Ollama on this Mac (127.0.0.1:11434). Make sure Ollama is running. | OnFailed: Local generation was cancelled. | fetch,fetch,abort");
    }

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = "<!DOCTYPE html><html><body></body></html>"
        }));
        // The app's message handler, faked: records each message and lets the test reply.
        await page.AddInitScriptAsync("""
            window.sent = [];
            window.webkit = { messageHandlers: { gwsOllama: { postMessage: (msg) => {
              window.sent.push(msg);
              if (msg.op === 'fetch') setTimeout(() => window.fakeReply(msg), 0);
            } } } };
            """);
        var source = (await File.ReadAllTextAsync(ScriptPath))
            .Replace("export async function ", "async function ", StringComparison.Ordinal)
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.localOllama = { probe, startGenerate, abort: (id) => abort(id) };"
            // startGenerate keys requests by the caller's id; remember it for the abort case.
            + "\nconst originalStart = window.localOllama.startGenerate; window.localOllama.startGenerate = (id, ...rest) => { window.lastId = id; return originalStart(id, ...rest); };";
        await page.RouteAsync("http://localhost/local-ollama.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = source
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/local-ollama.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.localOllama)");
        return page;
    }
}
