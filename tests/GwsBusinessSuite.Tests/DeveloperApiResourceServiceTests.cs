using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class DeveloperApiResourceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Contacts_ShouldCreateUpdateAndListWithoutExposingTrashedRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Contacts.Add(new Contact { FullName = "Trashed", TrashedAt = Now, CreatedBy = "seed" });
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Service.CreateContactAsync(new DeveloperApiContactInput
        {
            FullName = " Ada Lovelace ", Email = " ada@example.com ", Status = ContactStatuses.Prospect
        }, "api:key-1");
        var updated = await fixture.Service.UpdateContactAsync(created.Id, new DeveloperApiContactInput
        {
            FullName = "Ada Lovelace", Company = "Analytical Engines", Status = ContactStatuses.Customer
        }, "api:key-1");
        var page = await fixture.Service.ListContactsAsync(1, 50);

        updated!.Status.Should().Be(ContactStatuses.Customer);
        page.Total.Should().Be(1);
        page.Items.Single().Company.Should().Be("Analytical Engines");
        var stored = await fixture.Db.Contacts.SingleAsync(item => item.Id == created.Id);
        stored.CreatedBy.Should().Be("api:key-1");
        stored.UpdatedBy.Should().Be("api:key-1");
    }

    [Fact]
    public async Task Pagination_ShouldRejectUnboundedPageSizes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var act = () => fixture.Service.ListContactsAsync(1, 101);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*between 1 and 100*");
    }

    [Fact]
    public async Task Deals_ShouldRequireAnActiveContactAndMaintainClosedAt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Client" };
        fixture.Db.Contacts.Add(contact);
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Service.CreateDealAsync(new DeveloperApiDealInput
        {
            ContactId = contact.Id, Title = "Project", Stage = DealStages.Lead, ValueUsd = 1500
        }, "api:key-2");
        var won = await fixture.Service.UpdateDealAsync(created.Id, new DeveloperApiDealInput
        {
            ContactId = contact.Id, Title = "Project", Stage = DealStages.Won, ValueUsd = 1500
        }, "api:key-2");

        won!.ClosedAt.Should().Be(Now);
        var invalid = () => fixture.Service.CreateDealAsync(new DeveloperApiDealInput
        {
            ContactId = Guid.NewGuid(), Title = "Missing client", Stage = DealStages.Lead
        }, "api:key-2");
        await invalid.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active contact*");
    }

    [Fact]
    public async Task CmsPages_ShouldUseTheExistingBuilderWorkflowAndPreserveApiAuditActor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        var property = new CmsPageProperty { SiteId = site.Id, Name = "Audience", Type = CmsPagePropertyTypes.Text };
        fixture.Db.CmsSites.Add(site);
        fixture.Db.CmsPageProperties.Add(property);
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Service.CreateCmsPageAsync(new DeveloperApiCmsPageInput
        {
            SiteId = site.Id,
            Title = "API Page",
            Slug = "api-page",
            BlocksJson = "{\"sections\":[]}",
            Status = CmsPageStatuses.Published,
            PropertyValues = new() { [property.Id] = "Developers" }
        }, "api:key-3");
        var stored = await fixture.Db.CmsPages.SingleAsync(item => item.Id == created.Id);

        created.Status.Should().Be(CmsPageStatuses.Published);
        created.PropertyValues[property.Id].Should().Be("Developers");
        stored.CreatedBy.Should().Be("api:key-3");
        stored.UpdatedBy.Should().Be("api:key-3");
    }

    [Fact]
    public async Task CmsPageUpdate_ShouldNotCreateAReplacementForAnUnknownId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        fixture.Db.CmsSites.Add(site);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.UpdateCmsPageAsync(Guid.NewGuid(), new DeveloperApiCmsPageInput
        {
            SiteId = site.Id, Title = "Missing", Slug = "missing", BlocksJson = "{\"sections\":[]}" 
        }, "api:key-3");

        result.Should().BeNull();
        (await fixture.Db.CmsPages.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    public async Task CmsPages_ShouldRejectInvalidBlocksJson(string blocksJson)
    {
        await using var fixture = await Fixture.CreateAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        fixture.Db.CmsSites.Add(site);
        await fixture.Db.SaveChangesAsync();
        var act = () => fixture.Service.CreateCmsPageAsync(new DeveloperApiCmsPageInput
        {
            SiteId = site.Id, Title = "Page", Slug = "page", BlocksJson = blocksJson
        }, "api:key-3");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*blocksJson*");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            var clock = new FixedTimeProvider(Now);
            Service = new DeveloperApiResourceService(db, new CmsBuilderService(db, timeProvider: clock), clock);
        }
        public ApplicationDbContext Db { get; }
        public DeveloperApiResourceService Service { get; }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
