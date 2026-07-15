using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.CmsKnowledge;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.AppGeneration;

// Author-facing chat -> Admin-facing approval queue for adding AI-drafted pages to an
// existing CmsSite. Nothing touches CmsPages until ApproveAsync runs - up to then, the
// "current plan" is just JSON sitting on the AppGenerationRequest row, refined turn by
// turn. See AppGenerationRequest's doc comment in CoreEntities.cs for the overall shape.
public sealed class AppGenerationService(
    IAppDbContext dbContext,
    IOllamaService ollama,
    ISiteSettingsService siteSettingsService,
    ICmsBuilderService cmsBuilderService,
    ICmsKnowledgeService cmsKnowledgeService,
    ICurrentUserAccessor? currentUserAccessor = null) : IAppGenerationService
{
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    public async Task<IReadOnlyList<AppGenerationRequestView>> ListRequestsAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AppGenerationRequests.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        var requests = await query.ToListAsync(cancellationToken);
        if (requests.Count == 0)
        {
            return [];
        }

        var requestIds = requests.Select(r => r.Id).ToList();
        var messages = await dbContext.AppGenerationMessages
            .Where(m => requestIds.Contains(m.AppGenerationRequestId))
            .ToListAsync(cancellationToken);
        var messagesByRequest = messages.GroupBy(m => m.AppGenerationRequestId).ToDictionary(g => g.Key, g => g.ToList());

        var siteIds = requests.Select(r => r.TargetSiteId).Distinct().ToList();
        var siteNames = await dbContext.CmsSites
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        // Client-side ordering, not .OrderByDescending before ToListAsync — SQLite/EF Core
        // can't translate ORDER BY on a DateTimeOffset column (see feedback_sqlite_datetimeoffset).
        return requests
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => ToView(r, siteNames.GetValueOrDefault(r.TargetSiteId, "(deleted site)"), messagesByRequest.GetValueOrDefault(r.Id, [])))
            .ToList();
    }

    public async Task<AppGenerationRequestView?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await dbContext.AppGenerationRequests.FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        var siteName = await GetSiteNameAsync(request.TargetSiteId, cancellationToken);
        return await BuildViewAsync(request, siteName, cancellationToken);
    }

    public async Task<AppGenerationChatResult> StartAsync(StartAppGenerationInput input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.InitialPrompt))
        {
            return new AppGenerationChatResult(false, "Describe what you'd like generated first.", null);
        }

        var site = await dbContext.CmsSites.FirstOrDefaultAsync(s => s.Id == input.TargetSiteId, cancellationToken);
        if (site is null)
        {
            return new AppGenerationChatResult(false, "Target site not found.", null);
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var request = new AppGenerationRequest
        {
            TargetSiteId = site.Id,
            Title = string.IsNullOrWhiteSpace(input.Title) ? Truncate(input.InitialPrompt.Trim(), 120) : input.Title.Trim(),
            CreatedBy = username
        };
        dbContext.AppGenerationRequests.Add(request);
        dbContext.AppGenerationMessages.Add(new AppGenerationMessage
        {
            AppGenerationRequestId = request.Id,
            Role = AppGenerationMessageRoles.User,
            Content = input.InitialPrompt.Trim(),
            CreatedBy = username
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return await RunTurnAsync(request, site, cancellationToken);
    }

    public async Task<AppGenerationChatResult> SendMessageAsync(Guid requestId, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new AppGenerationChatResult(false, "Message can't be empty.", null);
        }

        var request = await dbContext.AppGenerationRequests.FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new AppGenerationChatResult(false, "Request not found.", null);
        }

        if (!string.Equals(request.Status, AppGenerationRequestStatuses.Drafting, StringComparison.Ordinal))
        {
            return new AppGenerationChatResult(false, "This chat is no longer in drafting, so it can't take new messages.", null);
        }

        var site = await dbContext.CmsSites.FirstOrDefaultAsync(s => s.Id == request.TargetSiteId, cancellationToken);
        if (site is null)
        {
            return new AppGenerationChatResult(false, "Target site no longer exists.", null);
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        dbContext.AppGenerationMessages.Add(new AppGenerationMessage
        {
            AppGenerationRequestId = request.Id,
            Role = AppGenerationMessageRoles.User,
            Content = message.Trim(),
            CreatedBy = username
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return await RunTurnAsync(request, site, cancellationToken);
    }

    public async Task<AppGenerationChatResult> SubmitForApprovalAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await dbContext.AppGenerationRequests.FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new AppGenerationChatResult(false, "Request not found.", null);
        }

        if (!string.Equals(request.Status, AppGenerationRequestStatuses.Drafting, StringComparison.Ordinal))
        {
            return new AppGenerationChatResult(false, "Only requests still in drafting can be submitted.", null);
        }

        if (DeserializePages(request.GeneratedPagesJson).Count == 0)
        {
            return new AppGenerationChatResult(false, "Keep chatting until at least one page has been drafted before submitting.", null);
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        request.Status = AppGenerationRequestStatuses.PendingApproval;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        request.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);

        var siteName = await GetSiteNameAsync(request.TargetSiteId, cancellationToken);
        return new AppGenerationChatResult(true, null, await BuildViewAsync(request, siteName, cancellationToken));
    }

    public async Task<AppGenerationChatResult> ApproveAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await dbContext.AppGenerationRequests.FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new AppGenerationChatResult(false, "Request not found.", null);
        }

        if (!string.Equals(request.Status, AppGenerationRequestStatuses.PendingApproval, StringComparison.Ordinal))
        {
            return new AppGenerationChatResult(false, "Only requests pending approval can be approved.", null);
        }

        var site = await dbContext.CmsSites.FirstOrDefaultAsync(s => s.Id == request.TargetSiteId, cancellationToken);
        if (site is null)
        {
            return new AppGenerationChatResult(false, "Target site no longer exists.", null);
        }

        var pages = DeserializePages(request.GeneratedPagesJson);
        if (pages.Count == 0)
        {
            return new AppGenerationChatResult(false, "No generated pages to apply.", null);
        }

        foreach (var page in pages)
        {
            await cmsBuilderService.SavePageAsync(new CmsPageEditorModel
            {
                SiteId = site.Id,
                Title = page.Title,
                Slug = page.Slug,
                BlocksJson = CmsBuilderJson.Serialize(page.Layout),
                MetaDescription = page.MetaDescription,
                Status = CmsPageStatuses.Draft
            }, cancellationToken);
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        request.Status = AppGenerationRequestStatuses.Approved;
        request.ApprovedBy = username;
        request.ApprovedAt = DateTimeOffset.UtcNow;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        request.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AppGenerationChatResult(true, null, await BuildViewAsync(request, site.Name, cancellationToken));
    }

    public async Task<AppGenerationChatResult> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken = default)
    {
        var request = await dbContext.AppGenerationRequests.FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new AppGenerationChatResult(false, "Request not found.", null);
        }

        if (!string.Equals(request.Status, AppGenerationRequestStatuses.PendingApproval, StringComparison.Ordinal))
        {
            return new AppGenerationChatResult(false, "Only requests pending approval can be rejected.", null);
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        request.Status = AppGenerationRequestStatuses.Rejected;
        request.RejectedAt = DateTimeOffset.UtcNow;
        request.RejectionReason = reason?.Trim() ?? string.Empty;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        request.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);

        var siteName = await GetSiteNameAsync(request.TargetSiteId, cancellationToken);
        return new AppGenerationChatResult(true, null, await BuildViewAsync(request, siteName, cancellationToken));
    }

    public Task<int> CountPendingApprovalAsync(CancellationToken cancellationToken = default) =>
        dbContext.AppGenerationRequests.CountAsync(x => x.Status == AppGenerationRequestStatuses.PendingApproval, cancellationToken);

    private async Task<AppGenerationChatResult> RunTurnAsync(AppGenerationRequest request, CmsSite site, CancellationToken cancellationToken)
    {
        var transcript = await dbContext.AppGenerationMessages
            .Where(m => m.AppGenerationRequestId == request.Id)
            .ToListAsync(cancellationToken);
        transcript = transcript.OrderBy(m => m.CreatedAt).ToList();

        var existingTitles = await dbContext.CmsPages
            .Where(p => p.SiteId == site.Id && p.TrashedAt == null)
            .Select(p => p.Title)
            .ToListAsync(cancellationToken);

        // Retrieval-augmented context: the latest user message is used as a keyword query
        // against the CmsKnowledge library (WordPress/Elementor-inspired clean-room notes),
        // so Ollama's plan benefits from established workflow patterns without every prompt
        // dumping the entire library. No match just means no reference-notes section - the
        // chat still works exactly as before this was wired in.
        var latestUserMessage = transcript.LastOrDefault(m => m.Role == AppGenerationMessageRoles.User)?.Content ?? string.Empty;
        var referenceNotes = await cmsKnowledgeService.SearchAsync(latestUserMessage, take: 3, cancellationToken: cancellationToken);

        var model = await GetEffectiveModelAsync(cancellationToken);
        var systemPrompt = BuildSystemPrompt(site.Name, existingTitles, referenceNotes);
        var userPrompt = BuildConversationPrompt(transcript);

        string raw;
        try
        {
            raw = await ollama.GenerateAsync(model, systemPrompt, userPrompt, cancellationToken);
        }
        catch (Exception ex)
        {
            var currentSiteName = await GetSiteNameAsync(request.TargetSiteId, cancellationToken);
            return new AppGenerationChatResult(false, $"Ollama generation failed: {ex.Message}", await BuildViewAsync(request, currentSiteName, cancellationToken));
        }

        var (reply, pages) = ParseAssistantTurn(raw);

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        dbContext.AppGenerationMessages.Add(new AppGenerationMessage
        {
            AppGenerationRequestId = request.Id,
            Role = AppGenerationMessageRoles.Assistant,
            Content = reply,
            CreatedBy = "ollama"
        });

        if (pages is { Count: > 0 })
        {
            request.GeneratedPagesJson = SerializePages(pages);
        }

        request.UpdatedAt = DateTimeOffset.UtcNow;
        request.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AppGenerationChatResult(true, null, await BuildViewAsync(request, site.Name, cancellationToken));
    }

    private async Task<string> GetSiteNameAsync(Guid siteId, CancellationToken cancellationToken) =>
        await dbContext.CmsSites.Where(s => s.Id == siteId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken)
        ?? "(deleted site)";

    private async Task<AppGenerationRequestView> BuildViewAsync(AppGenerationRequest request, string siteName, CancellationToken cancellationToken)
    {
        var messages = await dbContext.AppGenerationMessages
            .Where(m => m.AppGenerationRequestId == request.Id)
            .ToListAsync(cancellationToken);

        return ToView(request, siteName, messages);
    }

    private static AppGenerationRequestView ToView(AppGenerationRequest request, string siteName, List<AppGenerationMessage> messages) => new()
    {
        Id = request.Id,
        TargetSiteId = request.TargetSiteId,
        TargetSiteName = siteName,
        Title = request.Title,
        Status = request.Status,
        GeneratedPages = DeserializePages(request.GeneratedPagesJson),
        Messages = messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new AppGenerationMessageView(m.Role, m.Content, m.CreatedAt))
            .ToList(),
        CreatedBy = request.CreatedBy,
        CreatedAt = request.CreatedAt,
        ApprovedBy = request.ApprovedBy,
        ApprovedAt = request.ApprovedAt,
        RejectedAt = request.RejectedAt,
        RejectionReason = request.RejectionReason
    };

    private async Task<string> GetEffectiveModelAsync(CancellationToken cancellationToken)
    {
        var settings = await siteSettingsService.GetSettingsAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(settings.OllamaModelOverride)
            ? ContentStudioOptions.DefaultModel
            : settings.OllamaModelOverride;
    }

    private static string BuildConversationPrompt(List<AppGenerationMessage> transcript) =>
        string.Join("\n\n", transcript.Select(m => $"{m.Role}: {m.Content}"));

    private static string BuildSystemPrompt(string siteName, List<string> existingPageTitles, IReadOnlyList<CmsKnowledgeQueryResult> referenceNotes)
    {
        var existingSummary = existingPageTitles.Count == 0
            ? "none yet"
            : string.Join(", ", existingPageTitles.Take(20));

        var referenceNotesBlock = referenceNotes.Count == 0
            ? string.Empty
            : "Reference notes (WordPress/Elementor-inspired workflow patterns - use as inspiration,\n"
              + "not a requirement to include any of them):\n"
              + string.Join("\n", referenceNotes.Select(n => $"- {n.Capability}: {n.WorkflowSummary}"))
              + "\n\n";

        return """
            You are a website-building assistant embedded in a CMS admin tool. You're helping
            an author plan new pages to add to the existing site "__SITE_NAME__" (existing pages:
            __EXISTING_SUMMARY__). Nothing you propose goes live until a human admin approves it.

            __REFERENCE_NOTES__Have a brief, conversational back-and-forth about what the author
            wants. Ask at most one or two clarifying questions if the request is vague, otherwise
            just propose a concrete plan.

            CRITICAL OUTPUT CONTRACT: whenever you have a concrete plan (even a rough first
            draft), end your reply with a fenced code block labeled json containing the FULL
            current plan - not a diff from last time - in exactly this shape:

            ```json
            {"pages":[{"title":"Page Title","slug":"page-slug","metaDescription":"one sentence","layout":{"sections":[{"label":"Hero","background":"transparent","padding":"lg","columnLayout":"full","columns":[{"span":12,"widgets":[{"widgetType":"hero","props":{"headline":"...","subline":"...","cta1Label":"...","cta1Href":"/contact"}}]}]}]}}]}
            ```

            If you're only asking clarifying questions and have nothing concrete yet, omit the
            json block entirely.

            Allowed values:
            - background: transparent | light | dark | accent
            - padding: none | sm | md | lg | xl
            - columnLayout: full | half-half | one-third-two-thirds | two-thirds-one-third | thirds
            - widgetType and its props (props is always a flat string-to-string map):
              - hero: headline, subline, cta1Label, cta1Href, cta2Label, cta2Href, align(left|center|right)
              - heading: text, level(h1|h2|h3|h4), align
              - paragraph: text, align
              - richtext: content (markdown)
              - button: label, href, variant(primary|secondary|outline|ghost), align
              - image: src, alt, caption, width(full|half)
              - card: title, body, imageSrc, link
              - testimonial: quote, authorName, authorRole
              - spacer: height (pixels as a number string, e.g. "48")
              - divider: style(solid|dashed)

            Never invent widget types outside this list. Use realistic placeholder image paths
            like "/media/placeholder.jpg" since no image generation is available here. Keep
            prose replies short - the plan JSON is what matters.
            """
            .Replace("__SITE_NAME__", siteName)
            .Replace("__EXISTING_SUMMARY__", existingSummary)
            .Replace("__REFERENCE_NOTES__", referenceNotesBlock);
    }

    private static (string ReplyText, List<GeneratedPageSpec>? Pages) ParseAssistantTurn(string raw)
    {
        var trimmed = raw.Trim();
        var fenceStart = trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0)
        {
            return (trimmed, null);
        }

        var jsonStart = trimmed.IndexOf('{', fenceStart);
        var fenceEnd = jsonStart < 0 ? -1 : trimmed.IndexOf("```", jsonStart, StringComparison.Ordinal);
        if (jsonStart < 0 || fenceEnd < 0)
        {
            return (trimmed, null);
        }

        var jsonText = trimmed[jsonStart..fenceEnd].Trim();
        var replyText = trimmed[..fenceStart].Trim();
        if (string.IsNullOrWhiteSpace(replyText))
        {
            replyText = "Here's the updated page plan.";
        }

        var wire = CmsBuilderJson.Parse<WireResponse>(jsonText);
        var pages = wire?.Pages?
            .Where(p => !string.IsNullOrWhiteSpace(p.Title) && p.Layout is not null)
            .Select(p => new GeneratedPageSpec(
                p.Title!.Trim(),
                p.Slug?.Trim() ?? string.Empty,
                p.MetaDescription?.Trim() ?? string.Empty,
                p.Layout!))
            .ToList();

        return pages is { Count: > 0 } ? (replyText, pages) : (replyText, null);
    }

    private static string SerializePages(List<GeneratedPageSpec> pages) => CmsBuilderJson.Serialize(pages
        .Select(p => new WirePageSpec { Title = p.Title, Slug = p.Slug, MetaDescription = p.MetaDescription, Layout = p.Layout })
        .ToList());

    private static List<GeneratedPageSpec> DeserializePages(string json)
    {
        var wire = CmsBuilderJson.Parse<List<WirePageSpec>>(json);
        if (wire is null)
        {
            return [];
        }

        return wire
            .Where(w => !string.IsNullOrWhiteSpace(w.Title) && w.Layout is not null)
            .Select(w => new GeneratedPageSpec(w.Title!.Trim(), w.Slug?.Trim() ?? string.Empty, w.MetaDescription?.Trim() ?? string.Empty, w.Layout!))
            .ToList();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";

    private sealed class WirePageSpec
    {
        public string? Title { get; set; }
        public string? Slug { get; set; }
        public string? MetaDescription { get; set; }
        public PageLayout? Layout { get; set; }
    }

    private sealed class WireResponse
    {
        public List<WirePageSpec>? Pages { get; set; }
    }
}
