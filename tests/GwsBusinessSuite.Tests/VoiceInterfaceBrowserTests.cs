using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Exercises wwwroot/js/voiceInterface.js (SentinelGPT's Web Speech API voice interface)
// directly in a real Chromium page, the same harness pattern WikiBlockEditorBrowserTests uses.
// Deliberately mocks window.SpeechRecognition/webkitSpeechRecognition/speechSynthesis rather
// than relying on whatever level of Web Speech support the CI Chromium build happens to ship -
// that keeps these tests deterministic and, for the "unsupported" cases, verifies the module's
// most important property: browsers that lack either API (Firefox has neither; many headless/
// sandboxed environments lack a working speech backend even when the constructors exist) must
// degrade to a silent no-op rather than throwing and breaking the SentinelGPT page around it.
[Collection("Playwright")]
public sealed class VoiceInterfaceBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static async Task<IPage> LoadModuleAsync(PlaywrightBrowserFixture fixture)
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = "<main></main>"
        }));
        await page.GotoAsync("http://localhost/voice");

        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/voiceInterface.js"));
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Content = await File.ReadAllTextAsync(scriptPath)
        });
        await page.WaitForFunctionAsync("() => Boolean(window.gwsVoiceInterface)");

        // A fresh mock DotNetObjectReference per test, logging every invokeMethodAsync call
        // (method name + args) into window.calls so assertions can inspect exactly what the
        // module tried to report back to Blazor.
        await page.EvaluateAsync(
            """
            () => {
                window.calls = [];
                window.dotNetRef = {
                    invokeMethodAsync: (...args) => {
                        window.calls.push(args);
                        return Promise.resolve();
                    }
                };
            }
            """);
        return page;
    }

    [Fact]
    public async Task StartListening_ShouldNoOpAndNotThrow_WhenSpeechRecognitionIsUnavailable()
    {
        await using var page = await LoadModuleAsync(fixture);
        await page.EvaluateAsync("() => { window.SpeechRecognition = undefined; window.webkitSpeechRecognition = undefined; }");

        var supported = await page.EvaluateAsync<bool>("() => window.gwsVoiceInterface.isSttSupported()");
        supported.Should().BeFalse();

        await page.EvaluateAsync(
            """
            () => {
                window.gwsVoiceInterface.init('el', window.dotNetRef);
                window.gwsVoiceInterface.startListening('el', 'en-US');
            }
            """);

        var calls = await page.EvaluateAsync<int>("() => window.calls.length");
        calls.Should().Be(0, "an unsupported browser should never fire a voice callback");
    }

    [Fact]
    public async Task Speak_ShouldNoOpAndNotThrow_WhenSpeechSynthesisIsUnavailable()
    {
        await using var page = await LoadModuleAsync(fixture);
        // window.speechSynthesis is a getter-only native accessor - a plain assignment
        // silently no-ops, so overriding it for a test requires redefining the property.
        await page.EvaluateAsync("() => Object.defineProperty(window, 'speechSynthesis', { configurable: true, value: undefined })");

        var supported = await page.EvaluateAsync<bool>("() => window.gwsVoiceInterface.isTtsSupported()");
        supported.Should().BeFalse();

        await page.EvaluateAsync(
            """
            () => {
                window.gwsVoiceInterface.init('el', window.dotNetRef);
                window.gwsVoiceInterface.speak('el', 'Hello there', 'en-US');
            }
            """);

        var calls = await page.EvaluateAsync<int>("() => window.calls.length");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Speak_ShouldNoOp_ForBlankText()
    {
        await using var page = await LoadModuleAsync(fixture);
        await page.EvaluateAsync(
            """
            () => {
                window.__spoken = [];
                Object.defineProperty(window, 'speechSynthesis', {
                    configurable: true,
                    value: { cancel: () => {}, speak: (u) => window.__spoken.push(u.text) }
                });
            }
            """);

        await page.EvaluateAsync(
            """
            () => {
                window.gwsVoiceInterface.init('el', window.dotNetRef);
                window.gwsVoiceInterface.speak('el', '', 'en-US');
                window.gwsVoiceInterface.speak('el', '   ', 'en-US');
            }
            """);

        var spoken = await page.EvaluateAsync<int>("() => window.__spoken.length");
        spoken.Should().Be(0);
    }

    [Fact]
    public async Task Speak_ShouldCallSynthesisAndReportSpeakingEnded_ForRealText()
    {
        await using var page = await LoadModuleAsync(fixture);
        await page.EvaluateAsync(
            """
            () => {
                window.__spoken = [];
                Object.defineProperty(window, 'speechSynthesis', {
                    configurable: true,
                    value: {
                        cancel: () => {},
                        speak: (u) => { window.__spoken.push(u.text); u.onend(); }
                    }
                });
            }
            """);

        await page.EvaluateAsync(
            """
            () => {
                window.gwsVoiceInterface.init('el', window.dotNetRef);
                window.gwsVoiceInterface.speak('el', 'Hello there', 'en-US');
            }
            """);

        var spoken = await page.EvaluateAsync<string[]>("() => window.__spoken");
        spoken.Should().ContainSingle().Which.Should().Be("Hello there");
        var calls = await page.EvaluateAsync<string[][]>("() => window.calls.map(c => c.map(String))");
        calls.Should().ContainSingle(c => c[0] == "OnSpeakingEnded");
    }

    [Fact]
    public async Task StartListening_ShouldReportTranscriptThenListeningEnded_WhenRecognitionSucceeds()
    {
        await using var page = await LoadModuleAsync(fixture);
        await page.EvaluateAsync(
            """
            () => {
                window.SpeechRecognition = function () {
                    this.start = () => {
                        this.onresult({ results: [[{ transcript: 'add a follow up task' }]] });
                        this.onend();
                    };
                    this.stop = () => {};
                };
            }
            """);

        await page.EvaluateAsync(
            """
            () => {
                window.gwsVoiceInterface.init('el', window.dotNetRef);
                window.gwsVoiceInterface.startListening('el', 'en-US');
            }
            """);

        var calls = await page.EvaluateAsync<string[][]>("() => window.calls.map(c => [String(c[0]), String(c[1] ?? '')])");
        calls.Should().Contain(c => c[0] == "OnVoiceTranscript" && c[1] == "add a follow up task");
        calls.Should().Contain(c => c[0] == "OnListeningEnded");
    }

    [Fact]
    public async Task Destroy_ShouldStopTrackedRecognitionAndAllowReinitialization()
    {
        await using var page = await LoadModuleAsync(fixture);
        await page.EvaluateAsync(
            """
            () => {
                window.__stopped = false;
                window.SpeechRecognition = function () {
                    this.start = () => {};
                    this.stop = () => { window.__stopped = true; };
                };
            }
            """);

        await page.EvaluateAsync(
            """
            () => {
                window.gwsVoiceInterface.init('el', window.dotNetRef);
                window.gwsVoiceInterface.startListening('el', 'en-US');
                window.gwsVoiceInterface.destroy('el');
            }
            """);

        var stopped = await page.EvaluateAsync<bool>("() => window.__stopped");
        stopped.Should().BeTrue();

        // Re-init after destroy should work cleanly (no leftover instance blocking it).
        await page.EvaluateAsync("() => window.gwsVoiceInterface.init('el', window.dotNetRef)");
        var reinitialized = await page.EvaluateAsync<bool>(
            "() => { window.gwsVoiceInterface.startListening('el', 'en-US'); return true; }");
        reinitialized.Should().BeTrue();
    }
}
