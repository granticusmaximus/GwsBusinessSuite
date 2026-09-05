using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Click-to-type editing on the canvas. Rich (Markdown-backed) props post innerHTML up to the
// parent, which converts it back to Markdown with professionalEditor.js's serializer; plain props
// still post innerText. A prop whose Markdown uses something that serializer cannot represent is
// deliberately left non-editable so inline editing can never silently flatten it.
[Collection("Playwright")]
public sealed class CmsCanvasInlineEditingBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string Page(PageLayout layout)
    {
        var body = CmsBlockHtmlRenderer.Render(layout, "site", "page", editMode: true);
        return "<!doctype html><html><body>" + body + CmsBlockHtmlRenderer.BuildEditModeScript() + "</body></html>";
    }

    private static PageLayout LayoutWith(params LayoutWidget[] widgets) => new()
    {
        Sections = [ new LayoutSection { Id = "s1", Columns = [ new LayoutColumn { Widgets = [.. widgets] } ] } ]
    };

    private async Task<IPage> OpenAsync(PageLayout layout)
    {
        var page = await fixture.Browser.NewPageAsync();
        var html = Page(layout);
        await page.RouteAsync("http://localhost/**", r => r.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = html
        }));
        await page.GotoAsync("http://localhost/canvas");
        // Capture what the canvas posts to the parent so the assertions see the real payload.
        await page.EvaluateAsync(
            "() => { window.__msgs = []; const p = window.parent.postMessage.bind(window.parent);" +
            " window.parent.postMessage = (m, o) => { window.__msgs.push(m); return p(m, o); }; }");
        return page;
    }

    [Fact]
    public async Task AParagraph_ShouldBeEditableInPlace_AndPostItsHtml()
    {
        await using var page = await OpenAsync(LayoutWith(new LayoutWidget
        {
            Id = "w1", WidgetType = "paragraph", Props = { ["text"] = "Original copy" }
        }));

        var editable = await page.QuerySelectorAsync("[data-gws-inline-prop='text'][data-gws-inline-rich]");
        editable.Should().NotBeNull("a paragraph is prose and must be click-to-type");

        await page.ClickAsync("[data-gws-inline-rich]");
        await page.EvaluateAsync(
            "() => { const el = document.querySelector('[data-gws-inline-rich]');" +
            " el.innerHTML = '<p>Rewritten <strong>bold</strong></p>'; el.blur(); }");

        var msg = await page.EvaluateAsync<string>(
            "() => { const m = window.__msgs.filter(x => x && x.type === 'cms:edit').pop(); return m ? m.html : ''; }");
        msg.Should().Contain("Rewritten").And.Contain("<strong>");
        var rich = await page.EvaluateAsync<bool>(
            "() => { const m = window.__msgs.filter(x => x && x.type === 'cms:edit').pop(); return !!(m && m.rich); }");
        rich.Should().BeTrue("the parent needs to know to convert this back to Markdown");
    }

    [Fact]
    public async Task AccordionQuestionAndAnswer_ShouldBeEditable_WithIndexedPropPaths()
    {
        await using var page = await OpenAsync(LayoutWith(new LayoutWidget
        {
            Id = "w1", WidgetType = "accordion",
            Props = { ["itemsJson"] = """[{"question":"How much?","answer":"It depends."}]""" }
        }));

        (await page.QuerySelectorAsync("[data-gws-inline-prop='itemsJson[0].question']"))
            .Should().NotBeNull("an accordion question is prose the visitor reads");
        (await page.QuerySelectorAsync("[data-gws-inline-prop='itemsJson[0].answer']"))
            .Should().NotBeNull("an accordion answer is prose too");
    }

    [Fact]
    public async Task FormFieldLabels_ShouldBeEditable_ButNotFieldConfiguration()
    {
        await using var page = await OpenAsync(LayoutWith(new LayoutWidget
        {
            Id = "w1", WidgetType = "form",
            Props = { ["fieldsJson"] = """[{"key":"email","label":"Your email","type":"email","required":true}]""" }
        }));

        (await page.QuerySelectorAsync("[data-gws-inline-prop='fieldsJson[0].label']"))
            .Should().NotBeNull("a field label is visitor-facing prose");
        (await page.QuerySelectorAsync("input[contenteditable]"))
            .Should().BeNull("the control itself is configuration, not text to type over");
    }

    [Fact]
    public async Task FormLabelPaths_ShouldIndexTheStoredArray_NotTheRenderedFields()
    {
        // Keyless fields are dropped from the rendered form but still occupy a slot in the stored
        // array. Numbering the rendered fields would make the second visible label write to
        // fieldsJson[1] - the keyless entry - and silently edit the wrong field.
        const string storedFields =
            "[{\"key\":\"name\",\"label\":\"Name\",\"type\":\"text\"},"
            + "{\"label\":\"Dropped - no key\",\"type\":\"text\"},"
            + "{\"key\":\"email\",\"label\":\"Email\",\"type\":\"email\"}]";

        await using var page = await OpenAsync(LayoutWith(new LayoutWidget
        {
            Id = "w1", WidgetType = "form", Props = { ["fieldsJson"] = storedFields }
        }));

        var paths = await page.EvalOnSelectorAllAsync<string[]>(
            "[data-gws-inline-prop^='fieldsJson']",
            "els => els.map(e => e.getAttribute('data-gws-inline-prop'))");
        paths.Should().BeEquivalentTo(["fieldsJson[0].label", "fieldsJson[2].label"],
            "the second rendered label is the third stored field");
    }

    [Fact]
    public async Task ContentTheSerializerCannotRepresent_ShouldNotBecomeEditable()
    {
        // A Markdown table cannot survive the HTML->Markdown round trip, so this paragraph stays
        // click-to-select and is edited in the Inspector instead of being silently flattened.
        await using var page = await OpenAsync(LayoutWith(new LayoutWidget
        {
            Id = "w1", WidgetType = "paragraph",
            Props = { ["text"] = "| Plan | Price |\n| --- | --- |\n| Pro | $10 |" }
        }));

        (await page.QuerySelectorAsync("[data-gws-inline-rich]"))
            .Should().BeNull("a table must not become inline-editable");
    }

    [Fact]
    public async Task SelectingRichText_ShouldOpenTheFormattingToolbar()
    {
        await using var page = await OpenAsync(LayoutWith(new LayoutWidget
        {
            Id = "w1", WidgetType = "paragraph", Props = { ["text"] = "Select some of this text" }
        }));

        await page.EvaluateAsync(
            "() => { const el = document.querySelector('[data-gws-inline-rich]');" +
            " el.focus(); const r = document.createRange(); r.selectNodeContents(el);" +
            " const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);" +
            " document.dispatchEvent(new Event('selectionchange')); }");

        await page.WaitForSelectorAsync(".gws-format-bar.is-open", new() { Timeout = 3000 });
        var labels = await page.EvalOnSelectorAllAsync<string[]>(
            ".gws-format-bar button", "els => els.map(e => e.getAttribute('data-gws-format'))");
        labels.Should().BeEquivalentTo(["bold", "italic", "link"],
            "the toolbar only offers what the Markdown serializer can carry back");
    }
}
