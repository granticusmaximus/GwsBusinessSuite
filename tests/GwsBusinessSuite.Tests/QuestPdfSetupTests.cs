using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// QuestPDF 2026.9 turned installed fonts off by default and throws on a missing family. Every PDF
// here relies on the production container's installed Liberation fonts, so the dependency update
// (31-10-26) would have broken them all in production; QuestPdfSetup keeps the old behaviour.
public sealed class QuestPdfSetupTests
{
    [Fact]
    public void Apply_ShouldUseInstalledFonts_AndFallBackInsteadOfFailingOnAMissingOne()
    {
        QuestPdfSetup.Apply();

        QuestPDF.Settings.UseSystemFonts.Should().BeTrue("production's Liberation fonts are installed, not shipped with the app");
        QuestPDF.Settings.ThrowOnMissingFontFamilies.Should().BeFalse("a machine without them falls back, as before 2026.9");
        QuestPDF.Settings.License.Should().Be(QuestPDF.Infrastructure.LicenseType.Community);
    }
}
