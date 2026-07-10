using System.Net;
using System.Net.Http;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class CjAffiliateServiceTests
{
    // Publisher-scoped joined-advertiser lookup is tried first (see FetchPartnersAsync) and
    // only falls through to the commissions GraphQL query below when it comes back empty -
    // every commissions-focused test in this file needs to return an empty advertiser-lookup
    // result so the GraphQL path it's actually exercising still gets reached.
    private static HttpResponseMessage EmptyAdvertiserLookupResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
    };

    private static bool IsAdvertiserLookupRequest(HttpRequestMessage request) =>
        request.RequestUri is not null &&
        request.RequestUri.Host.Equals("advertiser-lookup.api.cj.com", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task FetchPartnersAsync_ShouldUseGraphQlPost_ForCommissionsEndpoint()
    {
        var observedUris = new List<Uri>();
        var observedMethods = new List<HttpMethod>();
        using var handler = new RecordingHandler(
            observedUris,
            observedMethods: observedMethods,
            responseFactory: request => IsAdvertiserLookupRequest(request)
                ? EmptyAdvertiserLookupResponse()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme Corp\"}]}}}", Encoding.UTF8, "application/json")
                });
        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "123456",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 5));

        Assert.Single(result.Partners);
        Assert.NotEmpty(observedUris);
        Assert.Contains(observedUris, uri => uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase));
        Assert.All(observedUris.Where(uri => uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase)),
            uri => Assert.Equal("commissions.api.cj.com", uri.Host, ignoreCase: true));
        Assert.Contains(observedMethods, method => method == HttpMethod.Post);
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldNormalizeCommissionsRootEndpointToQueryPath()
    {
        var observedUris = new List<Uri>();
        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request => IsAdvertiserLookupRequest(request)
                ? EmptyAdvertiserLookupResponse()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme Corp\"}]}}}", Encoding.UTF8, "application/json")
                });
        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "123456",
            EndpointUrl: "https://commissions.api.cj.com",
            MaxResults: 5));

        var commissionsUris = observedUris.Where(uri => uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(commissionsUris);
        Assert.All(commissionsUris, uri =>
        {
            Assert.Equal("commissions.api.cj.com", uri.Host, ignoreCase: true);
            Assert.Equal("/query", uri.AbsolutePath);
        });
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldThrowGraphQlError_WhenApiReturnsErrors()
    {
        var observedUris = new List<Uri>();
        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":null,\"errors\":[{\"message\":\"Unauthorized\"}]}", Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FetchPartnersAsync(new CjConnectionRequest(
                DeveloperKey: "dev-key",
                PublisherId: "123456",
                EndpointUrl: "https://commissions.api.cj.com",
                MaxResults: 5)));

        Assert.Contains("GraphQL query failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Unauthorized", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldFallbackToBearer_WhenRawAuthorizationFails()
    {
        var observedUris = new List<Uri>();
        var authorizationValues = new List<string>();

        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                var auth = request.Headers.TryGetValues("Authorization", out var values)
                    ? values.FirstOrDefault() ?? string.Empty
                    : string.Empty;

                authorizationValues.Add(auth);

                if (string.Equals(auth, "dev-key", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("unauthorized", Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme Corp\"}]}}}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "123456",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 5));

        Assert.Single(result.Partners);
        Assert.Contains("dev-key", authorizationValues);
        Assert.Contains("Bearer dev-key", authorizationValues);
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldMapGraphQlCommissionRecords_ToPartnerRecords()
    {
        var observedUris = new List<Uri>();
        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request => IsAdvertiserLookupRequest(request)
                ? EmptyAdvertiserLookupResponse()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme\",\"country\":\"US\",\"actionStatus\":\"new\"},{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme\",\"country\":\"US\",\"actionStatus\":\"new\"}]}}}", Encoding.UTF8, "application/json")
                });
        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "123456",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 10));

        var partner = Assert.Single(result.Partners);
        Assert.Equal("1001", partner.AdvertiserId);
        Assert.Equal("Acme", partner.AdvertiserName);
        Assert.Equal("US", partner.Country);
        Assert.Equal("new", partner.RelationshipStatus);
    }

    // "My Advertisers" is relationship data (joined/active), not transaction history - a
    // publisher can be joined with an advertiser for months before ever generating a
    // commission from them. So the joined-only advertiser-lookup result is authoritative
    // on its own and used as-is; it does NOT get merged with (or need corroboration from)
    // the commissions GraphQL endpoint, which is only consulted when lookup finds nothing.
    [Fact]
    public async Task FetchPartnersAsync_ShouldUseAdvertiserLookupAlone_WithoutCallingCommissionsEndpoint()
    {
        var observedUris = new List<Uri>();
        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                var uri = request.RequestUri;
                if (uri is not null && uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme Corp\",\"actionStatus\":\"active\"}]}}}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"advertiser-id\":\"2001\",\"advertiser-name\":\"Lookup Partner\",\"relationship-status\":\"joined\",\"country\":\"US\"},{\"advertiser-id\":\"2002\",\"advertiser-name\":\"Second Lookup Partner\",\"relationship-status\":\"active\",\"country\":\"US\"}]}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "123456",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 100));

        Assert.Equal(2, result.Partners.Count);
        Assert.Contains(result.Partners, partner => partner.AdvertiserId == "2001");
        Assert.Contains(result.Partners, partner => partner.AdvertiserId == "2002");
        Assert.Contains(observedUris, uri => uri.Host.Equals("advertiser-lookup.api.cj.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observedUris, uri => uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldUseOptionalWebsiteFilter_WhenProvided()
    {
        var observedUris = new List<Uri>();
        var payloads = new List<string>();

        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                if (IsAdvertiserLookupRequest(request))
                {
                    return EmptyAdvertiserLookupResponse();
                }

                var payload = request.Content is null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                payloads.Add(payload);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme\"}]}}}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "pub-1",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 10,
            WebsiteId: "web-1"));

        Assert.NotEmpty(payloads);
        Assert.Contains(payloads, x => x.Contains("\"publisherIds\":[\"pub-1\"]", StringComparison.Ordinal));
        Assert.Contains(payloads, x => x.Contains("\"websiteIds\":[\"web-1\"]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldFallbackToAdvertiserLookup_WhenCommissionsReturnsNoRecords()
    {
        var observedUris = new List<Uri>();

        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                if (request.RequestUri is not null &&
                    request.RequestUri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[]}}}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"advertiser-id\":\"2001\",\"advertiser-name\":\"Lookup Partner\",\"relationship-status\":\"joined\",\"country\":\"US\"}]}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "pub-1",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 10));

        Assert.Single(result.Partners);
        Assert.Contains("active CJ advertisers", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(observedUris, uri => uri.Host.Equals("advertiser-lookup.api.cj.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldUseKeywordAdvertiserLookup_WhenBroadLookupReturnsEmpty()
    {
        var observedUris = new List<Uri>();

        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                var uri = request.RequestUri;
                if (uri is not null && uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[]}}}", Encoding.UTF8, "application/json")
                    };
                }

                if (uri is not null && uri.Host.Equals("advertiser-lookup.api.cj.com", StringComparison.OrdinalIgnoreCase))
                {
                    var query = uri.Query;
                    if (query.Contains("keywords=a", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"results\":[{\"advertiser-id\":\"3001\",\"advertiser-name\":\"Alpha Partner\",\"relationship-status\":\"joined\",\"country\":\"US\"}]}", Encoding.UTF8, "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "pub-1",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 10));

        var partner = Assert.Single(result.Partners);
        Assert.Equal("3001", partner.AdvertiserId);
        Assert.Contains(observedUris, uri =>
            uri.Host.Equals("advertiser-lookup.api.cj.com", StringComparison.OrdinalIgnoreCase) &&
            uri.Query.Contains("keywords=a", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchPartnersAsync_ShouldRetryWithoutWebsiteFilter_WhenFilteredResultIsEmpty()
    {
        var observedUris = new List<Uri>();
        var payloads = new List<string>();

        using var handler = new RecordingHandler(
            observedUris,
            responseFactory: request =>
            {
                if (IsAdvertiserLookupRequest(request))
                {
                    return EmptyAdvertiserLookupResponse();
                }

                var body = request.Content is null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                payloads.Add(body);

                if (body.Contains("\"websiteIds\":[\"web-1\"]", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[]}}}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[{\"advertiserId\":\"1001\",\"advertiserName\":\"Acme\"}]}}}", Encoding.UTF8, "application/json")
                };
            });

        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.FetchPartnersAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "pub-1",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 10,
            WebsiteId: "web-1"));

        Assert.Single(result.Partners);
        Assert.Contains("without Website ID filter", result.Message, StringComparison.Ordinal);
        Assert.Contains(payloads, x => x.Contains("\"websiteIds\":[\"web-1\"]", StringComparison.Ordinal));
        Assert.Contains(payloads, x => x.Contains("\"publisherIds\":[\"pub-1\"]", StringComparison.Ordinal) && !x.Contains("\"websiteIds\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateConnectionAsync_ShouldSucceed_WhenNoRecordsReturned()
    {
        var observedUris = new List<Uri>();
        using var handler = new RecordingHandler(observedUris);
        using var client = new HttpClient(handler);
        var service = new CjAffiliateService(client);

        var result = await service.ValidateConnectionAsync(new CjConnectionRequest(
            DeveloperKey: "dev-key",
            PublisherId: "pub-1",
            EndpointUrl: "https://commissions.api.cj.com/query",
            MaxResults: 10,
            WebsiteId: "web-1"));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.PartnerCountPreview);
    }

    private sealed class RecordingHandler(
        List<Uri> observedUris,
        List<HttpMethod>? observedMethods = null,
        Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                observedUris.Add(request.RequestUri);
            }

            observedMethods?.Add(request.Method);

            var response = responseFactory?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"publisherCommissions\":{\"records\":[]}}}", Encoding.UTF8, "application/json")
                };

            return Task.FromResult(response);
        }
    }
}
