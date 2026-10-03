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

    // Feature sections that, when the shared section has no server, can lend theirs to every
    // other feature - so a deployment whose only mail settings are e.g. GrowthReportEmail__* (the
    // original single SMTP section) still delivers bookings, campaigns and portal mail too.
    public static readonly string[] DonorSectionNames =
        ["GrowthReportEmail", "EmailCampaignEmail", "BookingEmail", "ClientPortalEmail"];

    // Last-resort From address when no section sets one - the business's own mailbox.
    public const string DefaultFromAddress = "grant@gwsapp.net";

    public static OptionsBuilder<T> WithSharedSmtpFallback<T>(this OptionsBuilder<T> builder, IConfiguration configuration)
        where T : class, ISmtpTransportOptions =>
        builder.PostConfigure(options => ApplyFallback(options, configuration));

    public static void ApplyFallback(ISmtpTransportOptions options, IConfiguration configuration)
    {
        ApplyFallback(options, ResolveSharedSection(configuration));
        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            // A Gmail-style login doubles as the natural From address.
            options.FromAddress = options.Username.Contains('@') ? options.Username.Trim() : DefaultFromAddress;
        }
    }

    // The shared section when it names a server, otherwise the first feature section that does.
    public static IConfigurationSection ResolveSharedSection(IConfiguration configuration)
    {
        var shared = configuration.GetSection(SectionName);
        if (HasServer(shared)) return shared;
        foreach (var name in DonorSectionNames)
        {
            var donor = configuration.GetSection(name);
            if (HasServer(donor)) return donor;
        }

        return shared;
    }

    private static bool HasServer(IConfigurationSection section) =>
        !string.IsNullOrWhiteSpace(section["Host"]) || !string.IsNullOrWhiteSpace(section["PickupDirectory"]);

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
