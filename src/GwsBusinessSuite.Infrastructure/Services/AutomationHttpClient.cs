using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using GwsBusinessSuite.Application.Automation;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class AutomationHttpClient(HttpClient httpClient) : IAutomationHttpClient
{
    private const int MaxResponseBytes = 5 * 1024 * 1024;

    public async Task<AutomationHttpResponse> SendAsync(
        AutomationHttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(request.Url, UriKind.Absolute);
        await ValidatePublicDestinationAsync(uri, cancellationToken);

        using var message = new HttpRequestMessage(request.Method, uri);
        if (request.Body is not null && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            message.Content = new StringContent(request.Body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        foreach (var header in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value) && message.Content is not null)
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (limited.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("HTTP response exceeded the 5 MB workflow safety limit.");
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var body = System.Text.Encoding.UTF8.GetString(limited.ToArray());
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        return new AutomationHttpResponse((int)response.StatusCode, body, headers);
    }

    private static async Task ValidatePublicDestinationAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Only HTTP and HTTPS workflow requests are allowed.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workflow HTTP requests cannot target localhost.");

        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidOperationException("The workflow HTTP destination could not be resolved.", ex);
        }
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReserved))
            throw new InvalidOperationException("Workflow HTTP requests cannot target private, link-local, or reserved network addresses.");
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || bytes[0] >= 224;
    }
}
