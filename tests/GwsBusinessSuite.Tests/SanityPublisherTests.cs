using System.Net;
using System.Net.Http;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class SanityPublisherTests
{
    [Fact]
    public async Task PublishDraftAsync_ShouldSendCreateOrReplaceMutationToSanity()
    {
        var observedUris = new List<Uri>();
        var observedBodies = new List<string>();
        var observedAuth = new List<string>();

        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                observedBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                observedAuth.Add(request.Headers.Authorization?.ToString() ?? string.Empty);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"transactionId\":\"tx-123\"}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var publisher = new SanityPublisher(client, Options.Create(new SanityOptions
        {
            ProjectId = "demo-project",
            Dataset = "production",
            Token = "sanity-token",
            ApiVersion = "2021-10-21",
            DocumentType = "seoArticle",
            DocumentIdPrefix = "gws-seo-"
        }));

        var result = await publisher.PublishDraftAsync(new ArticleGenerationResult
        {
            DraftId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Title = "Clean Architecture in Blazor",
            Slug = "clean-architecture-in-blazor",
            Topic = "Clean Architecture in Blazor",
            TargetAudience = "Mid-level C# developers",
            PrimaryKeyword = "Blazor clean architecture",
            SecondaryKeywords = "ASP.NET Core, C#",
            Author = "GWS Editorial",
            Markdown = "# Draft",
            RenderedMarkdown = "# Draft",
            PublishMarkdown = "# Draft",
            MetaTitle = "Clean Architecture in Blazor",
            MetaDescription = "Learn Clean Architecture in Blazor with practical C# guidance.",
            Keywords = "Blazor clean architecture, C#",
            CanonicalUrl = "https://example.com/blog/clean-architecture-in-blazor",
            Status = "Approved",
            RevisionNumber = 1,
            AffiliatePlacements = Array.Empty<ArticleAffiliatePlacementView>()
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("tx-123", result.Revision);
        Assert.Contains(observedUris, uri => uri.Host.Equals("demo-project.api.sanity.io", StringComparison.OrdinalIgnoreCase));
        Assert.All(observedAuth, value => Assert.Equal("Bearer sanity-token", value));
        Assert.Single(observedBodies);
        Assert.Contains("createOrReplace", observedBodies[0]);
        Assert.Contains("clean-architecture-in-blazor", observedBodies[0]);
        Assert.Contains("# Draft", observedBodies[0]);
    }

    private sealed class RecordingHandler(
        List<Uri> observedUris,
        Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                observedUris.Add(request.RequestUri);
            }

            return Task.FromResult(responseFactory?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"transactionId\":\"tx-default\"}", Encoding.UTF8, "application/json")
                });
        }
    }
}