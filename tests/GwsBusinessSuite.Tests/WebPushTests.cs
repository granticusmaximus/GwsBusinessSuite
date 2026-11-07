using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Web Push for the installed app (PWA phase 2, 07-11-26): the server's key, the devices, what's
// sent (decrypted here as the phone would, RFC 8291), and the bell's notifications going out.
public sealed class WebPushTests : IAsyncLifetime
{
    private const string AppleEndpoint = "https://web.push.apple.com/QGuQyavXutnMH-test";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ApplicationDbContext> _db = null!;
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly List<(HttpRequestMessage Request, byte[] Body)> _requests = [];
    private Func<HttpRequestMessage, HttpStatusCode> _answer = _ => HttpStatusCode.Created;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using var db = await _db.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(
            new AppUser { Username = "grant", Role = AppRoles.Admin },
            new AppUser { Username = "ana", Role = AppRoles.Author },
            new AppUser { Username = "old-admin", Role = AppRoles.Admin, IsActive = false });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:APA91b", true)]
    [InlineData("https://web.push.apple.com/QGuQyavXut", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA", true)]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=BQYAAA", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://user@fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com.example.com/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://grantwatson.dev/admin/api/push/test", false)]
    [InlineData("not a url", false)]
    public void Endpoints_ShouldOnlyBeTheBrowsersOwnPushServices(string endpoint, bool accepted) =>
        WebPushService.IsPushServiceEndpoint(endpoint).Should().Be(accepted, "anything else would let a signed-in user make the server send requests anywhere");

    [Fact]
    public async Task ServerKey_ShouldBeMadeOnce_KeptEncrypted_AndMadeAgainIfItCantBeRead()
    {
        var first = await Service().PublicKeyAsync();
        (await Service().PublicKeyAsync()).Should().Be(first, "a new instance reads the saved key");
        WebPushService.Base64Url(first).Should().HaveCount(65).And.StartWith(new byte[] { 0x04 });

        await using (var db = await _db.CreateDbContextAsync())
        {
            var row = await db.WebPushKeys.SingleAsync();
            row.PublicKey.Should().Be(first);
            row.ProtectedPrivateKey.Length.Should().BeGreaterThan(60, "the private key is stored encrypted, not as its 43 characters");
        }

        // Lost Data Protection keys: the old key can't be read, so a new one, and the devices
        // (subscribed to the old key) are dropped.
        var (p256dh, auth, _) = DeviceKeys();
        await Service().SubscribeAsync("grant", AppleEndpoint, p256dh, auth, "iPhone (app)");
        var other = new WebPushService(_db, new EphemeralDataProtectionProvider(), new Clients(this), TimeProvider.System, NullLogger<WebPushService>.Instance);
        (await other.PublicKeyAsync()).Should().NotBe(first);
        (await other.DevicesAsync("grant")).Should().BeEmpty();
    }

    [Fact]
    public async Task Subscribe_ShouldCheckTheDevicesKeys_MoveAnEndpointBetweenPeople_AndKeepTheNewest20()
    {
        var service = Service();
        var (p256dh, auth, _) = DeviceKeys();
        await service.Invoking(s => s.SubscribeAsync("grant", "https://example.com/push", p256dh, auth, null)).Should().ThrowAsync<InvalidOperationException>();
        await service.Invoking(s => s.SubscribeAsync("grant", AppleEndpoint, "not-a-key", auth, null)).Should().ThrowAsync<InvalidOperationException>();
        await service.Invoking(s => s.SubscribeAsync("grant", AppleEndpoint, p256dh, "c2hvcnQ", null)).Should().ThrowAsync<InvalidOperationException>();

        await service.SubscribeAsync("ana", AppleEndpoint, p256dh, auth, "iPad (Safari)");
        await service.SubscribeAsync("grant", AppleEndpoint, p256dh, auth, "iPad (app)");
        (await service.DevicesAsync("ana")).Should().BeEmpty("one row per endpoint: the browser now belongs to whoever subscribed last");
        (await service.DevicesAsync("grant")).Should().ContainSingle().Which.Device.Should().Be("iPad (app)");

        for (var i = 0; i < WebPushService.MaxDevicesPerPerson + 3; i++)
            await service.SubscribeAsync("grant", $"https://fcm.googleapis.com/fcm/send/device-{i}", p256dh, auth, $"Device {i}");
        var devices = await service.DevicesAsync("grant");
        devices.Should().HaveCount(WebPushService.MaxDevicesPerPerson);
        devices.Select(d => d.Device).Should().Contain($"Device {WebPushService.MaxDevicesPerPerson + 2}").And.NotContain("iPad (app)");

        (await service.UnsubscribeAsync("ana", devices[0].Endpoint)).Should().BeFalse("only your own devices");
        (await service.UnsubscribeAsync("grant", devices[0].Endpoint)).Should().BeTrue();
    }

    [Fact]
    public async Task Send_ShouldSignWithTheServersKey_AndEncryptTheNoticeSoOnlyTheDeviceCanReadIt()
    {
        var service = Service();
        var (p256dh, auth, device) = DeviceKeys();
        await service.SubscribeAsync("grant", AppleEndpoint, p256dh, auth, "iPhone (app)");
        await service.SubscribeAsync("ana", "https://fcm.googleapis.com/fcm/send/ana", p256dh, auth, "Android (app)");

        var delivered = await service.SendAsync(["GRANT"], new PushNotice("World News: Floods in Istanbul", "64 outlets now report it.",
            "/admin/world-news?situation=s1", "world-news:1"));

        delivered.Should().Be(1, "only Grant's device (names match whatever their case)");
        var (request, body) = _requests.Single();
        request.RequestUri!.ToString().Should().Be(AppleEndpoint);
        request.Method.Should().Be(HttpMethod.Post);
        request.Headers.Authorization!.Scheme.Should().Be("vapid");
        request.Headers.Authorization.Parameter.Should().Contain("t=").And.Contain("k=" + await service.PublicKeyAsync());
        request.Headers.GetValues("TTL").Single().Should().Be("43200");
        request.Content!.Headers.ContentEncoding.Should().Equal("aes128gcm");

        using var payload = JsonDocument.Parse(Decrypt(body, device, WebPushService.Base64Url(auth)!));
        payload.RootElement.GetProperty("title").GetString().Should().Be("World News: Floods in Istanbul");
        payload.RootElement.GetProperty("body").GetString().Should().Be("64 outlets now report it.");
        payload.RootElement.GetProperty("url").GetString().Should().Be("/admin/world-news?situation=s1");
        payload.RootElement.GetProperty("tag").GetString().Should().Be("world-news:1");

        (await service.DevicesAsync("grant")).Single().LastDeliveredAtUnix.Should().NotBeNull();
        WebPushService.Payload(new PushNotice("t", "b", "https://evil.example/", "x")).Should().Contain("\"url\":\"/admin\"", "a push only ever opens the app");
    }

    [Fact]
    public async Task Send_ShouldForgetDevicesThePushServiceSaysAreGone_AndOnesThatKeepFailing()
    {
        var service = Service();
        var (p256dh, auth, _) = DeviceKeys();
        await service.SubscribeAsync("grant", AppleEndpoint, p256dh, auth, "iPhone (app)");
        await service.SubscribeAsync("grant", "https://fcm.googleapis.com/fcm/send/flaky", p256dh, auth, "Android (app)");
        _answer = request => request.RequestUri!.Host == "web.push.apple.com" ? HttpStatusCode.Gone : HttpStatusCode.InternalServerError;

        (await service.SendAsync(["grant"], new PushNotice("t", "b", "/admin", "x"))).Should().Be(0);
        (await service.DevicesAsync("grant")).Select(d => d.Device).Should().Equal("Android (app)");
        for (var i = 0; i < 4; i++) await service.SendAsync(["grant"], new PushNotice("t", "b", "/admin", "x"));
        (await service.DevicesAsync("grant")).Should().BeEmpty("five failures in a row");

        (await service.AdminsWithDevicesAsync()).Should().BeEmpty();
        await service.SubscribeAsync("grant", AppleEndpoint, p256dh, auth, null);
        await service.SubscribeAsync("ana", "https://fcm.googleapis.com/fcm/send/ana", p256dh, auth, null);
        await service.SubscribeAsync("old-admin", "https://fcm.googleapis.com/fcm/send/old", p256dh, auth, null);
        (await service.AdminsWithDevicesAsync()).Should().Equal("grant");
    }

    [Fact]
    public async Task Dispatcher_ShouldSendTheBellsNotifications_ToWhoeverTheBellShowsThemTo()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gws-webpush", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
            ["WorldNews:DatabasePath"] = Path.Combine(folder, "worldnews.db")
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        var sender = new RecordingSender(["grant"]);
        services.AddSingleton<IWebPushSender>(sender);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = ActivatorUtilities.CreateInstance<WebPushDispatcher>(provider);
        await dispatcher.StartAsync(default);

        var thread = Guid.NewGuid();
        var chat = provider.GetRequiredService<ChatNotifier>();
        chat.Publish(new ChatMessageNotice(thread, "Ops", true, "ana", "Ana", "Deploy done", ["grant", "ana"], DateTimeOffset.UtcNow));
        var docker = new DockerHealthAlertView { Id = Guid.NewGuid(), ContainerName = "gwssuite-web", Severity = "error", Message = "Restarted 3 times", CreatedAt = DateTimeOffset.UtcNow };
        provider.GetRequiredService<DockerHealthNotifier>().Publish(docker);
        provider.GetRequiredService<DockerHealthNotifier>().Publish(docker);
        provider.GetRequiredService<OverwatchAreaNotifier>().Raise("ana", new OverwatchAreaAlertView(Guid.NewGuid(), Guid.NewGuid(), "Home", "Road closed nearby", false, DateTimeOffset.UtcNow));

        await sender.WaitForAsync(3);
        await Task.Delay(300);
        sender.Sent.Should().HaveCount(3, "the same container alert twice is pushed once");
        var message = sender.Sent.Single(s => s.Notice.Tag.StartsWith("chat:", StringComparison.Ordinal));
        message.Users.Should().Equal(new[] { "grant" }, "not to the person who sent it");
        message.Notice.Should().Be(new PushNotice("Messages: Ops", "Ana: Deploy done", $"/admin/community/messages/{thread}", $"chat:{thread}"));
        var container = sender.Sent.Single(s => s.Notice.Tag.StartsWith("docker:", StringComparison.Ordinal));
        container.Users.Should().Equal(new[] { "grant" }, "server alerts go to admins with a device");
        container.Notice.Title.Should().Be("Containers: gwssuite-web");
        container.Notice.Url.Should().Be("/admin/docker-health/gwssuite-web");
        sender.Sent.Single(s => s.Notice.Tag.StartsWith("overwatch:", StringComparison.Ordinal)).Users.Should().Equal(new[] { "ana" });

        // Each chat message pushes (the tag replaces the last one on the phone); after stopping, nothing.
        chat.Publish(new ChatMessageNotice(thread, "Ops", true, "ana", "Ana", "And tested", ["grant", "ana"], DateTimeOffset.UtcNow));
        await sender.WaitForAsync(4);
        await dispatcher.StopAsync(default);
        chat.Publish(new ChatMessageNotice(thread, "Ops", true, "ana", "Ana", "Unheard", ["grant"], DateTimeOffset.UtcNow));
        await Task.Delay(300);
        sender.Sent.Should().HaveCount(4);

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private WebPushService Service() => new(_db, _protection, new Clients(this), TimeProvider.System, NullLogger<WebPushService>.Instance);

    // A device's keys as a browser makes them: a P-256 key pair and a 16-byte secret.
    private static (string P256dh, string Auth, ECDiffieHellman Device) DeviceKeys()
    {
        var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = device.ExportParameters(false).Q;
        return (WebPushService.ToBase64Url([0x04, .. q.X!, .. q.Y!]), WebPushService.ToBase64Url(RandomNumberGenerator.GetBytes(16)), device);
    }

    // RFC 8291 section 3 / RFC 8188, as the phone's browser does it.
    private static string Decrypt(byte[] body, ECDiffieHellman device, byte[] authSecret)
    {
        var salt = body[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4));
        recordSize.Should().BeGreaterThan(0u);
        var idLength = body[20];
        var serverKey = body[21..(21 + idLength)];
        var cipher = body[(21 + idLength)..];

        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverKey[1..33], Y = serverKey[33..65] }
        });
        var shared = device.DeriveRawSecretAgreement(server.PublicKey);
        var q = device.ExportParameters(false).Q;
        byte[] devicePublic = [0x04, .. q.X!, .. q.Y!];
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(devicePublic).Concat(serverKey).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var plain = new byte[cipher.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
        var end = Array.LastIndexOf(plain, (byte)0x02);
        end.Should().BeGreaterThan(0, "the last record ends with the 0x02 delimiter");
        return Encoding.UTF8.GetString(plain, 0, end);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class Clients(WebPushTests test) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(test));
    }

    private sealed class Handler(WebPushTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (test._requests) test._requests.Add((request, body));
            return new HttpResponseMessage(test._answer(request));
        }
    }

    private sealed class RecordingSender(IReadOnlyList<string> admins) : IWebPushSender
    {
        private readonly object _gate = new();
        private readonly List<(IReadOnlyCollection<string> Users, PushNotice Notice)> _sent = [];

        public IReadOnlyList<(IReadOnlyCollection<string> Users, PushNotice Notice)> Sent
        {
            get { lock (_gate) return _sent.ToList(); }
        }

        public Task<int> SendAsync(IReadOnlyCollection<string> usernames, PushNotice notice, CancellationToken cancellationToken = default)
        {
            lock (_gate) _sent.Add((usernames.ToList(), notice));
            return Task.FromResult(usernames.Count);
        }

        public Task<IReadOnlyList<string>> AdminsWithDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult(admins);

        public async Task WaitForAsync(int count)
        {
            for (var i = 0; i < 100 && Sent.Count < count; i++) await Task.Delay(50);
        }
    }
}
