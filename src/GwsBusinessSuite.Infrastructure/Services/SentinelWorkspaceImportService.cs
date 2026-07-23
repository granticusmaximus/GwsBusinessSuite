using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

/// <summary>
/// Restores a Notion Markdown/CSV or HTML workspace export into real Sentinel workspace
/// records. This is deliberately separate from SentinelTemplateService: an archive restore
/// reconciles editable pages/databases, while the template importer creates reusable copies.
/// </summary>
public sealed class SentinelWorkspaceImportService(IAppDbContext dbContext)
    : ISentinelWorkspaceImportService
{
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const long MaxDocumentBytes = 20L * 1024 * 1024;
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;
    private const int MaxEntries = 20_000;

    private static readonly Regex ExportIdPattern = new(
        @"(?:^|\s)(?<id>[0-9a-f]{32})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MarkdownLinkPattern = new(
        @"(?<image>!)?\[(?<label>[^\]]*)\]\((?<target>[^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex HtmlImagePattern = new(
        @"<img\b[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> DocumentExtensions =
        [".md", ".markdown", ".html", ".htm", ".csv"];

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".json"] = "application/json",
            [".csv"] = "text/csv",
            [".mp3"] = "audio/mpeg",
            [".m4a"] = "audio/mp4",
            [".wav"] = "audio/wav",
            [".mp4"] = "video/mp4",
            [".mov"] = "video/quicktime",
            [".zip"] = "application/zip",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

    public async Task<SentinelNotionWorkspaceImportResult> ImportAsync(
        byte[] zipArchive,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zipArchive);
        if (zipArchive.Length == 0)
        {
            throw new InvalidOperationException("Choose a Notion workspace ZIP export.");
        }
        if (zipArchive.LongLength > SentinelNotionWorkspaceImportLimits.MaxArchiveBytes)
        {
            throw new InvalidOperationException(
                $"The workspace ZIP exceeds the {SentinelNotionWorkspaceImportLimits.MaxArchiveBytes / 1024 / 1024} MB upload limit. Export smaller top-level page batches.");
        }

        var actor = NormalizeUser(performedBy);
        var warnings = new List<string>();
        using var archiveStream = new MemoryStream(zipArchive, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidOperationException($"The workspace export exceeds the {MaxEntries:N0}-file safety limit.");
        }

        var expandedBytes = archive.Entries.Aggregate(
            0L,
            (total, entry) => checked(total + entry.Length));
        if (expandedBytes > MaxExpandedBytes)
        {
            throw new InvalidOperationException(
                $"The expanded workspace exceeds the {MaxExpandedBytes / 1024 / 1024} MB safety limit. Import it in top-level page batches.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(item => item.Length > 0))
        {
            var path = NormalizeArchivePath(entry.FullName);
            if (path is null || path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{entry.FullName}: unsafe or metadata-only archive entry was skipped.");
                continue;
            }
            if (!entries.TryAdd(path, entry))
            {
                warnings.Add($"{path}: duplicate archive entry was skipped.");
            }
        }

        var documents = new List<ArchiveDocument>();
        foreach (var (path, entry) in entries.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!DocumentExtensions.Contains(extension)
                || IsSitemap(path, extension))
            {
                continue;
            }
            if (entry.Length > MaxDocumentBytes)
            {
                warnings.Add($"{path}: document exceeds 20 MB and was skipped.");
                continue;
            }

            documents.Add(new ArchiveDocument(
                path,
                RemoveExtension(path),
                DirectoryName(path),
                extension,
                ExtractTitle(path),
                ExtractExportId(path),
                await ReadTextAsync(entry, cancellationToken)));
        }

        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                "No Notion Markdown, HTML, or CSV documents were found in this ZIP.");
        }

        var documentsByStem = documents
            .GroupBy(document => document.StemPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            document.Parent = FindParentDocument(document.DirectoryPath, documentsByStem);
            document.Kind = document.Extension == ".csv"
                ? ArchiveDocumentKind.Database
                : document.Parent?.Kind == ArchiveDocumentKind.Database
                    ? ArchiveDocumentKind.DatabaseRow
                    : ArchiveDocumentKind.Page;
        }

        // A row is recognized after its database parent. Propagate that classification in a
        // second pass so the archive's lexical order cannot affect the result.
        foreach (var document in documents.Where(item => item.Extension != ".csv"))
        {
            if (document.Parent?.Extension == ".csv")
            {
                document.Kind = ArchiveDocumentKind.DatabaseRow;
            }
        }
        var documentTitlesByPath = documents
            .GroupBy(document => document.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Title,
                StringComparer.OrdinalIgnoreCase);

        var transaction = await dbContext.BeginTransactionAsync(cancellationToken);
        try
        {
            var attachmentResult = await ImportAttachmentsAsync(
                entries,
                documents.Select(document => document.EntryPath).ToHashSet(StringComparer.OrdinalIgnoreCase),
                actor,
                warnings,
                cancellationToken);

            var pagesCreated = 0;
            var pagesUpdated = 0;
            var databasesCreated = 0;
            var databasesUpdated = 0;
            var databaseRowsImported = 0;
            var now = DateTimeOffset.UtcNow;

            var existingPages = await dbContext.WikiPages.ToListAsync(cancellationToken);
            var pagesByExportId = existingPages
                .Where(page => !string.IsNullOrWhiteSpace(page.NotionExportId))
                .GroupBy(page => page.NotionExportId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var pagesByNotionId = existingPages
                .Where(page => !string.IsNullOrWhiteSpace(page.NotionId))
                .GroupBy(page => NormalizeNotionId(page.NotionId!), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var reservedSlugs = existingPages.Select(page => page.Slug)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pageByDocument = new Dictionary<ArchiveDocument, WikiPage>();

            foreach (var document in documents.Where(item => item.Kind == ArchiveDocumentKind.Page))
            {
                var blocksJson = ConvertDocumentToBlocks(
                    document,
                    attachmentResult.UrlsByPath,
                    documentTitlesByPath,
                    warnings);
                var page = FindExisting(document.ExportId, pagesByExportId, pagesByNotionId);
                if (page is null)
                {
                    page = new WikiPage
                    {
                        Title = document.Title,
                        Slug = ReserveSlug(document.Title, reservedSlugs),
                        BlocksJson = blocksJson,
                        ContentVersion = 1,
                        NotionExportId = document.ExportId,
                        CreatedAt = now,
                        CreatedBy = actor,
                        UpdatedAt = now,
                        UpdatedBy = actor
                    };
                    await dbContext.WikiPages.AddAsync(page, cancellationToken);
                    pagesByExportId[document.ExportId] = page;
                    pagesCreated++;
                }
                else
                {
                    if (!string.Equals(page.Title, document.Title, StringComparison.Ordinal)
                        || !string.Equals(page.BlocksJson, blocksJson, StringComparison.Ordinal))
                    {
                        await AddPreImportRevisionAsync(page, actor, cancellationToken);
                        page.Title = document.Title;
                        page.BlocksJson = blocksJson;
                        page.ContentVersion++;
                    }
                    page.NotionExportId = document.ExportId;
                    page.NotionArchivedAt = null;
                    page.UpdatedAt = now;
                    page.UpdatedBy = actor;
                    pagesUpdated++;
                }
                pageByDocument[document] = page;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            var existingDatabases = await dbContext.WikiDatabases
                .Include(database => database.Properties)
                .Include(database => database.Rows)
                .Include(database => database.Views)
                .ToListAsync(cancellationToken);
            var databasesByExportId = existingDatabases
                .Where(database => !string.IsNullOrWhiteSpace(database.NotionExportId))
                .GroupBy(database => database.NotionExportId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var databasesByNotionId = existingDatabases
                .Where(database => !string.IsNullOrWhiteSpace(database.NotionId))
                .GroupBy(database => NormalizeNotionId(database.NotionId!), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var databaseByDocument = new Dictionary<ArchiveDocument, WikiDatabase>();

            foreach (var document in documents.Where(item => item.Kind == ArchiveDocumentKind.Database))
            {
                var database = FindExisting(document.ExportId, databasesByExportId, databasesByNotionId);
                if (database is null)
                {
                    database = new WikiDatabase
                    {
                        Title = document.Title,
                        Icon = "🗄️",
                        NotionExportId = document.ExportId,
                        CreatedAt = now,
                        CreatedBy = actor,
                        UpdatedAt = now,
                        UpdatedBy = actor
                    };
                    await dbContext.WikiDatabases.AddAsync(database, cancellationToken);
                    databasesByExportId[document.ExportId] = database;
                    databasesCreated++;
                }
                else
                {
                    database.Title = document.Title;
                    database.NotionExportId = document.ExportId;
                    database.NotionArchivedAt = null;
                    database.UpdatedAt = now;
                    database.UpdatedBy = actor;
                    databasesUpdated++;
                }

                databaseRowsImported += ReconcileDatabase(
                    database,
                    document,
                    documents.Where(item => item.Kind == ArchiveDocumentKind.DatabaseRow
                        && ReferenceEquals(item.Parent, document)).ToList(),
                    attachmentResult.UrlsByPath,
                    documentTitlesByPath,
                    actor,
                    now,
                    warnings);
                databaseByDocument[document] = database;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            // Wire hierarchy only after every page/database has a durable local identity.
            foreach (var (document, page) in pageByDocument)
            {
                page.ParentWikiPageId = document.Parent is { } parent
                    && pageByDocument.TryGetValue(parent, out var parentPage)
                        ? parentPage.Id
                        : null;
            }
            foreach (var (document, database) in databaseByDocument)
            {
                database.ParentWikiPageId = document.Parent is { } parent
                    && pageByDocument.TryGetValue(parent, out var parentPage)
                        ? parentPage.Id
                        : null;
            }
            AssignSortOrders(pageByDocument, databaseByDocument);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new SentinelNotionWorkspaceImportResult(
                pagesCreated,
                pagesUpdated,
                databasesCreated,
                databasesUpdated,
                databaseRowsImported,
                attachmentResult.Imported,
                attachmentResult.Skipped,
                warnings);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            if (dbContext is DbContext efContext)
            {
                efContext.ChangeTracker.Clear();
            }
            throw;
        }
    }

    private async Task<AttachmentImportResult> ImportAttachmentsAsync(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        ISet<string> documentPaths,
        string actor,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var skipped = 0;
        foreach (var (path, entry) in entries)
        {
            if (documentPaths.Contains(path) || IsSitemap(path, Path.GetExtension(path)))
            {
                continue;
            }
            if (entry.Length > MaxAttachmentBytes)
            {
                warnings.Add($"{path}: attachment exceeds 25 MB and was skipped.");
                skipped++;
                continue;
            }

            var key = $"export:{Hash(path.ToLowerInvariant())}";
            var file = await dbContext.SentinelImportedFiles
                .FirstOrDefaultAsync(item => item.NotionBlockId == key, cancellationToken);
            var content = await ReadBytesAsync(entry, cancellationToken);
            var contentType = ContentTypes.GetValueOrDefault(
                Path.GetExtension(path),
                "application/octet-stream");
            if (file is null)
            {
                file = new SentinelImportedFile
                {
                    NotionBlockId = key,
                    FileName = Path.GetFileName(path),
                    ContentType = contentType,
                    Content = content,
                    SizeBytes = content.LongLength,
                    CreatedBy = actor
                };
                await dbContext.SentinelImportedFiles.AddAsync(file, cancellationToken);
            }
            else
            {
                file.FileName = Path.GetFileName(path);
                file.ContentType = contentType;
                file.Content = content;
                file.SizeBytes = content.LongLength;
                file.UpdatedAt = DateTimeOffset.UtcNow;
                file.UpdatedBy = actor;
            }
            urls[path] = $"/admin/sentinel/files/{file.Id}";
            imported++;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return new AttachmentImportResult(imported, skipped, urls);
    }

    private static int ReconcileDatabase(
        WikiDatabase database,
        ArchiveDocument document,
        IReadOnlyList<ArchiveDocument> rowDocuments,
        IReadOnlyDictionary<string, string> attachmentUrls,
        IReadOnlyDictionary<string, string> documentTitlesByPath,
        string actor,
        DateTimeOffset now,
        ICollection<string> warnings)
    {
        var records = ParseCsv(document.Content);
        if (records.Count == 0 || records[0].Count == 0)
        {
            warnings.Add($"{document.EntryPath}: empty database was imported without rows.");
            EnsureDatabaseDefaults(database, actor, now);
            return 0;
        }

        var headers = records[0]
            .Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"Column {index + 1}" : value.Trim())
            .ToList();
        var propertiesByName = database.Properties
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var properties = new List<WikiDatabaseProperty>();
        for (var index = 0; index < headers.Count; index++)
        {
            if (!propertiesByName.TryGetValue(headers[index], out var property))
            {
                property = new WikiDatabaseProperty
                {
                    WikiDatabase = database,
                    WikiDatabaseId = database.Id,
                    Name = headers[index],
                    Type = index == 0 ? WikiDatabasePropertyTypes.Title : WikiDatabasePropertyTypes.Text,
                    SortOrder = database.Properties.Count + properties.Count,
                    CreatedAt = now,
                    CreatedBy = actor
                };
                database.Properties.Add(property);
                propertiesByName[property.Name] = property;
            }
            properties.Add(property);
        }
        if (database.Properties.All(property => property.Type != WikiDatabasePropertyTypes.Title))
        {
            properties[0].Type = WikiDatabasePropertyTypes.Title;
        }
        EnsureDatabaseDefaults(database, actor, now);

        var titleProperty = properties.FirstOrDefault(property => property.Type == WikiDatabasePropertyTypes.Title)
            ?? properties[0];
        var rowDocumentsByTitle = rowDocuments
            .GroupBy(row => NormalizeTitle(row.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<ArchiveDocument>(group.OrderBy(item => item.EntryPath, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
        var rowsByExportId = database.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NotionExportId))
            .GroupBy(row => row.NotionExportId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rowsByNotionId = database.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NotionId))
            .GroupBy(row => NormalizeNotionId(row.NotionId!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rowsByTitle = database.Rows
            .GroupBy(row => NormalizeTitle(
                WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), titleProperty.Id)
                ?? string.Empty),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new Queue<WikiDatabaseRow>(group), StringComparer.OrdinalIgnoreCase);

        for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var title = records[rowIndex].Count > 0 ? records[rowIndex][0].Trim() : string.Empty;
            ArchiveDocument? rowDocument = null;
            if (rowDocumentsByTitle.TryGetValue(NormalizeTitle(title), out var matchingDocuments)
                && matchingDocuments.Count > 0)
            {
                rowDocument = matchingDocuments.Dequeue();
            }

            WikiDatabaseRow? row = null;
            if (rowDocument is not null)
            {
                rowsByExportId.TryGetValue(rowDocument.ExportId, out row);
                if (row is null)
                {
                    rowsByNotionId.TryGetValue(rowDocument.ExportId, out row);
                }
            }
            if (row is null
                && rowsByTitle.TryGetValue(NormalizeTitle(title), out var matchingRows)
                && matchingRows.Count > 0)
            {
                row = matchingRows.Dequeue();
            }
            row ??= new WikiDatabaseRow
            {
                WikiDatabase = database,
                WikiDatabaseId = database.Id,
                CreatedAt = now,
                CreatedBy = actor
            };
            if (!database.Rows.Contains(row))
            {
                database.Rows.Add(row);
            }

            var values = new JsonObject();
            for (var columnIndex = 0; columnIndex < properties.Count; columnIndex++)
            {
                var value = columnIndex < records[rowIndex].Count
                    ? records[rowIndex][columnIndex]
                    : string.Empty;
                SetCsvValue(values, properties[columnIndex], value);
            }
            row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
            row.SortOrder = rowIndex - 1;
            row.NotionArchivedAt = null;
            if (rowDocument is not null)
            {
                row.NotionExportId = rowDocument.ExportId;
                row.BlocksJson = ConvertDocumentToBlocks(
                    rowDocument,
                    attachmentUrls,
                    documentTitlesByPath,
                    warnings);
            }
            row.UpdatedAt = now;
            row.UpdatedBy = actor;
        }
        return records.Count - 1;
    }

    private static void EnsureDatabaseDefaults(WikiDatabase database, string actor, DateTimeOffset now)
    {
        if (database.Views.Count == 0)
        {
            database.Views.Add(new WikiDatabaseView
            {
                WikiDatabase = database,
                WikiDatabaseId = database.Id,
                Name = "Table",
                Type = WikiDatabaseViewTypes.Table,
                SortOrder = 0,
                ConfigJson = WikiDatabaseViewConfigJson.Serialize(WikiDatabaseViewConfig.Empty),
                CreatedAt = now,
                CreatedBy = actor
            });
        }
    }

    private static void SetCsvValue(JsonObject values, WikiDatabaseProperty property, string value)
    {
        switch (property.Type)
        {
            case WikiDatabasePropertyTypes.Number:
                WikiPropertyValues.SetNumber(
                    values,
                    property.Id,
                    decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                        ? number
                        : null);
                break;
            case WikiDatabasePropertyTypes.Checkbox:
                WikiPropertyValues.SetCheckbox(
                    values,
                    property.Id,
                    value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || value == "1");
                break;
            case WikiDatabasePropertyTypes.Date:
                WikiPropertyValues.SetDate(
                    values,
                    property.Id,
                    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                        ? date
                        : null);
                break;
            case WikiDatabasePropertyTypes.MultiSelect:
                values[property.Id.ToString()] = new JsonArray(value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => (JsonNode)item)
                    .ToArray());
                break;
            case WikiDatabasePropertyTypes.Formula:
            case WikiDatabasePropertyTypes.Rollup:
                break;
            default:
                WikiPropertyValues.SetText(values, property.Id, value);
                break;
        }
    }

    private async Task AddPreImportRevisionAsync(
        WikiPage page,
        string actor,
        CancellationToken cancellationToken)
    {
        var latestRevision = await dbContext.WikiPageRevisions
            .Where(revision => revision.WikiPageId == page.Id)
            .Select(revision => (int?)revision.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0;
        await dbContext.WikiPageRevisions.AddAsync(new WikiPageRevision
        {
            WikiPageId = page.Id,
            RevisionNumber = latestRevision + 1,
            Title = page.Title,
            Slug = page.Slug,
            BlocksJson = page.BlocksJson,
            Label = "Before Notion workspace import",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = actor
        }, cancellationToken);
    }

    private static string ConvertDocumentToBlocks(
        ArchiveDocument document,
        IReadOnlyDictionary<string, string> attachmentUrls,
        IReadOnlyDictionary<string, string> documentTitlesByPath,
        ICollection<string> warnings)
    {
        var source = document.Extension is ".html" or ".htm"
            ? HtmlToMarkdown(document.Content)
            : document.Content;
        source = RewriteArchiveLinks(
            source,
            document.DirectoryPath,
            attachmentUrls,
            documentTitlesByPath);
        return WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(source));
    }

    private static string RewriteArchiveLinks(
        string markdown,
        string documentDirectory,
        IReadOnlyDictionary<string, string> attachmentUrls,
        IReadOnlyDictionary<string, string> documentTitlesByPath) =>
        MarkdownLinkPattern.Replace(markdown, match =>
        {
            var rawTarget = match.Groups["target"].Value.Trim();
            var target = ExtractMarkdownTarget(rawTarget);
            if (Uri.TryCreate(target, UriKind.Absolute, out _)
                || target.StartsWith('#')
                || target.StartsWith('/'))
            {
                return match.Value;
            }

            var resolved = ResolveRelativeArchivePath(documentDirectory, target);
            if (resolved is null)
            {
                return match.Value;
            }
            if (!match.Groups["image"].Success
                && documentTitlesByPath.TryGetValue(resolved, out var documentTitle))
            {
                return $"[[{documentTitle}]]";
            }
            if (!attachmentUrls.TryGetValue(resolved, out var localUrl))
            {
                return match.Value;
            }
            var prefix = match.Groups["image"].Success ? "!" : string.Empty;
            return $"{prefix}[{match.Groups["label"].Value}]({localUrl})";
        });

    private static string HtmlToMarkdown(string html)
    {
        var markdown = HtmlImagePattern.Replace(html, match =>
            $"![]({WebUtility.HtmlDecode(match.Groups["src"].Value)})");
        for (var level = 1; level <= 3; level++)
        {
            markdown = Regex.Replace(
                markdown,
                $@"<h{level}\b[^>]*>(?<content>.*?)</h{level}>",
                match => $"{new string('#', level)} {StripHtml(match.Groups["content"].Value)}\n\n",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        markdown = Regex.Replace(
            markdown,
            @"<li\b[^>]*>(?<content>.*?)</li>",
            match => $"- {StripHtml(match.Groups["content"].Value)}\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        markdown = Regex.Replace(markdown, @"<(br|/p|/div|/section|/article)\b[^>]*>", "\n",
            RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(markdown, "<[^>]+>", string.Empty))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static string StripHtml(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)).Trim();

    private static void AssignSortOrders(
        IReadOnlyDictionary<ArchiveDocument, WikiPage> pages,
        IReadOnlyDictionary<ArchiveDocument, WikiDatabase> databases)
    {
        var orderedDocuments = pages.Keys.Cast<ArchiveDocument>()
            .Concat(databases.Keys)
            .OrderBy(document => document.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pageOrders = new Dictionary<Guid, int>();
        var databaseOrders = new Dictionary<Guid, int>();
        var rootPageOrder = 0;
        var rootDatabaseOrder = 0;
        foreach (var document in orderedDocuments)
        {
            if (pages.TryGetValue(document, out var page))
            {
                if (page.ParentWikiPageId is { } parentId)
                {
                    page.SortOrder = pageOrders.GetValueOrDefault(parentId);
                    pageOrders[parentId] = page.SortOrder + 1;
                }
                else
                {
                    page.SortOrder = rootPageOrder++;
                }
            }
            else if (databases.TryGetValue(document, out var database))
            {
                if (database.ParentWikiPageId is { } parentId)
                {
                    database.SortOrder = databaseOrders.GetValueOrDefault(parentId);
                    databaseOrders[parentId] = database.SortOrder + 1;
                }
                else
                {
                    database.SortOrder = rootDatabaseOrder++;
                }
            }
        }
    }

    private static T? FindExisting<T>(
        string exportId,
        IReadOnlyDictionary<string, T> byExportId,
        IReadOnlyDictionary<string, T> byNotionId)
        where T : class
    {
        if (byExportId.TryGetValue(exportId, out var existing))
        {
            return existing;
        }
        return byNotionId.TryGetValue(exportId, out existing) ? existing : null;
    }

    private static ArchiveDocument? FindParentDocument(
        string directory,
        IReadOnlyDictionary<string, ArchiveDocument> documentsByStem)
    {
        var candidate = directory;
        while (candidate.Length > 0)
        {
            if (documentsByStem.TryGetValue(candidate, out var parent))
            {
                return parent;
            }
            candidate = DirectoryName(candidate);
        }
        return null;
    }

    private static string ReserveSlug(string title, ISet<string> reservedSlugs)
    {
        var baseSlug = WikiService.CreateSlug(title);
        if (reservedSlugs.Add(baseSlug))
        {
            return baseSlug;
        }
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseSlug}-{suffix}";
            if (reservedSlugs.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string ExtractTitle(string path)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        title = ExportIdPattern.Replace(title, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
    }

    private static string ExtractExportId(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = ExportIdPattern.Match(stem);
        return match.Success
            ? match.Groups["id"].Value.ToLowerInvariant()
            : $"path:{Hash(RemoveExtension(path).ToLowerInvariant())}";
    }

    private static string NormalizeNotionId(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static string NormalizeTitle(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSitemap(string path, string extension) =>
        extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(path).Equals("index.html", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeArchivePath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/')
                     .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string? ResolveRelativeArchivePath(string directory, string target)
    {
        var withoutFragment = target.Split('#', 2)[0].Split('?', 2)[0];
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(withoutFragment);
        }
        catch (UriFormatException)
        {
            decoded = withoutFragment;
        }
        return NormalizeArchivePath(
            string.IsNullOrWhiteSpace(directory) ? decoded : $"{directory}/{decoded}");
    }

    private static string ExtractMarkdownTarget(string target)
    {
        if (target.StartsWith('<') && target.Contains('>'))
        {
            return target[1..target.IndexOf('>')];
        }
        var quotedTitle = Regex.Match(target, @"^(?<url>\S+)\s+[""'].*[""']$");
        return quotedTitle.Success ? quotedTitle.Groups["url"].Value : target;
    }

    private static string RemoveExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length == 0 ? path : path[..^extension.Length];
    }

    private static string DirectoryName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static async Task<string> ReadTextAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadBytesAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        using var destination = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await source.CopyToAsync(destination, cancellationToken);
        return destination.ToArray();
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
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }
                record.Add(field.ToString());
                field.Clear();
                if (record.Any(value => !string.IsNullOrWhiteSpace(value)))
                {
                    records.Add(record);
                }
                record = [];
            }
            else
            {
                field.Append(character);
            }
        }
        record.Add(field.ToString());
        if (record.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            records.Add(record);
        }
        return records;
    }

    private static string NormalizeUser(string performedBy) =>
        string.IsNullOrWhiteSpace(performedBy)
            ? "notion-workspace-import"
            : performedBy.Trim().ToLowerInvariant();

    private sealed record AttachmentImportResult(
        int Imported,
        int Skipped,
        IReadOnlyDictionary<string, string> UrlsByPath);

    private sealed class ArchiveDocument(
        string entryPath,
        string stemPath,
        string directoryPath,
        string extension,
        string title,
        string exportId,
        string content)
    {
        public string EntryPath { get; } = entryPath;
        public string StemPath { get; } = stemPath;
        public string DirectoryPath { get; } = directoryPath;
        public string Extension { get; } = extension;
        public string Title { get; } = title;
        public string ExportId { get; } = exportId;
        public string Content { get; } = content;
        public ArchiveDocument? Parent { get; set; }
        public ArchiveDocumentKind Kind { get; set; }
    }

    private enum ArchiveDocumentKind
    {
        Page,
        Database,
        DatabaseRow
    }
}
