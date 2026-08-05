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
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Only HTTP and HTTPS workflow requests are allowed.");
        // Not the real security boundary (that's AutomationDestinationValidator, wired into
        // this HttpClient's SocketsHttpHandler.ConnectCallback in DependencyInjection.cs) -
        // just a fast, clearly-worded rejection for the obvious/common case before attempting
        // any DNS work.
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Workflow HTTP requests cannot target localhost.");

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
}

/// <summary>
/// SSRF guard for workflow-authored HTTP requests, applied as a
/// <see cref="SocketsHttpHandler.ConnectCallback"/> (wired up in DependencyInjection.cs)
/// rather than a one-time pre-check on the request URL.
///
/// The previous design validated only the original URL's resolved address, once, before the
/// request was sent. Two ways around that: (1) a workflow author points the node at a URL
/// they control that responds with a 302 to an internal address (e.g. the cloud metadata
/// endpoint, or http://localhost/admin/...) - HttpClient follows redirects by re-resolving
/// and reconnecting on its own, past the point where the original check ran; (2)
/// DNS-rebinding - resolve to a public IP for the validation lookup, then to a private IP
/// moments later when HttpClient independently re-resolves the same hostname to actually
/// connect.
///
/// A ConnectCallback runs for *every* TCP connection this HttpClient ever makes, including
/// ones opened to follow a redirect (AllowAutoRedirect can safely stay on - each hop gets
/// independently validated here). And because this method resolves DNS and immediately
/// connects to the specific address it just validated - no separate re-resolution step in
/// between - there's no window for DNS-rebinding either.
/// </summary>
public static class AutomationDestinationValidator
{
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            // A literal IP in the URL bypasses DNS entirely - exactly the case this guard
            // exists to catch, so validate it directly.
            EnsurePublic(host, [literalAddress]);
            return await ConnectToAddressAsync(literalAddress, context.DnsEndPoint.Port, cancellationToken);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidOperationException($"The workflow HTTP destination '{host}' could not be resolved.", ex);
        }

        EnsurePublic(host, addresses);
        return await ConnectToAddressAsync(addresses[0], context.DnsEndPoint.Port, cancellationToken);
    }

    private static void EnsurePublic(string host, IPAddress[] addresses)
    {
        // Reject the whole destination if *any* resolved address is private/reserved, not
        // just skip the bad ones - a hostname that resolves to a mix of public and internal
        // addresses is itself a red flag, not something to route around.
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReservedAddress))
        {
            throw new InvalidOperationException(
                $"'{host}' cannot be used as an automation HTTP destination (resolves to a private, link-local, or reserved network address).");
        }
    }

    private static async ValueTask<Stream> ConnectToAddressAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Public (not just for EnsurePublic's own use) so it's directly unit-testable without
    // going through a real DNS lookup + socket connection - see AutomationHttpClientTests.
    public static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        // Unwrap IPv4-mapped IPv6 (::ffff:127.0.0.1) so the IPv4 checks below actually apply
        // to it - previously these addresses skipped the IPv4 range checks entirely because
        // they report as AddressFamily.InterNetworkV6.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || IsIPv6UniqueLocal(address);
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || bytes[0] >= 224;
    }

    // fc00::/7 (Unique Local Addresses, RFC 4193 - includes the fd00::/8 range Docker/many
    // internal networks default to). Not covered by any IPAddress.IsIPv6* property.
    private static bool IsIPv6UniqueLocal(IPAddress address) =>
        (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
