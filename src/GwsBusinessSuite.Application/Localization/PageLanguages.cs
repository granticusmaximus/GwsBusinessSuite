namespace GwsBusinessSuite.Application.Localization;

// The languages a public page is available in: the original plus its published translations.
// Translations share the page's address with ?lang={code} (see the public content routes), so
// this produces the hreflang alternate links search engines use to pair them, the <html lang>
// value, and a small language switcher for visitors. Built only when a page has at least one
// published translation.
public sealed class PageLanguages
{
    // The language every page is written in before translation.
    public const string SourceLanguage = "en";

    private PageLanguages(string current, string absoluteUrl, string relativePath, IReadOnlyList<string> codes)
    {
        Current = current;
        AbsoluteUrl = absoluteUrl;
        RelativePath = relativePath;
        Codes = codes;
    }

    // The language being shown (SourceLanguage when no translation applies).
    public string Current { get; }
    // The page's own address without ?lang, absolute (hreflang needs full URLs) and as a path.
    public string AbsoluteUrl { get; }
    public string RelativePath { get; }
    // SourceLanguage first, then each published translation.
    public IReadOnlyList<string> Codes { get; }

    public bool IsTranslated => Current != SourceLanguage;

    // requested: the ?lang value; it only applies when that translation is published.
    public static PageLanguages? Create(string absoluteUrl, string relativePath, IEnumerable<string> publishedCodes, string? requested)
    {
        var codes = publishedCodes
            .Select(code => code.Trim().ToLowerInvariant())
            .Where(code => code.Length > 0 && code != SourceLanguage)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0) return null;
        var wanted = requested?.Trim().ToLowerInvariant();
        var current = wanted is not null && codes.Contains(wanted) ? wanted : SourceLanguage;
        return new PageLanguages(current, absoluteUrl, relativePath, [SourceLanguage, .. codes]);
    }

    public string AbsoluteUrlFor(string code) => code == SourceLanguage ? AbsoluteUrl : $"{AbsoluteUrl}?lang={Uri.EscapeDataString(code)}";

    public string RelativeUrlFor(string code) => code == SourceLanguage ? RelativePath : $"{RelativePath}?lang={Uri.EscapeDataString(code)}";

    // <link rel="alternate" hreflang=...> for every language, plus x-default (the original).
    public string HeadLinks() =>
        string.Concat(Codes.Select(code => $"<link rel=\"alternate\" hreflang=\"{Attr(code)}\" href=\"{Attr(AbsoluteUrlFor(code))}\" />"))
        + $"<link rel=\"alternate\" hreflang=\"x-default\" href=\"{Attr(AbsoluteUrl)}\" />";

    // Each language named in itself ("Español"), the current one marked.
    public string Switcher() =>
        "<nav class=\"gws-lang-switcher\" aria-label=\"Language\"><i class=\"bi bi-translate\" aria-hidden=\"true\"></i>"
        + string.Concat(Codes.Select(code =>
            $"<a href=\"{Attr(RelativeUrlFor(code))}\" hreflang=\"{Attr(code)}\" lang=\"{Attr(code)}\"{(code == Current ? " aria-current=\"true\"" : string.Empty)}>{Attr(NativeName(code))}</a>"))
        + "</nav>";

    // A fixed list rather than CultureInfo, so the names don't depend on the server's ICU data.
    public static string NativeName(string code) => code.ToLowerInvariant() switch
    {
        "en" => "English", "es" => "Español", "fr" => "Français", "de" => "Deutsch", "it" => "Italiano",
        "pt" => "Português", "pt-br" => "Português (Brasil)", "nl" => "Nederlands", "sv" => "Svenska",
        "da" => "Dansk", "no" or "nb" => "Norsk", "fi" => "Suomi", "pl" => "Polski", "cs" => "Čeština",
        "tr" => "Türkçe", "el" => "Ελληνικά", "ru" => "Русский", "uk" => "Українська", "ar" => "العربية",
        "he" => "עברית", "hi" => "हिन्दी", "zh" or "zh-cn" => "中文", "zh-tw" => "中文（台灣）", "ja" => "日本語",
        "ko" => "한국어", "vi" => "Tiếng Việt", "th" => "ไทย", "id" => "Bahasa Indonesia", "ms" => "Bahasa Melayu",
        _ => code.ToUpperInvariant()
    };

    // Only the characters HTML needs escaped, so names like "Español" stay readable in the UTF-8 page.
    private static string Attr(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
