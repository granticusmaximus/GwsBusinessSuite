using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CjAds;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CjAdsServiceTests
{
    [Fact]
    public async Task SaveConnectorSettingsAsync_ShouldPersistEncryptedDeveloperKey_AndReturnDecryptedValue()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeSecretProtector());

        await service.SaveConnectorSettingsAsync(new CjConnectorSettingsView
        {
            DeveloperKey = "dev-key-123",
            PublisherId = "pub-1",
            WebsiteId = "site-1",
            EndpointUrl = "https://commissions.api.cj.com/query",
            MaxResults = 100
        });

        var stored = await db.CjConnectorSettings.AsNoTracking().SingleAsync();
        Assert.Equal("enc::dev-key-123", stored.DeveloperKey);

        var reloaded = await service.GetConnectorSettingsAsync();
        Assert.NotNull(reloaded);
        Assert.Equal("dev-key-123", reloaded!.DeveloperKey);
    }

    [Fact]
    public async Task GetConnectorSettingsAsync_ShouldSupportLegacyPlaintextDeveloperKey()
    {
        await using var db = await CreateDbAsync();
        db.CjConnectorSettings.Add(new GwsBusinessSuite.Domain.Entities.CjConnectorSettings
        {
            DeveloperKey = "legacy-plain",
            PublisherId = "pub-1",
            WebsiteId = "",
            EndpointUrl = "https://commissions.api.cj.com/query",
            MaxResults = 50
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());
        var settings = await service.GetConnectorSettingsAsync();

        Assert.NotNull(settings);
        Assert.Equal("legacy-plain", settings!.DeveloperKey);
    }

    private static CjAdsService CreateService(ApplicationDbContext db, ISecretProtector secretProtector)
    {
        return new CjAdsService(
            db,
            new FakeCjAffiliateService(),
            secretProtector,
            NullLogger<CjAdsService>.Instance);
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext)
        {
            return string.IsNullOrWhiteSpace(plaintext) ? string.Empty : $"enc::{plaintext}";
        }

        public string Unprotect(string protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return string.Empty;
            }

            if (!protectedValue.StartsWith("enc::", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Value is not encrypted with expected prefix.");
            }

            return protectedValue[5..];
        }
    }

    private sealed class FakeCjAffiliateService : ICjAffiliateService
    {
        public Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new CjConnectionValidationResult(true, "ok", 0));
        }

        public Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new CjPartnerFetchResult(Array.Empty<CjPartnerRecord>(), "ok"));
        }
    }
}
