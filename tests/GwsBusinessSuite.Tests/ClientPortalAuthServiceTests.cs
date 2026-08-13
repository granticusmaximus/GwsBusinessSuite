using FluentAssertions;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class ClientPortalAuthServiceTests
{
    [Fact]
    public async Task RequestLoginLinkAsync_ShouldNoOp_WhenNoContactMatchesTheEmail()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Service.RequestLoginLinkAsync("nobody@example.test", "https://app.example.test/client-portal/auth/consume");

        (await fixture.Db.ClientPortalLoginTokens.CountAsync()).Should().Be(0);
        fixture.Sender.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestLoginLinkAsync_ShouldMintAHashedTokenAndEmailTheLoginUrl_WhenTheEmailMatchesAContact()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera", "JAMIE@Example.test");

        await fixture.Service.RequestLoginLinkAsync("jamie@example.test", "https://app.example.test/client-portal/auth/consume");

        var stored = await fixture.Db.ClientPortalLoginTokens.SingleAsync();
        stored.ContactId.Should().Be(contact.Id);
        stored.ConsumedAt.Should().BeNull();

        fixture.Sender.Messages.Should().ContainSingle();
        var message = fixture.Sender.Messages.Single();
        message.ToEmail.Should().Be("JAMIE@Example.test");

        // The raw token embedded in the emailed link is never the same string persisted to the
        // database - only its SHA-256 hash is stored.
        var token = new Uri(message.LoginUrl).Query.TrimStart('?').Split('=')[1];
        stored.TokenHash.Should().NotBe(token);
    }

    [Fact]
    public async Task ConsumeLoginLinkAsync_ShouldReturnNull_ForAnUnknownToken()
    {
        await using var fixture = await Fixture.CreateAsync();

        (await fixture.Service.ConsumeLoginLinkAsync("not-a-real-token")).Should().BeNull();
    }

    [Fact]
    public async Task ConsumeLoginLinkAsync_ShouldResolveTheContact_AndThenRejectTheSameTokenOnASecondUse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.RequestLoginLinkAsync("jamie@example.test", "https://app.example.test/client-portal/auth/consume");
        var token = fixture.ExtractTokenFromLastEmail();

        var resolved = await fixture.Service.ConsumeLoginLinkAsync(token);

        resolved.Should().NotBeNull();
        resolved!.ContactId.Should().Be(contact.Id);
        resolved.FullName.Should().Be("Jamie Rivera");

        // Single-use: the same token must never resolve a second time.
        (await fixture.Service.ConsumeLoginLinkAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task ConsumeLoginLinkAsync_ShouldReturnNull_WhenTheTokenHasExpired()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.RequestLoginLinkAsync("jamie@example.test", "https://app.example.test/client-portal/auth/consume");
        var token = fixture.ExtractTokenFromLastEmail();

        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(16));

        (await fixture.Service.ConsumeLoginLinkAsync(token)).Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, CapturingSender sender, FixedTimeProvider timeProvider)
        {
            _connection = connection;
            Db = db;
            Sender = sender;
            TimeProvider = timeProvider;
            Service = new ClientPortalAuthService(db, sender, timeProvider, NullLogger<ClientPortalAuthService>.Instance);
        }

        public ApplicationDbContext Db { get; }
        public CapturingSender Sender { get; }
        public FixedTimeProvider TimeProvider { get; }
        public ClientPortalAuthService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, new CapturingSender(), new FixedTimeProvider(DateTimeOffset.UtcNow));
        }

        public async Task<Contact> AddContactAsync(string fullName, string email)
        {
            var contact = new Contact { FullName = fullName, Email = email };
            Db.Contacts.Add(contact);
            await Db.SaveChangesAsync();
            return contact;
        }

        public string ExtractTokenFromLastEmail()
        {
            var loginUrl = Sender.Messages.Last().LoginUrl;
            return new Uri(loginUrl).Query.TrimStart('?').Split('=')[1];
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CapturingSender : IClientPortalEmailSender
    {
        public List<(string ToEmail, string ContactName, string LoginUrl)> Messages { get; } = [];

        public Task SendLoginLinkAsync(string toEmail, string contactName, string loginUrl, CancellationToken cancellationToken = default)
        {
            Messages.Add((toEmail, contactName, loginUrl));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
