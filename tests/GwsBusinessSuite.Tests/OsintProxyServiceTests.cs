using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Web;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class OsintProxyServiceTests
{
    [Fact]
    public async Task ForwardAsync_ShouldForwardOnlyTheAllowlistedRequestSurface()
    {
        var handler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"ok\":true}")
            };
            response.Headers.TryAddWithoutValidation("X-Upstream", "preserved");
            response.Headers.TryAddWithoutValidation("Content-Security-Policy", "default-src *");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "upstream=value");
            response.Headers.TryAddWithoutValidation("X-Frame-Options", "DENY");
            return response;
        });
        var service = BuildService(handler);
        var context = BuildContext(HttpMethods.Post, "{\"query\":\"tail number\"}");
        context.Request.QueryString = new QueryString("?mode=all%20items");
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json";
        context.Request.Headers.Authorization = "Bearer private-gws-token";
        context.Request.Headers.Cookie = ".AspNetCore.Identity.Application=private-cookie";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        context.Request.Headers["RSC"] = "1";

        await service.ForwardAsync(
            context,
            "api/search",
            rewriteDocumentPaths: false,
            CancellationToken.None);

        handler.RequestCount.Should().Be(1);
        handler.Method.Should().Be(HttpMethod.Post);
        handler.RequestUri!.PathAndQuery.Should().Be("/api/search?mode=all%20items");
        handler.Body.Should().Be("{\"query\":\"tail number\"}");
        handler.Headers.Should().ContainKey("Accept").WhoseValue.Should().Contain("application/json");
        handler.Headers.Should().ContainKey("Content-Type").WhoseValue.Should().Contain("application/json");
        handler.Headers.Should().ContainKey("RSC").WhoseValue.Should().Contain("1");
        handler.Headers.Should().ContainKey("User-Agent")
            .WhoseValue.Should().Contain("GwsBusinessSuite-OsintProxy/1.0");
        handler.Headers.Should().NotContainKey("Authorization");
        handler.Headers.Should().NotContainKey("Cookie");
        handler.Headers.Should().NotContainKey("X-Forwarded-For");

        context.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        context.Response.Headers["X-Upstream"].ToString().Should().Be("preserved");
        context.Response.Headers.Should().NotContainKey("Content-Security-Policy");
        context.Response.Headers.Should().NotContainKey("Set-Cookie");
        context.Response.Headers.Should().NotContainKey("X-Frame-Options");
        (await ReadResponseBodyAsync(context)).Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task ForwardAsync_ShouldRejectTraversalBeforeCallingTheSidecar()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = BuildService(handler);
        var context = BuildContext(HttpMethods.Get);

        await service.ForwardAsync(
            context,
            "api/../private",
            rewriteDocumentPaths: false,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        handler.RequestCount.Should().Be(0);
        (await ReadResponseBodyAsync(context)).Should().Be("Invalid OSINT Watch path.");
    }

    [Fact]
    public void RewriteDocumentPaths_ShouldKeepSharedApiAndAssetRoutesAtTheOrigin()
    {
        const string html = """
            <a href="/">Home</a>
            <a href="/docs">Docs</a>
            <script src="/_next/static/app.js"></script>
            <img src="/favicon.ico">
            <form action="/lookup"></form>
            <span data-rsc="{\"href\":\"/\"}"></span>
            <span data-api="/api/events"></span>
            <span data-file="/data/submarine-cables.json"></span>
            """;

        var result = OsintProxyService.RewriteDocumentPaths(html);

        result.Should().Contain("href=\"/admin/osint-root/\"");
        result.Should().Contain("href=\"/admin/osint-root/docs\"");
        result.Should().Contain("src=\"/admin/osint-root/favicon.ico\"");
        result.Should().Contain("action=\"/admin/osint-root/lookup\"");
        result.Should().Contain("{\\\"href\\\":\\\"/admin/osint-root/\\\"}");
        result.Should().Contain("src=\"/_next/static/app.js\"");
        result.Should().Contain("/api/events");
        result.Should().Contain("/data/submarine-cables.json");
    }

    [Theory]
    [InlineData("http://osiris:3000/docs?section=map", "/admin/osint-root/docs?section=map")]
    [InlineData("/docs", "/admin/osint-root/docs")]
    [InlineData("/_next/static/app.js", "/_next/static/app.js")]
    [InlineData("https://example.com/docs", "https://example.com/docs")]
    public void RewriteLocation_ShouldOnlyPrefixOsirisDocumentRoutes(string location, string expected)
    {
        var result = OsintProxyService.RewriteLocation(
            new Uri(location, UriKind.RelativeOrAbsolute),
            new Uri("http://osiris:3000"));

        result.Should().Be(expected);
    }

    [Fact]
    public async Task MapOsintProxyEndpoints_ShouldRequireAdminAuthorizationAndRateLimiting()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();

        app.MapOsintProxyEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is not null)
            .ToArray();

        routes.Should().HaveCount(4);
        routes.Should().OnlyContain(endpoint => endpoint.Metadata
            .OfType<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "AdminOnly"));
        routes.Should().OnlyContain(endpoint => endpoint.Metadata
            .OfType<EnableRateLimitingAttribute>()
            .Any(metadata => metadata.PolicyName == "public-read"));
    }

    private static OsintProxyService BuildService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://osiris:3000"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        return new OsintProxyService(
            new StubHttpClientFactory(client),
            NullLogger<OsintProxyService>.Instance);
    }

    private static DefaultHttpContext BuildContext(string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }

        return context;
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = header.Value.ToArray();
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    Headers[header.Key] = header.Value.ToArray();
                }

                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responseFactory(request);
        }
    }
}
