using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

// The app's Content-Security-Policy is `script-src 'self' https://cdn.jsdelivr.net` - it does not
// allow 'unsafe-inline'. The canvas edit-mode behaviour was an inline <script> for a long time,
// which meant the browser silently refused to run it in every deployed environment: the preview
// rendered, the markup looked right, and nothing was clickable. Nothing caught it because tests
// that stub the HTML response serve it without the real CSP header.
public sealed class CmsEditModeScriptCspTests
{
    [Fact]
    public void BuildEditModeScript_ShouldNotEmitAnInlineScriptBlock()
    {
        var markup = CmsBlockHtmlRenderer.BuildEditModeScript();

        markup.Should().NotContain("<script>",
            "an inline script is blocked by script-src 'self' and the canvas silently stops working");
        markup.Should().Contain("<script src=\"/js/cms-edit-mode.js\"",
            "the behaviour has to load from a same-origin file to satisfy the policy");
    }

    [Fact]
    public void BuildEditModeScript_MayStillInlineItsStyles()
    {
        // style-src does allow 'unsafe-inline', so the edit-mode CSS is fine where it is - this
        // records that the rule above is specifically about scripts, not a blanket ban.
        CmsBlockHtmlRenderer.BuildEditModeScript().Should().Contain("<style>");
    }
}
