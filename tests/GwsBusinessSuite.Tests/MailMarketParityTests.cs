using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class MailMarketParityTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void MailUi_ShouldKeepSignatureAttachmentReminderAndResponsiveFiltersWired()
    {
        var razor = File.ReadAllText(Path.Combine(Root, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Mail.razor"));
        var css = File.ReadAllText(Path.Combine(Root, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/Mail.razor.css"));

        razor.Should().Contain("SaveSignatureAsync");
        razor.Should().Contain("MailComposeGuard.MentionsAttachment");
        razor.Should().Contain("ConfirmSendWithoutAttachmentAsync");
        razor.Should().Contain("aria-label=\"Filter messages\"");
        razor.Should().Contain("FilteredMessages");
        css.Should().Contain(".mail-filters");
        css.Should().Contain("@container mail-list (max-width: 520px)");
        css.Should().Contain(".mail-folders > *");
        css.Should().Contain("flex: 0 0 auto");
        css.Should().Contain("overflow-y: hidden");
        css.Should().Contain("grid-template-columns: repeat(5, minmax(0, 1fr))");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GwsBusinessSuite.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
