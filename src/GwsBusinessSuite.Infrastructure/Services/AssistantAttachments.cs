using System.Text;
using UglyToad.PdfPig;

namespace GwsBusinessSuite.Infrastructure.Services;

// Files attached to a SentinelGPT question in the web panel (PWA phase 3, 07-11-26): where the Mac
// app attaches a folder, a phone or tablet attaches files. Text files are read as they are and
// PDFs for their text; anything else is turned away. What the agent gets is capped
// (SentinelAgentRunner.AttachmentCharacters), and none of it is kept.
public static class AssistantAttachments
{
    public const int MaxFiles = 3;
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxCharactersPerFile = 8_000;

    public static readonly IReadOnlyList<string> TextExtensions =
    [
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".log", ".xml", ".yaml", ".yml", ".html", ".htm",
        ".cs", ".razor", ".js", ".ts", ".py", ".sql", ".css", ".ini", ".toml", ".sh", ".ics", ".vcf"
    ];

    // For the file picker's accept attribute.
    public static string Accept => string.Join(',', TextExtensions.Append(".pdf"));

    public sealed record Attachment(string Name, string Text, bool Cut);

    // The file's text, or why it can't be used.
    public static (Attachment? File, string? Problem) Read(string name, byte[] content)
    {
        var shown = Path.GetFileName(name);
        var extension = Path.GetExtension(shown).ToLowerInvariant();
        if (content.LongLength > MaxBytes) return (null, $"{shown} is over 5 MB.");
        string text;
        if (extension == ".pdf")
        {
            try
            {
                using var document = PdfDocument.Open(content);
                var pages = new StringBuilder();
                foreach (var page in document.GetPages())
                {
                    pages.AppendLine(string.Join(' ', page.GetWords().Select(word => word.Text)));
                    if (pages.Length > MaxCharactersPerFile) break;
                }
                text = pages.ToString();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (null, $"{shown} couldn't be read as a PDF.");
            }
            if (string.IsNullOrWhiteSpace(text)) return (null, $"{shown} has no text to read (a scan?).");
        }
        else if (TextExtensions.Contains(extension))
        {
            text = Encoding.UTF8.GetString(content).TrimStart('﻿');
            // A binary file with a text name.
            if (text.Count(c => c == '\0') > text.Length / 100) return (null, $"{shown} isn't text.");
        }
        else
        {
            return (null, $"{shown}: attach text files or PDFs.");
        }

        text = text.Replace("\r\n", "\n").Trim();
        var cut = text.Length > MaxCharactersPerFile;
        return (new Attachment(shown, cut ? text[..MaxCharactersPerFile] : text, cut), null);
    }

    // The files as the agent reads them.
    public static string? Format(IReadOnlyCollection<Attachment> files)
    {
        if (files.Count == 0) return null;
        var text = new StringBuilder();
        foreach (var file in files)
        {
            text.AppendLine($"--- {file.Name}{(file.Cut ? " (only its start: it's long)" : string.Empty)} ---");
            text.AppendLine(file.Text);
        }
        return text.ToString();
    }
}
