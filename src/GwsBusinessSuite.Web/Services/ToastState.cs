using GwsBusinessSuite.Web.Components;

namespace GwsBusinessSuite.Web.Services;

// The undo toast (shell redesign R5): a page does the thing at once, then offers Undo here, and
// ToastHost (an interactive island in the static layout, on the same circuit) shows it. One toast
// at a time; a new one replaces the last. Undo callbacks run on the circuit's dispatcher, so a
// page's callback can change its own state, but it must call its own StateHasChanged.
public sealed class ToastState(ILogger<ToastState> logger)
{
    private int _nextId;

    // ActionLabel names the button when it isn't an undo ("Resolve", "Open card").
    public sealed record Toast(int Id, string Message, StatusTone Tone, Func<Task>? Undo, bool SurvivesNavigation, string? ActionLabel = null);

    public Toast? Current { get; private set; }

    public event Action? Changed;

    // SurvivesNavigation keeps the toast through the next page change, for an action that leaves
    // the page (trashing the record you're on); any later navigation dismisses it as usual.
    public void Show(string message, Func<Task>? undo = null, StatusTone tone = StatusTone.Neutral, bool survivesNavigation = false, string? actionLabel = null)
    {
        Current = new Toast(Interlocked.Increment(ref _nextId), message, tone, undo, survivesNavigation, actionLabel);
        Changed?.Invoke();
    }

    public void Dismiss(int id)
    {
        if (Current?.Id != id) return;
        Current = null;
        Changed?.Invoke();
    }

    public void OnNavigated()
    {
        if (Current is null) return;
        Current = Current.SurvivesNavigation ? Current with { SurvivesNavigation = false } : null;
        Changed?.Invoke();
    }

    public async Task UndoAsync(int id)
    {
        if (Current is not { Undo: { } undo } toast || toast.Id != id) return;
        Current = null;
        Changed?.Invoke();
        try
        {
            await undo();
            // An action other than Undo ("Resolve") says what it did itself.
            if (toast.ActionLabel is null) Show("Undone.", tone: StatusTone.Success);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A toast action failed: {Message}", toast.Message);
            Show(toast.ActionLabel is null ? $"Couldn't undo that. {ex.Message}" : $"Couldn't do that. {ex.Message}", tone: StatusTone.Danger);
        }
    }
}
