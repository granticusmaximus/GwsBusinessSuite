using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DigitalOceanService(HttpClient http) : IDigitalOceanService
{
    public Task<string> GetDropletsAsync(CancellationToken ct = default)
    {
        _ = http;
        return Task.FromResult("DigitalOcean integration is not configured for this environment.");
    }
}
