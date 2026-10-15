using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace GwsBusinessSuite.Tests;

// Sentinel item 5, live business blocks in the real editor JS, with the Wiki component's
// SearchLiveBlockTargets / GetLiveBlock answered by a fake whose "record" the test changes.
[Collection("Playwright")]
public sealed class SentinelLiveBlockBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

    private const string FakeDotNet = """
        () => {
            window.liveStage = 'Negotiation';
            window.liveCalls = [];
            const ref = {
                invokeMethodAsync: (method, ...args) => {
                    window.liveCalls.push(method);
                    if (method === 'SearchLiveBlockTargets') {
                        return Promise.resolve([
                            { id: '11111111-1111-1111-1111-111111111111', label: 'Website', detail: 'Sam Client · Negotiation' },
                            { id: '22222222-2222-2222-2222-222222222222', label: 'Retainer', detail: 'Pat Other · Lead' }
                        ].filter(item => item.label.toLowerCase().includes((args[1] || '').toLowerCase())));
                    }
                    if (method === 'GetLiveBlock') {
                        return Promise.resolve({
                            kind: args[0], title: 'Website', subtitle: null, link: '/admin/crm/contacts/abc',
                            fields: [{ label: 'Stage', value: window.liveStage }, { label: 'Value', value: '$4,000' }],
                            bars: [], rows: [], asOf: '15:00', error: null
                        });
                    }
                    return Promise.resolve([]);
                }
            };
            window.sentinelBlockEditor.initialize(document.querySelector('#editor'), ref, '[]');
        }
        """;

    [Fact]
    public async Task SlashDeal_ShouldPickADeal_ShowItLive_AndPickUpChangesOnTheNextRefresh()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
        await page.Clock.InstallAsync();
        await page.RouteAsync("http://localhost/harness", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = """<!DOCTYPE html><html><body><div id="editor" class="wiki-block-editor sentinel-workspace"></div></body></html>"""
        }));
        var moduleSource = (await File.ReadAllTextAsync(Path.Combine(WebRoot, "js", "wiki-block-editor.js")))
            .Replace("export function ", "function ", StringComparison.Ordinal)
            + "\nwindow.sentinelBlockEditor = { initialize, getBlocksJson, dispose };";
        await page.RouteAsync("http://localhost/js/editor-under-test.js", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/javascript", Body = moduleSource
        }));
        await page.GotoAsync("http://localhost/harness");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Type = "module", Url = "http://localhost/js/editor-under-test.js" });
        await page.WaitForFunctionAsync("() => Boolean(window.sentinelBlockEditor)");
        await page.EvaluateAsync(FakeDotNet);

        await page.Locator(".wiki-block-content").FillAsync("/deal");
        await page.GetByRole(AriaRole.Listbox, new() { Name = "Insert a block" }).GetByRole(AriaRole.Option)
            .Filter(new() { HasText = "A live card for a deal" }).ClickAsync();
        await Expect(page.Locator(".wiki-block")).ToHaveAttributeAsync("data-block-type", WikiBlockTypes.Business);

        // The picker searches as you type.
        var options = page.Locator(".wiki-live-option");
        await Expect(options).ToHaveCountAsync(2);
        await page.Locator(".wiki-live-search").FillAsync("web");
        await page.Clock.RunForAsync(300);
        await Expect(options).ToHaveCountAsync(1);
        await options.First.ClickAsync();

        var card = page.Locator(".wiki-live-block");
        await Expect(card.Locator(".wiki-live-title")).ToHaveTextAsync("Website");
        await Expect(card.Locator(".wiki-live-title")).ToHaveAttributeAsync("href", "/admin/crm/contacts/abc");
        await Expect(card.Locator(".wiki-live-fields")).ToContainTextAsync("Negotiation");

        // The deal moves elsewhere; the card shows it on its next 30 s refresh without a reload.
        await page.EvaluateAsync("() => { window.liveStage = 'Won'; }");
        await page.Clock.RunForAsync(31000);
        await Expect(card.Locator(".wiki-live-fields")).ToContainTextAsync("Won");

        // Only the choice is saved - never the data on the card.
        var json = await page.EvaluateAsync<string>("() => window.sentinelBlockEditor.getBlocksJson(document.querySelector('#editor'))");
        var block = WikiBlockJson.ParseBlocks(json).Should().ContainSingle().Subject;
        block.Type.Should().Be(WikiBlockTypes.Business);
        block.Props["kind"].Should().Be(SentinelLiveBlockKinds.Deal);
        block.Props["recordId"].Should().Be("11111111-1111-1111-1111-111111111111");
        block.Props["recordLabel"].Should().Be("Website");
        json.Should().NotContain("Won").And.NotContain("$4,000");

        // Change goes back to the picker.
        await card.GetByRole(AriaRole.Button, new() { Name = "Change" }).ClickAsync();
        await Expect(page.Locator(".wiki-live-search")).ToBeVisibleAsync();
        errors.Should().BeEmpty();
    }
}
