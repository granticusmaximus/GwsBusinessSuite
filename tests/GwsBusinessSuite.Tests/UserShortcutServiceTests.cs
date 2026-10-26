using FluentAssertions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class UserShortcutServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private UserShortcutService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        IDbContextFactory<ApplicationDbContext> factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        _service = new UserShortcutService(factory, _clock);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Theory]
    [InlineData("https://admin.gwsapp.net/admin/crm?tab=deals#top", "admin/crm?tab=deals")]
    [InlineData("/admin/crm", "admin/crm")]
    [InlineData("admin/crm", "admin/crm")]
    [InlineData("", null)]
    [InlineData("javascript://alert(1)", null)]
    public void NormalizeUrl_ShouldGiveOnePageOneAddress(string input, string? expected) =>
        UserShortcutRules.NormalizeUrl(input).Should().Be(expected);

    [Fact]
    public async Task Pins_ShouldToggle_KeepTheirOrder_StayPerPerson_AndStopAtTheLimit()
    {
        (await _service.TogglePinAsync("grant", "/admin/crm", "CRM", "bi-people-fill")).Should().BeTrue();
        _clock.Advance();
        (await _service.TogglePinAsync("grant", "admin/billing", "Billing", "bi-receipt")).Should().BeTrue();
        (await _service.IsPinnedAsync("grant", "https://admin.gwsapp.net/admin/crm")).Should().BeTrue("the same page, however it's written");
        (await _service.IsPinnedAsync("ana", "admin/crm")).Should().BeFalse("pins are per person");

        var view = await _service.ListAsync("grant");
        view.Pins.Select(p => p.Title).Should().Equal("CRM", "Billing");
        view.Pins[0].Icon.Should().Be("bi-people-fill");

        (await _service.TogglePinAsync("grant", "admin/crm", "CRM", "")).Should().BeFalse("pinning a pinned page unpins it");
        (await _service.ListAsync("grant")).Pins.Select(p => p.Url).Should().Equal("admin/billing");

        for (var i = 0; i < UserShortcutRules.MaxPins - 1; i++) await _service.TogglePinAsync("grant", $"admin/page{i}", $"Page {i}", "");
        var tooMany = () => _service.TogglePinAsync("grant", "admin/one-more", "One more", "");
        await tooMany.Should().ThrowAsync<UserShortcutException>().WithMessage("*up to 8*");
    }

    [Fact]
    public async Task Recents_ShouldPutTheLatestFirst_UpdateTitles_AndKeepOnlyAFew()
    {
        await _service.RecordVisitAsync("grant", "admin/article-editor/1", "Article", "bi-pencil");
        _clock.Advance();
        await _service.RecordVisitAsync("grant", "admin/crm/contacts/7", "Ana Silva", "bi-person");
        _clock.Advance();
        // The article's real title arrives once it loads: same record, new title, back on top.
        await _service.RecordVisitAsync("grant", "/admin/article-editor/1", "Atlanta approves MARTA expansion", "bi-pencil");

        var recents = (await _service.ListAsync("grant")).Recents;
        recents.Select(r => r.Title).Should().Equal("Atlanta approves MARTA expansion", "Ana Silva");

        await _service.RecordVisitAsync("grant", "admin/x", "  ", "");
        (await _service.ListAsync("grant")).Recents.Should().HaveCount(2, "a page with no title isn't recorded");

        for (var i = 0; i < 30; i++)
        {
            _clock.Advance();
            await _service.RecordVisitAsync("grant", $"admin/docker-health/c{i}", $"Container {i}", "bi-box-seam");
        }
        var view = await _service.ListAsync("grant");
        view.Recents.Should().HaveCount(UserShortcutRules.MaxRecents);
        view.Recents[0].Title.Should().Be("Container 29");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        (await db.UserShortcuts.CountAsync(s => s.Kind == "recent")).Should().BeLessThanOrEqualTo(UserShortcutRules.MaxRecents * 2, "old visits are trimmed");

        await _service.RemoveAsync("grant", "recent", "admin/docker-health/c29");
        (await _service.ListAsync("grant")).Recents[0].Title.Should().Be("Container 28");
        (await _service.ListAsync("ana")).Recents.Should().BeEmpty();
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance() => _now = _now.AddSeconds(5);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
