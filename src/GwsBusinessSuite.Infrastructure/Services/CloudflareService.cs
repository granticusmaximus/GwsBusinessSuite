using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class CloudflareService(HttpClient http) : ICloudflareService
{
    public Task<string> CreateSubdomainRouteAsync(string subdomain, int port, CancellationToken ct = default)
    {
        _ = http;
        return Task.FromResult($"Cloudflare integration is not configured. Requested route: {subdomain} -> localhost:{port}.");
    }
}
