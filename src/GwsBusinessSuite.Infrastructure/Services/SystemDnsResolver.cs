using System.Net;
using GwsBusinessSuite.Application.ThreatIntel;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(hostName, cancellationToken);
}
