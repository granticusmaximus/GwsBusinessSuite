using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GwsBusinessSuite.Web;

public static class OsintProxyEndpointRouteBuilderExtensions
{
    private static readonly string[] ProxiedMethods = [HttpMethods.Get, HttpMethods.Post, HttpMethods.Head];

    public static IEndpointRouteBuilder MapOsintProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapProxyRoute(endpoints, "/_next/{**path}", static path => $"_next/{path}");
        MapProxyRoute(endpoints, "/api/{**path}", static path => $"api/{path}");
        MapProxyRoute(endpoints, "/data/{**path}", static path => $"data/{path}");
        MapProxyRoute(
            endpoints,
            $"{OsintProxyService.ShellPrefix}/{{**path}}",
            static path => path,
            rewriteDocumentPaths: true);

        return endpoints;
    }

    private static void MapProxyRoute(
        IEndpointRouteBuilder endpoints,
        string pattern,
        Func<string, string> targetPath,
        bool rewriteDocumentPaths = false)
    {
        endpoints.MapMethods(
                pattern,
                ProxiedMethods,
                async (string? path, HttpContext context, [FromServices] OsintProxyService proxy, CancellationToken cancellationToken) =>
                    await proxy.ForwardAsync(
                        context,
                        targetPath(path ?? string.Empty),
                        rewriteDocumentPaths,
                        cancellationToken))
            .RequireAuthorization("AdminOnly")
            .RequireRateLimiting("public-read");
    }
}
