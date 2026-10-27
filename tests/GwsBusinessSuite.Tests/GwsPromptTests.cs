using FluentAssertions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// gwsPrompt replaces window.prompt (27-10-26): Cancel on a prompt() crashed the Mac app, because
// MAUI's web view answers WebKit's text-input panel with a null its completion handler rejects.
[Collection("Playwright")]
public sealed class GwsPromptTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
    private static string ComponentsRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/Components"));

    [Fact]
    public void NoPageOrScript_ShouldUseTheNativePromptAgain()
    {
        var scripts = Directory.EnumerateFiles(WebRoot, "*.js", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}")
                && !path.EndsWith(".min.js", StringComparison.Ordinal)
                && !path.EndsWith("gws-prompt.js", StringComparison.Ordinal));
        var components = Directory.EnumerateFiles(ComponentsRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal));

        var offenders = scripts.Concat(components)
            .Where(path => File.ReadAllText(path) is var text
                && (text.Contains("window.prompt(", StringComparison.Ordinal) || text.Contains("(\"prompt\"", StringComparison.Ordinal)))
            .Select(path => Path.GetFileName(path))
            .ToList();

        offenders.Should().BeEmpty("use gwsPrompt.ask / askKeepingSelection - Cancel on a native prompt() crashes the Mac app");
    }

    [Fact]
    public async Task Ask_ShouldAnswerLikePrompt_OkEnterCancelAndEscape()
    {
        await using var page = await OpenAsync();

        var ok = page.EvaluateAsync<string?>("() => gwsPrompt.ask('Link URL', 'https://')");
        await page.FillAsync(".gws-prompt-input", "https://grantwatson.dev");
        await page.ClickAsync(".gws-prompt-ok");
        (await ok).Should().Be("https://grantwatson.dev");

        var enter = page.EvaluateAsync<string?>("() => gwsPrompt.ask('Name:', 'draft')");
        (await page.InputValueAsync(".gws-prompt-input")).Should().Be("draft", "the default text is filled in");
        await page.Keyboard.PressAsync("Enter");
        (await enter).Should().Be("draft");

        var cancel = page.EvaluateAsync<string?>("() => gwsPrompt.ask('Link URL')");
        await page.ClickAsync(".gws-prompt-cancel");
        (await cancel).Should().BeNull();

        var escape = page.EvaluateAsync<string?>("() => gwsPrompt.ask('Link URL')");
        await page.WaitForSelectorAsync(".gws-prompt-input");
        await page.Keyboard.PressAsync("Escape");
        (await escape).Should().BeNull();

        (await page.Locator("dialog.gws-prompt").CountAsync()).Should().Be(0, "every dialog leaves the page");
    }

    [Fact]
    public async Task AskKeepingSelection_ShouldPutTheEditorSelectionBack()
    {
        await using var page = await OpenAsync();
        await page.EvaluateAsync("""
            () => {
              const editor = document.querySelector('#editor');
              const text = editor.firstChild;
              const range = document.createRange();
              range.setStart(text, 6);
              range.setEnd(text, 11);
              editor.focus();
              getSelection().removeAllRanges();
              getSelection().addRange(range);
            }
            """);
        var linked = page.EvaluateAsync<string>("""
            () => gwsPrompt.askKeepingSelection('Link URL').then(url => {
              document.execCommand('createLink', false, url);
              return document.querySelector('#editor').innerHTML;
            })
            """);
        await page.FillAsync(".gws-prompt-input", "https://example.com");
        await page.ClickAsync(".gws-prompt-ok");

        (await linked).Should().Be("Hello <a href=\"https://example.com\">world</a> again");
    }

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.Browser.NewPageAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = """<!DOCTYPE html><html><body><div id="editor" contenteditable="true">Hello world again</div><script src="/js/gws-prompt.js"></script></body></html>"""
        }));
        await page.RouteAsync("http://localhost/js/gws-prompt.js", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/javascript",
            Path = Path.Combine(WebRoot, "js", "gws-prompt.js")
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.WaitForFunctionAsync("() => !!window.gwsPrompt");
        return page;
    }
}
