using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MissouriDotIncidentProviderTests
{
    // Fields and values from TravelerInformationMod/0, fetched during resume verification.
    private const string SampleJson = """
        { "features": [
          { "attributes": { "DATA_ID": 821953, "ROUTE": "RT VV S", "TYPE_CODE": "BRIDGE CLOSED", "LEVEL_OF_IMPACT_CODE": "CLOSED", "EXT_COMMENT": "Please use alternate route." }, "geometry": { "x": -92.809158893, "y": 39.857016956 } }
        ] }
        """;
    private const string EmptyJson = """{ "features": [] }""";
    private static readonly BoundingBox MissouriBbox = new(North: 41, South: 36, East: -89, West: -96);

    [Fact]
    public async Task GetIncidentsAsync_ShouldQueryIncidentLayersAndMapTheActualFields()
    {
        var requests = new List<Uri>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request.RequestUri!);
            return JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/0/query") ? SampleJson : EmptyJson);
        });

        var result = await provider.GetIncidentsAsync(MissouriBbox);

        var incident = result.Should().ContainSingle().Subject;
        incident.Id.Should().Be("modot-event-821953");
        incident.RoadwayName.Should().Be("RT VV S");
        incident.Description.Should().Be("Please use alternate route.");
        incident.EventType.Should().Be("BRIDGE CLOSED");
        incident.Severity.Should().Be("CLOSED");
        incident.Latitude.Should().Be(39.857016956);
        incident.Longitude.Should().Be(-92.809158893);
        requests.Select(u => u.AbsolutePath.Split('/')[^2]).Should().Equal("0", "3", "5", "8", "9", "11");
        requests.Should().OnlyContain(u => u.AbsolutePath.Contains("TravelerInformationMod/MapServer/")
            && u.Query.Contains("outSR=4326") && u.Query.Contains("outFields=DATA_ID,ROUTE,"));
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldFollowTransferLimitAndDeduplicateAcrossLayers()
    {
        var requests = new List<Uri>();
        var provider = CreateProvider(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            if (uri.AbsolutePath.EndsWith("/0/query") && uri.Query.Contains("resultOffset=0&"))
                return JsonResponse(SampleJson.Replace("{ \"features\":", "{ \"exceededTransferLimit\": true, \"features\":"));
            if (uri.AbsolutePath.EndsWith("/0/query"))
                return JsonResponse(SampleJson.Replace("821953", "821954"));
            return JsonResponse(SampleJson);
        });

        var result = await provider.GetIncidentsAsync(MissouriBbox);

        result.Select(i => i.Id).Should().BeEquivalentTo("modot-event-821953", "modot-event-821954");
        requests.Should().Contain(u => u.AbsolutePath.EndsWith("/0/query") && u.Query.Contains("resultOffset=1&"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetIncidentsAsync_ShouldRetainOtherLayersWhenOneLayerFails(bool arcGisError)
    {
        var provider = CreateProvider(request => request.RequestUri!.AbsolutePath.EndsWith("/0/query")
            ? arcGisError ? JsonResponse("""{ "error": { "code": 400, "message": "Failed to execute query." } }""")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(MissouriBbox);

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldCacheAllLayersAndFilterEachBoundingBox()
    {
        var requests = 0;
        var provider = CreateProvider(_ => { requests++; return JsonResponse(SampleJson); });
        (await provider.GetIncidentsAsync(MissouriBbox)).Should().ContainSingle();

        (await provider.GetIncidentsAsync(new BoundingBox(10, 0, 10, 0))).Should().BeEmpty();

        requests.Should().Be(6);
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldSkipMissingIdentifiersAndGeometry()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            { "features": [
              { "attributes": { "ROUTE": "MO 13" }, "geometry": { "x": -93.3, "y": 37.2 } },
              { "attributes": { "DATA_ID": 2 }, "geometry": null }
            ] }
            """));

        (await provider.GetIncidentsAsync(MissouriBbox)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmptyWhenAllRequestsFail()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await provider.GetIncidentsAsync(MissouriBbox)).Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static MissouriDotIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://mapping.modot.org/")
        };
        return new MissouriDotIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MissouriDotIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
