using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// The app's mail account as the senders see it (IMailServerSettingsService). Every sender asks
// IMailTransport.Describe synchronously, so this keeps a snapshot of MailServerSettings: loaded at
// startup and every few minutes (MailTransport.RefreshGmailStatusAsync), and set right after a save.
public sealed class AppMailAccount(IServiceScopeFactory? scopeFactory = null, ILogger<AppMailAccount>? logger = null)
{
    private volatile AppMailAccountOptions? _current;

    // Null unless the account is saved and turned on.
    public AppMailAccountOptions? Current => _current;

    public void Set(AppMailAccountOptions? account) => _current = account;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (scopeFactory is null) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var row = await db.MailServerSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            Set(From(row, scope.ServiceProvider.GetRequiredService<ISecretProtector>()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Couldn't load the app's mail account.");
        }
    }

    public static AppMailAccountOptions? From(MailServerSettings? row, ISecretProtector protector) =>
        row is { Enabled: true, Host.Length: > 0 }
            ? new AppMailAccountOptions
            {
                Host = row.Host,
                Port = row.Port,
                Security = row.Security,
                Username = row.Username,
                Password = row.PasswordProtected.Length > 0 ? protector.Unprotect(row.PasswordProtected) : string.Empty,
                FromAddress = row.FromAddress.Length > 0 ? row.FromAddress : row.Username
            }
            : null;
}

public sealed class AppMailAccountOptions : ISmtpTransportOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = MailServerSecurity.StartTls;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string PickupDirectory { get; set; } = string.Empty;
}

public sealed class MailServerSettingsService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    AppMailAccount account,
    TimeProvider timeProvider,
    ISecurityAuditService? securityAudit = null) : IMailServerSettingsService
{
    public async Task<MailServerSettingsView> GetAsync(CancellationToken cancellationToken = default) =>
        ToView(await db.MailServerSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken));

    public async Task<MailServerSettingsView> SaveAsync(MailServerSettingsUpdate update, string performedBy, CancellationToken cancellationToken = default)
    {
        var settings = await db.MailServerSettings.FirstOrDefaultAsync(cancellationToken);
        var (host, username, fromAddress) = Validate(update, hasSavedPassword: settings is { PasswordProtected.Length: > 0 });
        if (settings is null)
        {
            settings = new MailServerSettings { CreatedBy = performedBy };
            db.MailServerSettings.Add(settings);
        }
        var changedLogin = !string.Equals(settings.Host, host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(settings.Username, username, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(update.Password);
        settings.Enabled = update.Enabled;
        settings.Host = host;
        settings.Port = update.Port;
        settings.Security = update.Security;
        settings.Username = username;
        if (!string.IsNullOrEmpty(update.Password)) settings.PasswordProtected = secretProtector.Protect(update.Password.Replace(" ", string.Empty));
        settings.FromAddress = fromAddress;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        account.Set(AppMailAccount.From(settings, secretProtector));

        if (securityAudit is not null)
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.Integration, "MailServerSettingsSaved", SecurityAuditOutcomes.Succeeded,
                Severity: changedLogin ? SecurityAuditSeverities.Warning : SecurityAuditSeverities.Information,
                TargetType: "MailServerSettings", TargetId: settings.Id.ToString(),
                Details: new Dictionary<string, string?>
                {
                    ["enabled"] = update.Enabled ? "true" : "false", ["host"] = host, ["username"] = username,
                    ["passwordChanged"] = string.IsNullOrEmpty(update.Password) ? "false" : "true"
                },
                ActorUsername: performedBy), cancellationToken);
        return ToView(settings);
    }

    public async Task<MailServerCheckResult> CheckAsync(MailServerSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        var settings = await db.MailServerSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        string host, username;
        try
        {
            (host, username, _) = Validate(update with { Enabled = true }, hasSavedPassword: settings is { PasswordProtected.Length: > 0 });
        }
        catch (MailServerSettingsException ex)
        {
            return new(false, ex.Message);
        }
        var password = !string.IsNullOrEmpty(update.Password) ? update.Password.Replace(" ", string.Empty)
            : settings is { PasswordProtected.Length: > 0 } ? secretProtector.Unprotect(settings.PasswordProtected) : string.Empty;
        try
        {
            using var client = new SmtpClient { Timeout = 15_000 };
            await client.ConnectAsync(host, update.Port, MailTransport.ParseSecurity(update.Security), cancellationToken);
            if (username.Length > 0) await client.AuthenticateAsync(username, password, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return new(true, username.Length > 0 ? $"Signed in to {host} as {username}." : $"Connected to {host}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, $"{host} said: {ex.Message}");
        }
    }

    // The trimmed host, login and From address; throws with what to fix.
    private static (string Host, string Username, string FromAddress) Validate(MailServerSettingsUpdate update, bool hasSavedPassword)
    {
        var host = (update.Host ?? string.Empty).Trim();
        var username = (update.Username ?? string.Empty).Trim();
        var fromAddress = (update.FromAddress ?? string.Empty).Trim();
        if (host.Length > 200 || host.Contains(' ') || host.Contains('/') || (update.Enabled && host.Length == 0))
            throw new MailServerSettingsException("Enter the SMTP server's host name, e.g. smtp.gmail.com.");
        if (update.Port is < 1 or > 65535)
            throw new MailServerSettingsException("The port must be between 1 and 65535 (587 for STARTTLS, 465 for SSL).");
        if (!MailServerSecurity.All.Contains(update.Security))
            throw new MailServerSettingsException("Choose how to secure the connection.");
        if (username.Length > 320)
            throw new MailServerSettingsException("That sign-in name is too long.");
        if (update.Enabled && username.Length > 0 && string.IsNullOrEmpty(update.Password) && !hasSavedPassword)
            throw new MailServerSettingsException("Enter the account's password (an app password for Google, iCloud and others).");
        if (fromAddress.Length > 0 && !MailboxAddress.TryParse(fromAddress, out _))
            throw new MailServerSettingsException($"'{fromAddress}' isn't a valid email address.");
        if (update.Enabled && fromAddress.Length == 0 && !username.Contains('@'))
            throw new MailServerSettingsException("Enter the From address mail should come from.");
        return (host, username, fromAddress);
    }

    private static MailServerSettingsView ToView(MailServerSettings? settings) =>
        settings is null
            ? new(false, string.Empty, 587, MailServerSecurity.StartTls, string.Empty, false, string.Empty, null, null)
            : new(settings.Enabled, settings.Host, settings.Port, settings.Security, settings.Username, settings.PasswordProtected.Length > 0,
                settings.FromAddress, settings.UpdatedAt, settings.UpdatedBy);
}
