using FluentAssertions;
using GwsBusinessSuite.Web.Components;
using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// The undo toast (shell redesign R5).
public sealed class ToastStateTests
{
    private readonly ToastState _toasts = new(NullLogger<ToastState>.Instance);

    [Fact]
    public void Show_ShouldReplaceTheLastToast_AndRaiseChanged()
    {
        var changes = 0;
        _toasts.Changed += () => changes++;

        _toasts.Show("First");
        var first = _toasts.Current!.Id;
        _toasts.Show("Second");

        _toasts.Current!.Message.Should().Be("Second");
        _toasts.Current.Id.Should().NotBe(first);
        changes.Should().Be(2);
    }

    [Fact]
    public void Dismiss_ShouldIgnoreAToastThatWasAlreadyReplaced()
    {
        _toasts.Show("First");
        var stale = _toasts.Current!.Id;
        _toasts.Show("Second");

        _toasts.Dismiss(stale);
        _toasts.Current!.Message.Should().Be("Second", "a late timer for the old toast mustn't close the new one");

        _toasts.Dismiss(_toasts.Current.Id);
        _toasts.Current.Should().BeNull();
    }

    [Fact]
    public async Task Undo_ShouldRunOnce_ThenConfirm()
    {
        var runs = 0;
        _toasts.Show("Moved 'About' to Trash.", () => { runs++; return Task.CompletedTask; });
        var id = _toasts.Current!.Id;

        await _toasts.UndoAsync(id);
        await _toasts.UndoAsync(id);

        runs.Should().Be(1, "a second click on a stale Undo does nothing");
        _toasts.Current!.Message.Should().Be("Undone.");
        _toasts.Current.Tone.Should().Be(StatusTone.Success);
        _toasts.Current.Undo.Should().BeNull();
    }

    [Fact]
    public async Task FailedUndo_ShouldSaySo()
    {
        _toasts.Show("Comment marked Spam.", () => throw new InvalidOperationException("The comment was deleted."));

        await _toasts.UndoAsync(_toasts.Current!.Id);

        _toasts.Current!.Tone.Should().Be(StatusTone.Danger);
        _toasts.Current.Message.Should().Contain("The comment was deleted.");
    }

    [Fact]
    public void Navigation_ShouldDismiss_UnlessTheToastSurvivesOneNavigation()
    {
        _toasts.Show("Comment marked Approved.");
        _toasts.OnNavigated();
        _toasts.Current.Should().BeNull();

        _toasts.Show("Moved Dana Rivers to Trash.", () => Task.CompletedTask, survivesNavigation: true);
        _toasts.OnNavigated();
        _toasts.Current.Should().NotBeNull("trashing the record you're on leaves the page, and the toast goes with you");
        _toasts.OnNavigated();
        _toasts.Current.Should().BeNull("only the one navigation");
    }
}
