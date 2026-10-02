using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// The transport settings every email feature (form notifications/Growth reports, bookings,
// email campaigns, the client portal) already carries under its own config section.
public interface ISmtpTransportOptions
{
    string Host { get; set; }
    int Port { get; set; }
    string Security { get; set; }
    string Username { get; set; }
    string Password { get; set; }
    string FromAddress { get; set; }
    string PickupDirectory { get; set; }
}

// One shared "Smtp" section that every email feature falls back to, so a deployment configures
// its mail server once (Smtp__Host, Smtp__Username, ... in .env) instead of repeating it per
// feature. A feature's own section still wins whenever it sets a server (Host or
// PickupDirectory); its FromAddress likewise wins when set. Sender display names stay per feature.
public static class SharedSmtpOptions
{
    public const string SectionName = "Smtp";

    public static OptionsBuilder<T> WithSharedSmtpFallback<T>(this OptionsBuilder<T> builder, IConfiguration configuration)
        where T : class, ISmtpTransportOptions =>
        builder.PostConfigure(options => ApplyFallback(options, configuration.GetSection(SectionName)));

    public static void ApplyFallback(ISmtpTransportOptions options, IConfigurationSection shared)
    {
        var hasOwnServer = !string.IsNullOrWhiteSpace(options.Host) || !string.IsNullOrWhiteSpace(options.PickupDirectory);
        if (!hasOwnServer)
        {
            options.Host = shared["Host"] ?? string.Empty;
            options.PickupDirectory = shared["PickupDirectory"] ?? string.Empty;
            if (int.TryParse(shared["Port"], out var port)) options.Port = port;
            if (!string.IsNullOrWhiteSpace(shared["Security"])) options.Security = shared["Security"]!;
            options.Username = shared["Username"] ?? string.Empty;
            options.Password = shared["Password"] ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            options.FromAddress = shared["FromAddress"] ?? string.Empty;
        }
    }
}
