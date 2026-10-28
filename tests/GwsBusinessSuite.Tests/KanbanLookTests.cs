using FluentAssertions;
using GwsBusinessSuite.Application.Boards;

namespace GwsBusinessSuite.Tests;

// Boards K7: who else is on a board, and what a board's look may hold.
public sealed class KanbanLookTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Presence_ShouldListEveryoneElseOnTheBoard_WithTheCardTheyHaveOpen()
    {
        var clock = new Clock();
        var presence = new KanbanPresence(clock);
        var board = Guid.NewGuid();
        var otherBoard = Guid.NewGuid();
        var card = Guid.NewGuid();
        var (me, sam, grantElsewhere, stranger) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var heard = new List<Guid>();
        presence.Changed += heard.Add;

        presence.Enter(me, board, "grant", null);
        presence.Enter(sam, board, "sam", card);
        presence.Enter(grantElsewhere, board, "grant", null);
        presence.Enter(stranger, otherBoard, "alex", null);

        presence.Others(board, me).Select(viewer => (viewer.Username, viewer.OpenCardId))
            .Should().Equal(("grant", (Guid?)null), ("sam", card));
        heard.Should().Equal(board, board, board, otherBoard);

        // The same card again tells no one; closing it does.
        heard.Clear();
        presence.Enter(sam, board, "sam", card);
        heard.Should().BeEmpty();
        presence.Enter(sam, board, "sam", null);
        heard.Should().Equal(board);

        // Moving to another board tells both.
        heard.Clear();
        presence.Enter(sam, otherBoard, "sam", null);
        heard.Should().Equal(board, otherBoard);
        presence.Others(board, me).Select(viewer => viewer.Username).Should().Equal("grant");
    }

    [Fact]
    public void Presence_ShouldForgetASessionThatStopsCheckingIn_AndOneThatLeaves()
    {
        var clock = new Clock();
        var presence = new KanbanPresence(clock);
        var board = Guid.NewGuid();
        var (me, quiet, steady) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        presence.Enter(me, board, "grant", null);
        presence.Enter(quiet, board, "sam", null);
        presence.Enter(steady, board, "alex", null);

        clock.Now = clock.Now.AddSeconds(60);
        presence.Touch(steady);
        clock.Now = clock.Now.AddSeconds(45);
        presence.Others(board, me).Select(viewer => viewer.Username).Should().Equal("alex");

        presence.Leave(steady);
        presence.Others(board, me).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://images.example.com/boards/hero.jpg", true)]
    [InlineData("/media/2026/10/team.png", true)]
    [InlineData("https://cdn.example.com:8443/a.webp?w=1200&q=80", true)]
    [InlineData("http://example.com/a.jpg", false)]
    [InlineData("//evil.example/a.jpg", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://example.com/a.jpg) ; background: red", false)]
    [InlineData("/media/a'b.jpg", false)]
    [InlineData("/media/a b.jpg", false)]
    [InlineData("https://example.com/a\\b.jpg", false)]
    [InlineData("aurora", false)]
    public void Images_ShouldBeHttpsOrOnThisSite_AndSafeInACssUrl(string url, bool expected) =>
        KanbanLook.IsImage(url).Should().Be(expected);

    private static string BoardsRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite"));

    [Fact]
    public void CardCovers_ShouldNeverStartAnImageDrag()
    {
        // Found live (28-10-26): pressing on a card's cover began the browser's own image drag, so
        // the card never lifted. The picture must be undraggable and let the press through.
        var markup = File.ReadAllText(Path.Combine(BoardsRoot, "Board.razor"));
        var css = File.ReadAllText(Path.Combine(BoardsRoot, "Board.razor.css"));

        markup.Should().MatchRegex("<img class=\"kb-card-cover\"[^>]*draggable=\"false\"");
        var coverRule = css[css.IndexOf(".kb-card-cover {", StringComparison.Ordinal)..];
        coverRule[..coverRule.IndexOf('}')].Should().Contain("pointer-events: none");
    }
}
