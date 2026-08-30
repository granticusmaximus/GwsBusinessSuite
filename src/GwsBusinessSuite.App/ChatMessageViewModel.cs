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
        set { _text = value; OnPropertyChanged(); }
    }

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

    public Color BubbleColor => IsError ? Color.FromArgb("#2B1515") : Color.FromArgb("#1C1917");

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
        set { _isComplete = value; OnPropertyChanged(); }
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
