using QuestPDF.Infrastructure;

namespace GwsBusinessSuite.Infrastructure.Services;

// QuestPDF's settings in one place, for every PDF the app makes (résumé, invoices, incident
// reports, Sentinel exports). QuestPDF 2026.9 stopped using installed fonts by default and throws
// when a font family isn't found. These PDFs use the Liberation fonts the production container
// installs (fonts-liberation in the Dockerfile), so installed fonts stay on, and a missing family
// (CI, a Mac without Liberation) falls back to another font as it always did, instead of failing.
public static class QuestPdfSetup
{
    public static void Apply()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }
}
