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
    public async Task GetConnectorSettingsAsync_ShouldFlagUndecryptableDeveloperKeyAsUnreadable()
    {
        // A value stored without the expected "enc::" prefix can never be decrypted
        // (whether it's legacy plaintext or ciphertext from a rotated key ring), so it
        // must be surfaced as unreadable rather than returned as a usable key.
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
        Assert.Equal(string.Empty, settings!.DeveloperKey);
        Assert.True(settings.DeveloperKeyUnreadable);
    }

    [Fact]
    public async Task GetOffersForAdvertiserAsync_ShouldReturnMatchingCJOffersOnly()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.AddRange(
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "northwind-1",
                AdvertiserName = "Northwind",
                LinkName = "northwind-1",
                Category = "Software",
                TrackingUrl = "https://example.com/northwind-1",
                CreatedBy = "test"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "northwind-1",
                AdvertiserName = "Northwind",
                LinkName = "Laptop Deal",
                Category = "Deals",
                TrackingUrl = "https://example.com/northwind-2",
                CreatedBy = "test"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "Other",
                AdvertiserId = "northwind-1",
                AdvertiserName = "Northwind",
                LinkName = "northwind-other",
                Category = "Ignored",
                TrackingUrl = "https://example.com/ignore",
                CreatedBy = "test"
            });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());

        var offers = await service.GetOffersForAdvertiserAsync("northwind-1", "Northwind");

        Assert.Single(offers);
        Assert.All(offers, offer => Assert.Equal("Northwind", offer.AdvertiserName));
        Assert.Equal("northwind-1", offers[0].AdvertiserId);
        Assert.Equal("Laptop Deal", offers[0].LinkName);
        Assert.True(offers[0].IsImportedCatalogOffer);
    }

    [Fact]
    public async Task ImportOffersAsync_ShouldAddCatalogRowsAndKeepPartnerListingDeduped()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.Add(new GwsBusinessSuite.Domain.Entities.AffiliateOffer
        {
            Network = "CJ",
            AdvertiserId = "northwind-1",
            AdvertiserName = "Northwind",
            LinkName = "northwind-1",
            Category = "Joined",
            TrackingUrl = "https://example.com/partner",
            CreatedBy = "cj-sync"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());

        var result = await service.ImportOffersAsync(new CjOfferImportRequest
        {
            AdvertiserId = "northwind-1",
            AdvertiserName = "Northwind",
            Format = "CSV",
            ReplaceExistingOffers = true,
            Payload = "offerName,category,trackingUrl,promotionEndsAt\nLaptop Deal,Electronics,https://example.com/laptop,2026-12-31\nServer Bundle,Infrastructure,https://example.com/server,2027-01-31"
        });

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deleted);

        var partners = await service.ListPartnersAsync();
        Assert.Single(partners);
        Assert.Equal("northwind-1", partners[0].AdvertiserId);
        Assert.Equal(2, partners[0].OfferCount);

        var offers = await service.GetOffersForAdvertiserAsync("northwind-1", "Northwind");
        Assert.Equal(2, offers.Count);
        Assert.DoesNotContain(offers, offer => offer.LinkName == "northwind-1");
        Assert.All(offers, offer => Assert.True(offer.IsImportedCatalogOffer));
    }

    [Fact]
    public async Task ImportOffersAsync_ShouldReplaceExistingImportedCatalogRowsOnly()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.AddRange(
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "northwind-1",
                AdvertiserName = "Northwind",
                LinkName = "northwind-1",
                Category = "Joined",
                TrackingUrl = "https://example.com/partner",
                CreatedBy = "cj-sync"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "northwind-1",
                AdvertiserName = "Northwind",
                LinkName = "Old Deal",
                Category = "Old",
                TrackingUrl = "https://example.com/old",
                CreatedBy = "cj-import"
            });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());

        var result = await service.ImportOffersAsync(new CjOfferImportRequest
        {
            AdvertiserId = "northwind-1",
            AdvertiserName = "Northwind",
            Format = "JSON",
            ReplaceExistingOffers = true,
            Payload = "[{\"offerName\":\"Fresh Deal\",\"category\":\"New\",\"trackingUrl\":\"https://example.com/fresh\"}]"
        });

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Deleted);

        var offers = await service.GetOffersForAdvertiserAsync("northwind-1", "Northwind");
        Assert.Single(offers);
        Assert.Equal("Fresh Deal", offers[0].LinkName);
    }

    [Fact]
    public async Task ListPartnersAsync_ShouldUseRelationshipStatusInsteadOfCategoryForFilters()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.AddRange(
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "joined-1",
                AdvertiserName = "Joined Advertiser",
                LinkName = "joined-1",
                RelationshipStatus = "joined",
                Category = "Software",
                TrackingUrl = "https://example.com/joined",
                CreatedBy = "cj-sync"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "pending-1",
                AdvertiserName = "Pending Advertiser",
                LinkName = "pending-1",
                RelationshipStatus = "pending",
                Category = "Hosting",
                TrackingUrl = "https://example.com/pending",
                CreatedBy = "cj-sync"
            });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());

        var joined = await service.ListPartnersAsync("Joined");

        Assert.Single(joined);
        Assert.Equal("joined", joined[0].RelationshipStatus, ignoreCase: true);
        Assert.Equal("Software", joined[0].PrimaryCategory);
    }

    [Fact]
    public async Task SyncPartnersAsync_ShouldKeepJoinedAdvertisersAndRemoveStaleRows()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.AddRange(
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "old-1",
                AdvertiserName = "Old Advertiser",
                LinkName = "old-1",
                Category = "Legacy",
                TrackingUrl = "https://example.com/old",
                CreatedBy = "cj-sync"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "stale-import",
                AdvertiserName = "Stale Import",
                LinkName = "Imported Deal",
                Category = "Legacy",
                TrackingUrl = "https://example.com/stale",
                CreatedBy = "cj-import"
            });
        await db.SaveChangesAsync();

        var service = new CjAdsService(
            db,
            new FakeCjAffiliateService(
            [
                new CjPartnerRecord("joined-1", "Joined One", "joined", "US", "Software", "https://example.com/joined-1"),
                new CjPartnerRecord("joined-2", "Joined Two", "approved", "US", "Hosting", "https://example.com/joined-2"),
                new CjPartnerRecord("pending-1", "Pending One", "pending", "US", "Other", "https://example.com/pending-1")
            ],
            isCompleteRoster: true),
            new FakeSecretProtector(),
            NullLogger<CjAdsService>.Instance);

        var result = await service.SyncPartnersAsync(new CjPartnerSyncRequest
        {
            DeveloperKey = "key",
            PublisherId = "pub",
            EndpointUrl = "https://commissions.api.cj.com/query",
            MaxResults = 100
        });

        Assert.Equal(2, result.TotalReceived);

        var partners = await service.ListPartnersAsync();
        Assert.Equal(2, partners.Count);
        Assert.DoesNotContain(partners, partner => partner.AdvertiserId == "pending-1");
        Assert.DoesNotContain(partners, partner => partner.AdvertiserId == "old-1");
        Assert.DoesNotContain(await db.AffiliateOffers.ToListAsync(), row => row.AdvertiserId == "stale-import");
    }

    [Fact]
    public async Task SyncPartnersAsync_ShouldNotRemoveStaleRows_WhenRosterIsPartial()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.AddRange(
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "old-1",
                AdvertiserName = "Old Advertiser",
                LinkName = "old-1",
                Category = "Legacy",
                TrackingUrl = "https://example.com/old",
                CreatedBy = "cj-sync"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "stale-import",
                AdvertiserName = "Stale Import",
                LinkName = "Imported Deal",
                Category = "Legacy",
                TrackingUrl = "https://example.com/stale",
                CreatedBy = "cj-import"
            });
        await db.SaveChangesAsync();

        var service = new CjAdsService(
            db,
            new FakeCjAffiliateService(
            [
                new CjPartnerRecord("joined-1", "Joined One", "joined", "US", "Software", "https://example.com/joined-1")
            ],
            isCompleteRoster: false),
            new FakeSecretProtector(),
            NullLogger<CjAdsService>.Instance);

        var result = await service.SyncPartnersAsync(new CjPartnerSyncRequest
        {
            DeveloperKey = "key",
            PublisherId = "pub",
            EndpointUrl = "https://commissions.api.cj.com/query",
            MaxResults = 100
        });

        Assert.Equal(1, result.TotalReceived);

        var partners = await service.ListPartnersAsync();
        Assert.Equal(3, partners.Count);
        Assert.Contains(partners, partner => partner.AdvertiserId == "old-1");
        Assert.Contains(partners, partner => partner.AdvertiserId == "stale-import");
    }

    [Fact]
    public async Task GetOffersForAdvertiserAsync_ShouldReturnOnlyActiveAds()
    {
        await using var db = await CreateDbAsync();
        db.AffiliateOffers.AddRange(
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "joined-1",
                AdvertiserName = "Joined Advertiser",
                LinkName = "Active Deal",
                Category = "Software",
                TrackingUrl = "https://example.com/active",
                PromotionEndsAt = DateTimeOffset.UtcNow.AddDays(3),
                CreatedBy = "test"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "joined-1",
                AdvertiserName = "Joined Advertiser",
                LinkName = "Expired Deal",
                Category = "Software",
                TrackingUrl = "https://example.com/expired",
                PromotionEndsAt = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedBy = "test"
            },
            new GwsBusinessSuite.Domain.Entities.AffiliateOffer
            {
                Network = "CJ",
                AdvertiserId = "joined-1",
                AdvertiserName = "Joined Advertiser",
                LinkName = "No Url",
                Category = "Software",
                TrackingUrl = string.Empty,
                PromotionEndsAt = DateTimeOffset.UtcNow.AddDays(3),
                CreatedBy = "test"
            });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeSecretProtector());

        var offers = await service.GetOffersForAdvertiserAsync("joined-1", "Joined Advertiser");

        Assert.Single(offers);
        Assert.Equal("Active Deal", offers[0].LinkName);
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
        private readonly IReadOnlyCollection<CjPartnerRecord> partners;
        private readonly bool isCompleteRoster;

        public FakeCjAffiliateService()
            : this(Array.Empty<CjPartnerRecord>())
        {
        }

        public FakeCjAffiliateService(IReadOnlyCollection<CjPartnerRecord> partners, bool isCompleteRoster = false)
        {
            this.partners = partners;
            this.isCompleteRoster = isCompleteRoster;
        }

        public Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new CjConnectionValidationResult(true, "ok", 0));
        }

        public Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new CjPartnerFetchResult(partners, "ok", isCompleteRoster));
        }
    }
}
