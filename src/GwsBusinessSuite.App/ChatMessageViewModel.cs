using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GwsBusinessSuite.App;

public sealed class ChatMessageViewModel(bool isUser) : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isError;
    private bool _isApproved;
    private bool _isComplete;
    private string? _retryPrompt;
    private bool _isFromServerFallback;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsUser { get; } = isUser;
    public bool IsAssistant => !IsUser;

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(ShowTypingIndicator));
        }
    }

    public bool HasText => _text.Length > 0;

    // The dots show only in the genuinely empty window - request sent, nothing streamed back
    // yet. Once the first token lands the text itself is the progress signal, and leaving the
    // dots up alongside it would be two indicators saying the same thing. Transient status the
    // page writes into Text (tool activity, "Thinking...") also counts as text and dismisses
    // them, which is correct: something more specific is now being said.
    public bool ShowTypingIndicator => IsAssistant && !IsComplete && !HasText;

    // A caret while tokens are still arriving, so a paused stream is visibly distinguishable
    // from a finished answer - without it, a model that stalls mid-sentence looks like a model
    // that simply ended mid-sentence.
    public bool ShowStreamingCaret => IsAssistant && !IsComplete && HasText && !IsSystemNotice;

    public bool IsError
    {
        get => _isError;
        set
        {
            _isError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BubbleColor));
        }
    }

    public Color BubbleColor => IsError ? Color.FromArgb("#2B1515")
        : IsSystemNotice ? Color.FromArgb("#161B22")
        : Color.FromArgb("#1C1917");

    // Local output from a slash command, not something the model said. Styled and labelled
    // differently on purpose: presenting the app's own answer in an identical bubble would let a
    // user believe the assistant knows things it was never told.
    public bool IsSystemNotice { get; init; }

    public bool IsModelAnswer => IsAssistant && !IsSystemNotice;

    // Command output is a list of names and usages; a proportional font ruins the alignment.
    public string TextFontFamily => IsSystemNotice ? "Menlo" : string.Empty;

    public bool IsApproved
    {
        get => _isApproved;
        set
        {
            _isApproved = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ApproveButtonText));
        }
    }

    public string ApproveButtonText => IsApproved ? "✓ Approved" : "\U0001F44D";

    // Set once a turn resolves (successfully or not) - lets the transcript template hide the
    // approve/speak/retry row while a streaming answer is still arriving.
    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            _isComplete = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowTypingIndicator));
            OnPropertyChanged(nameof(ShowStreamingCaret));
        }
    }

    // Only meaningful on the most recent assistant message; the page clears this on every other
    // message when a new turn starts so at most one "Retry" affordance is ever visible.
    public string? RetryPrompt
    {
        get => _retryPrompt;
        set
        {
            _retryPrompt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRetryPrompt));
        }
    }

    public bool HasRetryPrompt => RetryPrompt is not null;

    // Set when this answer came from the server fallback (see NativeFallbackChatService) instead
    // of the user's own local Ollama - the whole point of the local-first business rule is that
    // this exception stays visible rather than silently blending in with local answers.
    public bool IsFromServerFallback
    {
        get => _isFromServerFallback;
        set { _isFromServerFallback = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
