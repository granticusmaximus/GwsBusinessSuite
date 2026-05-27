using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SanityPublisher(HttpClient http, IOptions<SanityOptions> options) : ISanityPublisher
{
    public async Task<SanityPublishResult> PublishDraftAsync(ArticleGenerationResult draft, CancellationToken ct = default)
    {
        var sanityOptions = options.Value;
        ValidateOptions(sanityOptions);

        var documentId = BuildDocumentId(sanityOptions.DocumentIdPrefix, draft.DraftId);
        var endpoint = BuildEndpoint(sanityOptions);
        var payload = new
        {
            mutations = new object[]
            {
                new
                {
                    createOrReplace = new
                    {
                        _id = documentId,
                        _type = SanityType(sanityOptions.DocumentType),
                        title = draft.Title,
                        slug = new { current = draft.Slug },
                        excerpt = draft.MetaDescription,
                        bodyMarkdown = draft.PublishMarkdown,
                        canonicalUrl = draft.CanonicalUrl,
                        author = draft.Author,
                        topic = draft.Topic,
                        targetAudience = draft.TargetAudience,
                        primaryKeyword = draft.PrimaryKeyword,
                        secondaryKeywords = draft.SecondaryKeywords,
                        status = draft.Status,
                        revisionNumber = draft.RevisionNumber,
                        approvedAt = draft.ApprovedAt,
                        updatedAt = draft.UpdatedAt,
                        seo = new
                        {
                            metaTitle = draft.MetaTitle,
                            metaDescription = draft.MetaDescription,
                            keywords = draft.Keywords
                        },
                        affiliatePlacements = draft.AffiliatePlacements.Select(placement => new
                        {
                            slotToken = placement.SlotToken,
                            advertiserId = placement.AdvertiserId,
                            advertiserName = placement.AdvertiserName,
                            category = placement.Category,
                            trackingUrl = placement.TrackingUrl,
                            callToActionText = placement.CallToActionText,
                            sortOrder = placement.SortOrder
                        }).ToArray()
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sanityOptions.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Sanity publish failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}",
                null,
                response.StatusCode);
        }

        return new SanityPublishResult(
            IsSuccess: true,
            Message: $"Published '{draft.Title}' to Sanity.",
            DocumentId: documentId,
            Revision: ExtractRevision(body),
            DocumentUrl: BuildDocumentUrl(sanityOptions, documentId));
    }

    private static void ValidateOptions(SanityOptions sanityOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanityOptions.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanityOptions.Dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanityOptions.Token);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanityOptions.DocumentType);
    }

    private static string BuildEndpoint(SanityOptions options)
    {
        var apiVersion = string.IsNullOrWhiteSpace(options.ApiVersion) ? "2021-10-21" : options.ApiVersion.Trim();
        return $"https://{options.ProjectId}.api.sanity.io/v{apiVersion}/data/mutate/{options.Dataset.Trim()}?returnIds=true&returnDocuments=true";
    }

    private static string BuildDocumentId(string prefix, Guid draftId)
    {
        var safePrefix = string.IsNullOrWhiteSpace(prefix) ? "gws-seo-" : prefix.Trim();
        return $"{safePrefix}{draftId:N}";
    }

    private static string SanityType(string documentType)
    {
        return documentType.Trim();
    }

    private static string BuildDocumentUrl(SanityOptions options, string documentId)
    {
        return $"https://{options.ProjectId}.sanity.studio/desk/{options.Dataset};{documentId}";
    }

    private static string ExtractRevision(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var transaction = document.RootElement.TryGetProperty("transactionId", out var value)
                ? value.GetString()
                : string.Empty;

            return string.IsNullOrWhiteSpace(transaction) ? string.Empty : transaction;
        }
        catch
        {
            return string.Empty;
        }
    }
}
