using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Tests;

public sealed class BoardNotificationServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private ApplicationDbContext _db = null!;
    private KanbanBoardService _boards = null!;
    private BoardNotificationNotifier _notifier = null!;
    private RecordingMail _mail = null!;
    private BoardNotificationService _notifications = null!;
    private readonly List<string> _told = [];

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new ApplicationDbContext(_options);
        await _db.Database.EnsureCreatedAsync();
        _db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true },
            new AppUser { Username = "sam", Role = AppRoles.Admin, IsActive = true },
            new AppUser { Username = "ann", Role = AppRoles.Author, IsActive = true });
        await _db.SaveChangesAsync();
        _boards = new KanbanBoardService(_db, new WikiDatabaseService(_db));
        _notifier = new BoardNotificationNotifier();
        _notifier.OnNotified += _told.Add;
        _mail = new RecordingMail();
        _notifications = new BoardNotificationService(new Factory(_options), _boards, _notifier, new UtcClock(),
            _mail, Options.Create(new GrowthReportEmailOptions { FromAddress = "suite@example.test" }),
            Options.Create(new SupportNotificationOptions { AdminBaseUrl = "https://admin.example.test/" }));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Sweep_ShouldTellNewAssignees_ButNotOnTheFirstLook_NorOnCardsTheyMade()
    {
        var (id, board) = await NewBoardAsync();
        var existing = await _boards.AddCardAsync(id, board.Columns[0].OptionId, "Already assigned", "grant");
        await AssignAsync(id, board, existing, "grant", ["sam"]);

        (await _notifications.SweepAsync(Now)).Should().Be(0, "the first look records what's there without telling anyone");
        (await _notifications.ListAsync("sam")).Should().BeEmpty();

        var card = await _boards.AddCardAsync(id, board.Columns[0].OptionId, "Write the FAQ", "grant");
        await AssignAsync(id, board, card, "grant", ["sam", "ann"]);
        var mine = await _boards.AddCardAsync(id, board.Columns[0].OptionId, "My own task", "sam");
        await AssignAsync(id, board, mine, "sam", ["sam"]);

        (await _notifications.SweepAsync(Now)).Should().Be(1, "a card sam made for sam is no news");
        _told.Should().Equal(["sam"], "ann is an Author, who can't open boards (yet)");
        (await _notifications.ListAsync("ann")).Should().BeEmpty();
        var notes = await _notifications.ListAsync("sam");
        notes.Should().ContainSingle().Which.Should().Match<BoardNotificationView>(note =>
            note.RowId == card && note.Kind == BoardNotificationKinds.Assigned && !note.IsRead
            && note.Message.Contains("Write the FAQ") && note.Url == $"/admin/boards/{id}?card={card}");
        (await _notifications.SweepAsync(Now)).Should().Be(0, "it's said once");

        await _notifications.MarkReadAsync("sam", notes[0].Id);
        (await _notifications.ListAsync("sam")).Single().IsRead.Should().BeTrue();

        // Unassigned, then assigned again: said again.
        await AssignAsync(id, board, card, "grant", []);
        await _notifications.SweepAsync(Now);
        await AssignAsync(id, board, card, "grant", ["sam"]);
        (await _notifications.SweepAsync(Now)).Should().Be(1);
    }

    [Fact]
    public async Task Sweep_ShouldTellAssigneesWhenAnOpenCardIsDueTomorrowOrOverdue_Once()
    {
        var (id, board) = await NewBoardAsync();
        await _notifications.SweepAsync(Now);
        var tomorrow = await _boards.AddCardAsync(id, board.Columns[0].OptionId, "Send the invoice", "grant");
        var late = await _boards.AddCardAsync(id, board.Columns[0].OptionId, "Renew the domain", "grant");
        var finished = await _boards.AddCardAsync(id, board.Columns[^1].OptionId, "Old work", "grant");
        foreach (var (card, due) in new[] { (tomorrow, Now.AddDays(1)), (late, Now.AddDays(-3)), (finished, Now.AddDays(-3)) })
        {
            await AssignAsync(id, board, card, "sam", ["grant"]);
            await _boards.SetCardsValueAsync(id, [card], board.Fields.DuePropertyId!.Value, _ => JsonValue.Create(due), "sam");
        }

        await _notifications.SweepAsync(Now);

        var kinds = (await _notifications.ListAsync("grant")).Where(note => note.Kind != BoardNotificationKinds.Assigned)
            .Select(note => (note.RowId, note.Kind)).ToList();
        kinds.Should().BeEquivalentTo([(tomorrow, BoardNotificationKinds.DueTomorrow), (late, BoardNotificationKinds.Overdue)],
            "a finished card is never overdue");
        (await _notifications.SweepAsync(Now)).Should().Be(0);
    }

    [Fact]
    public async Task Digest_ShouldEmailOnlyThoseWhoAskedFor_OnceAMorning()
    {
        var (id, board) = await NewBoardAsync();
        await _notifications.SweepAsync(Now.AddDays(-1));
        var card = await _boards.AddCardAsync(id, board.Columns[0].OptionId, "Write the FAQ", "grant");
        await AssignAsync(id, board, card, "grant", ["sam", "grant"]);
        await _notifications.SweepAsync(Now.AddHours(-3));
        var sam = await _db.AppUsers.SingleAsync(user => user.Username == "sam");
        _db.MemberProfiles.Add(new GwsBusinessSuite.Domain.Entities.MemberProfile { AppUserId = sam.Id, Email = "sam@example.test", EmailBoardDigest = true });
        await _db.SaveChangesAsync();

        (await _notifications.SendDigestsAsync(new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.Zero))).Should().Be(0, "not before 07:00");
        (await _notifications.SendDigestsAsync(Now)).Should().Be(1, "grant didn't ask; sam did");
        _mail.Sent.Should().ContainSingle().Which.To.ToString().Should().Contain("sam@example.test");
        _mail.Sent[0].TextBody.Should().Contain("Write the FAQ").And.Contain($"https://admin.example.test/admin/boards/{id}?card={card}");
        (await _notifications.SendDigestsAsync(Now.AddHours(2))).Should().Be(0, "one email a day");
    }

    private async Task<(Guid Id, KanbanBoard Board)> NewBoardAsync()
    {
        var id = await _boards.CreateBoardAsync("Website relaunch", "personal", "grant");
        return (id, (await _boards.GetBoardAsync(id))!);
    }

    private Task AssignAsync(Guid id, KanbanBoard board, Guid card, string by, string[] people) =>
        _boards.SetCardsValueAsync(id, [card], board.Fields.AssigneePropertyId!.Value,
            _ => new JsonArray(people.Select(person => (JsonNode?)JsonValue.Create(person)).ToArray()), by);

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class UtcClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RecordingMail : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public MailRoute Describe(ISmtpTransportOptions options) => new(MailRouteKind.PickupDirectory, "test");

        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
