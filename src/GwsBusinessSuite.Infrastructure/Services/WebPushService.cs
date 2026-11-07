using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// One notification as a push: what the phone shows, where tapping it goes (an /admin path), and a
// tag so a newer push about the same thing replaces the older one.
public sealed record PushNotice(string Title, string Body, string Url, string Tag);

public sealed record PushDevice(Guid Id, string Device, string Endpoint, DateTimeOffset AddedAt, long? LastDeliveredAtUnix);

public interface IWebPushSender
{
    // Sends to every device of these people; returns how many took it.
    Task<int> SendAsync(IReadOnlyCollection<string> usernames, PushNotice notice, CancellationToken cancellationToken = default);

    // Admins with at least one device (for server-wide alerts: containers, social posts).
    Task<IReadOnlyList<string>> AdminsWithDevicesAsync(CancellationToken cancellationToken = default);
}

// Web Push for the app installed on phones, tablets and computers (PWA phase 2, 07-11-26). The
// server signs each push with its own VAPID key pair, made once and kept in the database (the
// private half encrypted with Data Protection), so there's nothing to configure.
//
// A subscription is a URL the server will POST to, so only the browsers' own push services are
// accepted (https, and a known host): otherwise a signed-in user could make the server send
// requests anywhere.
public sealed class WebPushService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClients,
    TimeProvider timeProvider,
    ILogger<WebPushService> logger) : IWebPushSender
{
    public const int MaxDevicesPerPerson = 20;
    public const string HttpClientName = "web-push";
    public const string Subject = "https://grantwatson.dev";
    private const string ProtectorPurpose = "GwsBusinessSuite.WebPush.Vapid.v1";

    // Push services: Google (Chrome, Edge on Android, Android), Mozilla (Firefox), Apple (Safari on
    // iPhone, iPad and Mac), Microsoft (Edge on Windows).
    private static readonly string[] PushHosts = ["fcm.googleapis.com", "android.googleapis.com", "updates.push.services.mozilla.com", "push.services.mozilla.com"];
    private static readonly string[] PushHostSuffixes = [".push.apple.com", ".notify.windows.com"];

    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private (string PublicKey, string PrivateKey)? _keys;

    public static bool IsPushServiceEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (PushHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || PushHostSuffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

    // The public half, for the browser's pushManager.subscribe (applicationServerKey).
    public async Task<string> PublicKeyAsync(CancellationToken cancellationToken = default) => (await KeysAsync(cancellationToken)).PublicKey;

    public async Task<PushDevice> SubscribeAsync(string username, string endpoint, string p256dh, string auth, string? device, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("Sign in first.");
        if (!IsPushServiceEndpoint(endpoint)) throw new InvalidOperationException("That isn't a browser push service this app sends to.");
        if (Base64Url(p256dh) is not { Length: 65 } point || point[0] != 0x04) throw new InvalidOperationException("The device's encryption key isn't valid.");
        if (Base64Url(auth) is not { Length: 16 }) throw new InvalidOperationException("The device's authentication secret isn't valid.");
        var name = string.IsNullOrWhiteSpace(device) ? "This device" : device.Trim()[..Math.Min(device.Trim().Length, 60)];

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // One row per endpoint: subscribing again (or as someone else on a shared browser) moves it.
        var row = await db.WebPushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);
        if (row is null)
        {
            row = new WebPushSubscription { Username = username, Endpoint = endpoint, P256dh = p256dh, Auth = auth, Device = name, CreatedBy = username };
            db.WebPushSubscriptions.Add(row);
        }
        else
        {
            row.Username = username;
            row.P256dh = p256dh;
            row.Auth = auth;
            row.Device = name;
            row.Failures = 0;
            row.UpdatedAt = timeProvider.GetUtcNow();
            row.UpdatedBy = username;
        }
        await db.SaveChangesAsync(cancellationToken);

        // A cap per person: the oldest go first.
        var devices = (await db.WebPushSubscriptions.Where(s => s.Username == username).ToListAsync(cancellationToken))
            .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt).ToList();
        if (devices.Count > MaxDevicesPerPerson)
        {
            db.WebPushSubscriptions.RemoveRange(devices.Skip(MaxDevicesPerPerson));
            await db.SaveChangesAsync(cancellationToken);
        }
        return new PushDevice(row.Id, row.Device ?? name, row.Endpoint, row.CreatedAt, row.LastDeliveredAtUnix);
    }

    public async Task<bool> UnsubscribeAsync(string username, string endpoint, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WebPushSubscriptions.Where(s => s.Username == username && s.Endpoint == endpoint).ToListAsync(cancellationToken);
        db.WebPushSubscriptions.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count > 0;
    }

    public async Task<IReadOnlyList<PushDevice>> DevicesAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await db.WebPushSubscriptions.AsNoTracking().Where(s => s.Username == username).ToListAsync(cancellationToken))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new PushDevice(s.Id, s.Device ?? "This device", s.Endpoint, s.CreatedAt, s.LastDeliveredAtUnix))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> AdminsWithDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var admins = await db.AppUsers.AsNoTracking().Where(u => u.Role == AppRoles.Admin && u.IsActive).Select(u => u.Username).ToListAsync(cancellationToken);
        var withDevices = await db.WebPushSubscriptions.AsNoTracking().Select(s => s.Username).Distinct().ToListAsync(cancellationToken);
        return admins.Intersect(withDevices, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<int> SendAsync(IReadOnlyCollection<string> usernames, PushNotice notice, CancellationToken cancellationToken = default)
    {
        if (usernames.Count == 0) return 0;
        var wanted = usernames.Select(u => u.ToLowerInvariant()).ToHashSet();
        List<WebPushSubscription> targets;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            targets = (await db.WebPushSubscriptions.AsNoTracking().ToListAsync(cancellationToken))
                .Where(s => wanted.Contains(s.Username.ToLowerInvariant()) && IsPushServiceEndpoint(s.Endpoint))
                .ToList();
        }
        if (targets.Count == 0) return 0;

        var keys = await KeysAsync(cancellationToken);
        var client = new PushServiceClient(httpClients.CreateClient(HttpClientName))
        {
            DefaultAuthentication = new VapidAuthentication(keys.PublicKey, keys.PrivateKey) { Subject = Subject }
        };
        var content = Payload(notice);
        var delivered = new List<Guid>();
        var gone = new List<Guid>();
        var failed = new List<Guid>();
        foreach (var target in targets)
        {
            var subscription = new PushSubscription { Endpoint = target.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, target.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, target.Auth);
            var message = new PushMessage(content) { TimeToLive = (int)TimeSpan.FromHours(12).TotalSeconds, Urgency = PushMessageUrgency.Normal };
            try
            {
                await client.RequestPushMessageDeliveryAsync(subscription, message, cancellationToken);
                delivered.Add(target.Id);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                gone.Add(target.Id);
            }
            catch (Exception ex) when (ex is PushServiceClientException or HttpRequestException or TaskCanceledException)
            {
                logger.LogInformation(ex, "Web Push to {Username}'s {Device} wasn't delivered.", target.Username, target.Device);
                failed.Add(target.Id);
            }
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var ids = delivered.Concat(gone).Concat(failed).ToList();
            var rows = await db.WebPushSubscriptions.Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (gone.Contains(row.Id) || failed.Contains(row.Id) && row.Failures + 1 >= 5)
                {
                    db.WebPushSubscriptions.Remove(row);
                }
                else if (failed.Contains(row.Id))
                {
                    row.Failures++;
                }
                else
                {
                    row.Failures = 0;
                    row.LastDeliveredAtUnix = now;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return delivered.Count;
    }

    // What the service worker reads (sw.js "push"). Push services take about 4 KB.
    public static string Payload(PushNotice notice) => JsonSerializer.Serialize(new
    {
        title = Clip(notice.Title, 120),
        body = Clip(notice.Body, 300),
        url = notice.Url.StartsWith("/admin", StringComparison.Ordinal) ? notice.Url : "/admin",
        tag = Clip(notice.Tag, 100)
    });

    private async Task<(string PublicKey, string PrivateKey)> KeysAsync(CancellationToken cancellationToken)
    {
        if (_keys is { } known) return known;
        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_keys is { } loaded) return loaded;
            var protector = dataProtection.CreateProtector(ProtectorPurpose);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = (await db.WebPushKeys.ToListAsync(cancellationToken)).OrderBy(k => k.CreatedAt).FirstOrDefault();
            if (row is not null)
            {
                try
                {
                    _keys = (row.PublicKey, protector.Unprotect(row.ProtectedPrivateKey));
                    return _keys.Value;
                }
                catch (CryptographicException ex)
                {
                    // The Data Protection keys were lost: devices subscribed to the old key can't be
                    // reached any more, so start again (they're offered "Turn on" again).
                    logger.LogWarning(ex, "Web Push: the VAPID private key couldn't be read; making a new key pair. Devices need to turn notifications on again.");
                    db.WebPushKeys.RemoveRange(await db.WebPushKeys.ToListAsync(cancellationToken));
                    db.WebPushSubscriptions.RemoveRange(await db.WebPushSubscriptions.ToListAsync(cancellationToken));
                }
            }

            var (publicKey, privateKey) = NewKeyPair();
            db.WebPushKeys.Add(new WebPushKey { PublicKey = publicKey, ProtectedPrivateKey = protector.Protect(privateKey) });
            await db.SaveChangesAsync(cancellationToken);
            _keys = (publicKey, privateKey);
            return _keys.Value;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    // A P-256 key pair as VAPID wants it: the public key as an uncompressed point, the private key
    // as its 32-byte scalar, both base64url.
    public static (string PublicKey, string PrivateKey) NewKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);
        return (ToBase64Url(point), ToBase64Url(parameters.D!));
    }

    public static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? Base64Url(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return null;
        var padded = text.Trim().Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Clip(string? text, int max)
    {
        var flat = (text ?? string.Empty).Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }
}
