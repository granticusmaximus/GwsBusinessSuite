using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelTemplateService(
    IAppDbContext dbContext,
    IWikiService wikiService,
    IWikiDatabaseService wikiDatabaseService)
    : ISentinelTemplateService
{
    public async Task<IReadOnlyList<SentinelPageTemplateView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await dbContext.SentinelPageTemplates
            .AsNoTracking()
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);

        return templates.Select(ToView).ToList();
    }

    public async Task<SentinelPageTemplateView> CreateFromPageAsync(
        Guid wikiPageId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var page = await dbContext.WikiPages
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel page no longer exists.");

        if (await dbContext.SentinelPageTemplates.AnyAsync(
                template => template.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException("A Sentinel template with that name already exists.");
        }

        var template = new SentinelPageTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            PageTitle = page.Title,
            BlocksJson = page.BlocksJson,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            CreatedBy = NormalizeUser(performedBy)
        };

        await dbContext.SentinelPageTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(template);
    }

    public async Task<WikiPage> CreatePageAsync(
        Guid templateId,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelPageTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel template no longer exists.");

        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson)
            .Select(block => block with { Id = Guid.NewGuid() })
            .ToList();

        return await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = template.PageTitle,
            BlocksJson = WikiBlockJson.Serialize(blocks),
            Icon = template.Icon,
            CoverImageUrl = template.CoverImageUrl,
            ParentWikiPageId = parentWikiPageId
        }, NormalizeUser(performedBy), cancellationToken);
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelPageTemplates
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null) return;

        dbContext.SentinelPageTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelBlockTemplateView>> ListBlockTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await dbContext.SentinelBlockTemplates
            .AsNoTracking()
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);
        return templates.Select(ToBlockView).ToList();
    }

    public async Task<SentinelBlockTemplateView> CreateBlockTemplateAsync(
        string name,
        string blocksJson,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var blocks = WikiBlockJson.ParseBlocks(blocksJson);
        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("A block template must contain at least one block.");
        }
        if (await dbContext.SentinelBlockTemplates.AnyAsync(
                template => template.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException("A Sentinel block template with that name already exists.");
        }

        var template = new SentinelBlockTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            BlocksJson = WikiBlockJson.Serialize(blocks),
            CreatedBy = NormalizeUser(performedBy)
        };
        await dbContext.SentinelBlockTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToBlockView(template);
    }

    public async Task<string> MaterializeBlockTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelBlockTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel block template no longer exists.");
        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson)
            .Select(block => block with { Id = Guid.NewGuid() })
            .ToList();
        return WikiBlockJson.Serialize(blocks);
    }

    public async Task DeleteBlockTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelBlockTemplates
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null) return;
        dbContext.SentinelBlockTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelDatabaseTemplateView>> ListDatabaseTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await dbContext.SentinelDatabaseTemplates
            .AsNoTracking()
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);
        return templates.Select(ToDatabaseView).ToList();
    }

    public async Task<SentinelDatabaseTemplateView> CreateFromDatabaseAsync(
        Guid wikiDatabaseId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        if (await dbContext.SentinelDatabaseTemplates.AnyAsync(
                template => template.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException("A Sentinel database template with that name already exists.");
        }

        var snapshot = await wikiDatabaseService.CreateTemplateSnapshotAsync(wikiDatabaseId, cancellationToken);
        var template = new SentinelDatabaseTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            DatabaseTitle = snapshot.Title,
            Icon = snapshot.Icon,
            SnapshotJson = JsonSerializer.Serialize(snapshot, WikiPropertyValues.Options),
            CreatedBy = NormalizeUser(performedBy)
        };
        await dbContext.SentinelDatabaseTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDatabaseView(template);
    }

    public async Task<WikiDatabase> CreateDatabaseAsync(
        Guid templateId,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelDatabaseTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel database template no longer exists.");
        var snapshot = JsonSerializer.Deserialize<WikiDatabaseTemplateSnapshot>(
                template.SnapshotJson, WikiPropertyValues.Options)
            ?? throw new InvalidOperationException("The Sentinel database template snapshot is invalid.");
        return await wikiDatabaseService.CreateDatabaseFromTemplateAsync(
            snapshot, parentWikiPageId, NormalizeUser(performedBy), cancellationToken);
    }

    public async Task DeleteDatabaseTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelDatabaseTemplates
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null) return;
        dbContext.SentinelDatabaseTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SentinelNotionTemplateImportResult> ImportNotionExportAsync(
        byte[] zipArchive,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zipArchive);
        if (zipArchive.Length == 0)
        {
            throw new InvalidOperationException("Choose a Notion ZIP export to import.");
        }
        if (zipArchive.Length > 25 * 1024 * 1024)
        {
            throw new InvalidOperationException("Notion template exports cannot exceed 25 MB.");
        }

        using var archiveStream = new MemoryStream(zipArchive, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 500)
        {
            throw new InvalidOperationException("This export contains too many files. The limit is 500.");
        }

        var totalExpandedBytes = archive.Entries.Sum(entry => entry.Length);
        if (totalExpandedBytes > 100 * 1024 * 1024)
        {
            throw new InvalidOperationException("The expanded Notion export cannot exceed 100 MB.");
        }

        var existingNames = (await dbContext.SentinelPageTemplates.AsNoTracking()
                .Select(template => template.NormalizedName)
                .Concat(dbContext.SentinelDatabaseTemplates.AsNoTracking()
                    .Select(template => template.NormalizedName))
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var pageCount = 0;
        var databaseCount = 0;
        var skipped = 0;
        var warnings = new List<string>();
        var createdBy = NormalizeUser(performedBy);

        foreach (var entry in archive.Entries
                     .Where(entry => entry.Length > 0 && !entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal))
                     .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not ".md" and not ".markdown" and not ".html" and not ".htm" and not ".csv")
            {
                skipped++;
                continue;
            }

            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (entry.Name.Contains("no access", StringComparison.OrdinalIgnoreCase)
                || content.Trim().Equals("No access", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{entry.FullName}: restricted content was not imported.");
                skipped++;
                continue;
            }

            var title = NotionExportTitle(entry.Name);
            var templateName = UniqueTemplateName(title, existingNames);
            if (extension == ".csv")
            {
                var snapshot = CreateCsvSnapshot(title, content, warnings, entry.FullName);
                if (snapshot is null)
                {
                    skipped++;
                    continue;
                }

                await dbContext.SentinelDatabaseTemplates.AddAsync(new SentinelDatabaseTemplate
                {
                    Name = templateName,
                    NormalizedName = NormalizeName(templateName),
                    DatabaseTitle = title,
                    Icon = "🗄️",
                    SnapshotJson = JsonSerializer.Serialize(snapshot, WikiPropertyValues.Options),
                    CreatedBy = createdBy
                }, cancellationToken);
                databaseCount++;
                continue;
            }

            var markdown = extension is ".html" or ".htm" ? HtmlToPlainText(content) : content;
            await dbContext.SentinelPageTemplates.AddAsync(new SentinelPageTemplate
            {
                Name = templateName,
                NormalizedName = NormalizeName(templateName),
                PageTitle = title,
                BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromLegacyMarkdown(markdown)),
                Icon = "📄",
                CreatedBy = createdBy
            }, cancellationToken);
            pageCount++;
        }

        if (pageCount == 0 && databaseCount == 0)
        {
            throw new InvalidOperationException(
                "No supported Notion pages or databases were found. Export the template as Markdown & CSV or HTML.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new SentinelNotionTemplateImportResult(pageCount, databaseCount, skipped, warnings);
    }

    private static SentinelPageTemplateView ToView(SentinelPageTemplate template) => new(
        template.Id,
        template.Name,
        template.PageTitle,
        template.Icon,
        WikiBlockJson.ParseBlocks(template.BlocksJson).Count,
        template.CreatedAt,
        template.CreatedBy);

    private static SentinelDatabaseTemplateView ToDatabaseView(SentinelDatabaseTemplate template)
    {
        var snapshot = JsonSerializer.Deserialize<WikiDatabaseTemplateSnapshot>(
                template.SnapshotJson, WikiPropertyValues.Options)
            ?? new WikiDatabaseTemplateSnapshot(template.DatabaseTitle, template.Icon, [], [], []);
        return new SentinelDatabaseTemplateView(
            template.Id,
            template.Name,
            template.DatabaseTitle,
            template.Icon,
            snapshot.Properties.Count,
            snapshot.Rows.Count,
            snapshot.Views.Count,
            template.CreatedAt,
            template.CreatedBy);
    }

    private static SentinelBlockTemplateView ToBlockView(SentinelBlockTemplate template)
    {
        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson);
        var preview = string.Join(" · ", blocks
            .Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 48))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(2));
        return new SentinelBlockTemplateView(
            template.Id,
            template.Name,
            blocks.Count,
            string.IsNullOrWhiteSpace(preview) ? "Structured blocks" : preview,
            template.CreatedAt,
            template.CreatedBy);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A template name is required.", nameof(name));
        }
        if (trimmed.Length > 120)
        {
            throw new ArgumentException("Template names cannot exceed 120 characters.", nameof(name));
        }

        return trimmed.ToUpperInvariant();
    }

    private static string NormalizeUser(string performedBy) =>
        string.IsNullOrWhiteSpace(performedBy) ? "unknown" : performedBy.Trim().ToLowerInvariant();

    private static string NotionExportTitle(string fileName)
    {
        var title = Path.GetFileNameWithoutExtension(fileName);
        title = Regex.Replace(title, @"\s+[0-9a-f]{32}$", string.Empty, RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(title) ? "Imported Notion template" : title.Trim();
    }

    private static string UniqueTemplateName(string requestedName, ISet<string> existingNames)
    {
        var baseName = requestedName.Length <= 120 ? requestedName : requestedName[..120];
        var candidate = baseName;
        var suffix = 2;
        while (!existingNames.Add(candidate.ToUpperInvariant()))
        {
            var suffixText = $" ({suffix++})";
            candidate = $"{baseName[..Math.Min(baseName.Length, 120 - suffixText.Length)]}{suffixText}";
        }
        return candidate;
    }

    private static string HtmlToPlainText(string html)
    {
        var withBreaks = Regex.Replace(html, @"<(br|/p|/div|/li|/h[1-6])\b[^>]*>", "\n",
            RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(withBreaks, "<[^>]+>", string.Empty))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static WikiDatabaseTemplateSnapshot? CreateCsvSnapshot(
        string title,
        string csv,
        ICollection<string> warnings,
        string sourceName)
    {
        var records = ParseCsv(csv);
        if (records.Count == 0 || records[0].Count == 0)
        {
            warnings.Add($"{sourceName}: empty CSV database was skipped.");
            return null;
        }

        var headers = records[0].Select((value, index) =>
            string.IsNullOrWhiteSpace(value) ? $"Column {index + 1}" : value.Trim()).ToList();
        var properties = headers.Select((header, index) => new WikiDatabaseTemplateProperty(
            Guid.NewGuid(),
            header,
            index == 0 ? WikiDatabasePropertyTypes.Title : WikiDatabasePropertyTypes.Text,
            index,
            "{}")).ToList();
        var rows = new List<WikiDatabaseTemplateRow>();
        for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var values = new JsonObject();
            for (var columnIndex = 0; columnIndex < properties.Count; columnIndex++)
            {
                WikiPropertyValues.SetText(
                    values,
                    properties[columnIndex].Id,
                    columnIndex < records[rowIndex].Count ? records[rowIndex][columnIndex] : string.Empty);
            }
            rows.Add(new WikiDatabaseTemplateRow(
                Guid.NewGuid(), rowIndex - 1, WikiPropertyValues.Serialize(values), "[]"));
        }

        return new WikiDatabaseTemplateSnapshot(
            title,
            "🗄️",
            properties,
            rows,
            [new WikiDatabaseTemplateView(
                Guid.NewGuid(), "Table", WikiDatabaseViewTypes.Table, 0,
                WikiDatabaseViewConfigJson.Serialize(WikiDatabaseViewConfig.Empty))]);
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '"')
            {
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                record.Add(field.ToString());
                field.Clear();
                if (record.Any(value => !string.IsNullOrWhiteSpace(value))) records.Add(record);
                record = [];
            }
            else
            {
                field.Append(character);
            }
        }
        record.Add(field.ToString());
        if (record.Any(value => !string.IsNullOrWhiteSpace(value))) records.Add(record);
        return records;
    }
}
