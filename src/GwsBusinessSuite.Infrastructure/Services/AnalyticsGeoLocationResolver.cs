using System.Net;
using GwsBusinessSuite.Application.Growth;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class AnalyticsGeoIpOptions
{
    public const string SectionName = "AnalyticsGeoIp";
    public const string DefaultDatabasePath = "/app/data/GeoLite2-City.mmdb";

    public string DatabasePath { get; set; } = DefaultDatabasePath;
}

public sealed class AnalyticsGeoLocationResolver : IAnalyticsGeoLocationResolver, IDisposable
{
    private readonly ILogger<AnalyticsGeoLocationResolver> logger;
    private readonly DatabaseReader? reader;

    public AnalyticsGeoLocationResolver(
        IOptions<AnalyticsGeoIpOptions> options,
        ILogger<AnalyticsGeoLocationResolver> logger)
    {
        this.logger = logger;
        var path = options.Value.DatabasePath?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            logger.LogInformation(
                "Analytics GeoIP enrichment is disabled because the local database is unavailable.");
            return;
        }

        try
        {
            reader = new DatabaseReader(path);
            logger.LogInformation("Analytics GeoIP enrichment is ready using the local database.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Analytics GeoIP enrichment is disabled because the local database could not be opened.");
        }
    }

    public bool IsConfigured => reader is not null;

    public AnalyticsGeoLocation? Resolve(IPAddress? address)
    {
        if (reader is null || !IsPublicAddress(address))
        {
            return null;
        }

        try
        {
            var result = reader.City(address!);
            var countryCode = result.Country.IsoCode ?? string.Empty;
            var countryName = result.Country.Name ?? string.Empty;
            var regionCode = result.MostSpecificSubdivision.IsoCode ?? string.Empty;
            var regionName = result.MostSpecificSubdivision.Name ?? string.Empty;
            if (countryCode.Length == 0 && regionCode.Length == 0)
            {
                return null;
            }

            return new AnalyticsGeoLocation(countryCode, countryName, regionCode, regionName);
        }
        catch (AddressNotFoundException)
        {
            return null;
        }
        catch (Exception exception)
        {
            // Never include the request address in logs. Analytics IPs are transient inputs.
            logger.LogWarning(exception, "A local analytics GeoIP lookup failed.");
            return null;
        }
    }

    public static bool IsPublicAddress(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && bytes[0] < 224;
        }

        return !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.IsIPv6SiteLocal
            && !address.Equals(IPAddress.IPv6None)
            && !address.Equals(IPAddress.IPv6Any)
            && (bytes[0] & 0xfe) != 0xfc;
    }

    public void Dispose() => reader?.Dispose();
}
