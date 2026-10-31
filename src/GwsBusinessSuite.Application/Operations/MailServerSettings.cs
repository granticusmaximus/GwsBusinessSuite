namespace GwsBusinessSuite.Application.Operations;

// The app's own mail account (Settings > Email, 31-10-26): what used to be only Smtp__* in .env.
// While it's on, every email the app sends goes out through it, ahead of .env and the connected
// Google account. The password never comes back out; HasPassword says one is saved.
public sealed record MailServerSettingsView(
    bool Enabled, string Host, int Port, string Security, string Username, bool HasPassword, string FromAddress,
    DateTimeOffset? UpdatedAt, string? UpdatedBy);

// Password: null or empty keeps the saved one.
public sealed record MailServerSettingsUpdate(
    bool Enabled, string Host, int Port, string Security, string Username, string? Password, string FromAddress);

public sealed record MailServerCheckResult(bool Succeeded, string Message);

public sealed class MailServerSettingsException(string message) : Exception(message);

public static class MailServerSecurity
{
    public const string StartTls = "StartTls";
    public const string SslOnConnect = "SslOnConnect";
    public const string Auto = "Auto";
    public const string None = "None";
    public static readonly IReadOnlyList<string> All = [StartTls, SslOnConnect, Auto, None];
}

public interface IMailServerSettingsService
{
    Task<MailServerSettingsView> GetAsync(CancellationToken cancellationToken = default);
    // Throws MailServerSettingsException with what to fix.
    Task<MailServerSettingsView> SaveAsync(MailServerSettingsUpdate update, string performedBy, CancellationToken cancellationToken = default);
    // Signs in to the server with these settings (the saved password when none is typed), sends nothing.
    Task<MailServerCheckResult> CheckAsync(MailServerSettingsUpdate update, CancellationToken cancellationToken = default);
}
