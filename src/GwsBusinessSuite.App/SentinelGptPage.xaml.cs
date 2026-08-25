using System.Collections.ObjectModel;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

public sealed record ConversationSummary(string Title, string Subtitle, string Path);

public partial class SentinelGptPage : ContentPage
{
    private const string PreferredModel = "sentinelgpt";

    private readonly OllamaClient _ollama;
    private readonly NativeToolExecutor _toolExecutor;
    private readonly ConversationSessionStore _sessions;
    private readonly ApprovedMemoryStore _approvedMemory;
    private readonly SecureApiKeyStore _apiKeyStore;
    private readonly SentinelVoiceService _voice;
    private readonly DeepAnalysisAdvisor _deepAnalysis;

    private readonly ObservableCollection<ChatMessageViewModel> _messages = [];
    private readonly ObservableCollection<ConversationSummary> _history = [];

    private List<string> _models = [];
    private OllamaToolCallingAgent? _agent;
    private CancellationTokenSource? _turnCts;
    private string? _currentSessionPath;
    private bool _sending;
    private bool _useDeepAnalysis;

    public SentinelGptPage(
        OllamaClient ollama,
        NativeToolExecutor toolExecutor,
        ConversationSessionStore sessions,
        ApprovedMemoryStore approvedMemory,
        SecureApiKeyStore apiKeyStore,
        SentinelVoiceService voice,
        DeepAnalysisAdvisor deepAnalysis)
    {
        _ollama = ollama;
        _toolExecutor = toolExecutor;
        _sessions = sessions;
        _approvedMemory = approvedMemory;
        _apiKeyStore = apiKeyStore;
        _voice = voice;
        _deepAnalysis = deepAnalysis;

        InitializeComponent();
        Transcript.ItemsSource = _messages;
        HistoryList.ItemsSource = _history;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_models.Count == 0)
            await LoadModelsAsync();
        RefreshHistory();
#if DEBUG
        await RunSandboxSpikeAsync();
#endif
    }

#if DEBUG
    // Temporary, DEBUG-only diagnostic for Developer Mode Phase 1b: does Process.Start work at
    // all from inside this sandboxed, codesigned Mac Catalyst app, and is Homebrew/`~/.dotnet`
    // on its PATH? Tests both independently - git/find live under /usr/bin (sandbox permission
    // in isolation), dotnet/rg are typically Homebrew/`~/.dotnet`-installed (PATH visibility, a
    // separate concern). Remove this block once answered - diagnostic only, never shipped
    // (guarded by #if DEBUG so a Release build can never include it).
    private async Task RunSandboxSpikeAsync()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"[spike] git: {await ProbeAsync("git", "--version")}");
        report.AppendLine($"[spike] dotnet: {await ProbeAsync("dotnet", "--version")}");
        report.AppendLine($"[spike] rg: {await ProbeAsync("rg", "--version")}");
        report.Append($"[spike] PATH={Environment.GetEnvironmentVariable("PATH")}");
        var text = report.ToString();
        StatusLabel.Text += "\n" + text;
        // Written to a plain file (not just the on-screen label) so this can be verified without
        // needing to relay on-screen text - readable directly from Terminal once this runs.
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(FileSystem.Current.AppDataDirectory, "sandbox-spike-result.txt"), text);
        }
        catch
        {
            // Diagnostic-only; the on-screen StatusLabel text above is the fallback.
        }
    }

    private static async Task<string> ProbeAsync(string program, string arguments)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return "Process.Start returned null";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (stdout + stderr).Trim();
            return $"exit={process.ExitCode} \"{(output.Length > 120 ? output[..120] + "..." : output)}\"";
        }
        catch (Exception ex)
        {
            return $"THREW {ex.GetType().Name}: {ex.Message}";
        }
    }
#endif

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _turnCts?.Cancel();
    }

    private async Task LoadModelsAsync()
    {
        StatusLabel.Text = "Connecting to local Ollama...";
        try
        {
            _models = (await _ollama.ListModelsAsync(CancellationToken.None)).ToList();
            if (_models.Count == 0)
            {
                StatusLabel.Text = "No local models installed. Run 'sentinelgpt models sync' in Terminal.";
                return;
            }

            ModelPicker.ItemsSource = _models;
            var preferredIndex = _models.FindIndex(model => NormalizeModel(model) == PreferredModel);
            ModelPicker.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
            StatusLabel.Text = "Local Ollama - conversations never leave this Mac.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusLabel.Text = "Ollama isn't reachable. Start the Ollama app on this Mac, then reopen this tab.";
        }
    }

    private OllamaToolCallingAgent CreateAgent(string model) =>
        new(_ollama, _toolExecutor, model, BuildSystemPrompt(), maxRounds: 6);

    private static string BuildSystemPrompt() =>
        "You are SentinelGPT, Grant Watson's private AI assistant running entirely locally via " +
        "Ollama on this Mac - nothing in this conversation is sent to any hosted server. Answer " +
        "directly and concisely. If wiki-search tools are available and the question depends on " +
        "workspace-specific facts, use them rather than guessing.";

    private void OnNewChatClicked(object? sender, EventArgs e)
    {
        if (_sending) return;
        _messages.Clear();
        _currentSessionPath = null;
        if (ModelPicker.SelectedItem is string model)
            _agent = CreateAgent(model);
        HistoryList.SelectedItem = null;
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (ModelPicker.SelectedItem is not string model) return;
        _messages.Clear();
        _currentSessionPath = null;
        _agent = CreateAgent(model);
    }

    private void OnDeepAnalysisToggled(object? sender, ToggledEventArgs e) => _useDeepAnalysis = e.Value;

    private void OnToggleSettingsClicked(object? sender, EventArgs e)
    {
        SettingsRow.IsVisible = !SettingsRow.IsVisible;
        if (SettingsRow.IsVisible)
            _ = LoadApiKeyIntoFieldAsync();
    }

    private async Task LoadApiKeyIntoFieldAsync() => ApiKeyEntry.Text = await _apiKeyStore.GetAsync();

    private async void OnSaveApiKeyClicked(object? sender, EventArgs e)
    {
        var key = ApiKeyEntry.Text?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            _apiKeyStore.Remove();
            StatusLabel.Text = "Grounding key removed - SentinelGPT will answer from local knowledge only.";
        }
        else
        {
            await _apiKeyStore.SetAsync(key);
            StatusLabel.Text = "Grounding key saved.";
        }
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (_sending) return;
        var prompt = PromptEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (ModelPicker.SelectedItem is not string model)
        {
            StatusLabel.Text = "No local model selected. Run 'sentinelgpt models sync' in Terminal, then reopen this tab.";
            return;
        }

        _agent ??= CreateAgent(model);
        PromptEditor.Text = string.Empty;
        await RunTurnAsync(prompt);
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        if (_sending) return;
        if ((sender as Button)?.BindingContext is not ChatMessageViewModel { RetryPrompt: { } prompt })
            return;
        await RunTurnAsync(prompt);
    }

    private async Task RunTurnAsync(string prompt)
    {
        if (_agent is null) return;

        _sending = true;
        SendButton.IsEnabled = false;
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();

        foreach (var previous in _messages)
            previous.RetryPrompt = null;

        _messages.Add(new ChatMessageViewModel(isUser: true) { Text = prompt, IsComplete = true });
        var answer = new ChatMessageViewModel(isUser: false);
        _messages.Add(answer);

        var effectivePrompt = prompt;
        try
        {
            if (_useDeepAnalysis)
            {
                answer.Text = "Consulting specialist advisers...";
                var advisory = await _deepAnalysis.BuildAdvisoryContextAsync(prompt, _turnCts.Token);
                if (!string.IsNullOrWhiteSpace(advisory))
                    effectivePrompt = $"{prompt}\n\n{advisory}";
                answer.Text = string.Empty;
            }

            var approvedContext = await _approvedMemory.BuildContextAsync(prompt, _turnCts.Token);
            if (!string.IsNullOrWhiteSpace(approvedContext))
                effectivePrompt = $"{effectivePrompt}\n\n{approvedContext}";

            // showingActivity is only ever read/written here, synchronously on the loop's own
            // thread - each MainThread-marshaled closure below only carries an already-computed
            // string, never reads or writes the flag itself, so there's no race between the
            // enumerator advancing and a previous UI update still being queued.
            var showingActivity = false;
            await foreach (var turnEvent in _agent.RunTurnStreamingAsync(effectivePrompt, _turnCts.Token))
            {
                if (turnEvent.ToolActivity is { } activity)
                {
                    showingActivity = true;
                    var activityText = $"\U0001F527 {activity}...";
                    MainThread.BeginInvokeOnMainThread(() => answer.Text = activityText);
                }
                else if (turnEvent.ContentDelta is { Length: > 0 } delta)
                {
                    var replacePrevious = showingActivity;
                    showingActivity = false;
                    MainThread.BeginInvokeOnMainThread(() =>
                        answer.Text = replacePrevious ? delta : answer.Text + delta);
                }
            }

            answer.IsComplete = true;
            _currentSessionPath = await _sessions.SaveAsync(_currentSessionPath, PickerModel(), _agent.Messages, CancellationToken.None);
            RefreshHistory();
        }
        catch (OperationCanceledException)
        {
            // Page navigated away mid-turn - nothing left to show a result to.
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            answer.Text = $"Request failed: {ex.Message}";
            answer.IsError = true;
            answer.IsComplete = true;
            answer.RetryPrompt = prompt;
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
        }
    }

    private string PickerModel() => ModelPicker.SelectedItem as string ?? PreferredModel;

    private async void OnApproveClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.BindingContext is not ChatMessageViewModel answer || answer.IsApproved) return;
        var index = _messages.IndexOf(answer);
        var question = index > 0 ? _messages[index - 1] : null;
        if (question is null || !question.IsUser) return;

        await _approvedMemory.AppendAsync(question.Text, answer.Text, CancellationToken.None);
        answer.IsApproved = true;
    }

    private async void OnSpeakClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.BindingContext is ChatMessageViewModel { Text.Length: > 0 } message)
            await _voice.SpeakAsync(message.Text);
    }

    private void RefreshHistory()
    {
        _history.Clear();
        foreach (var (path, conversation) in _sessions.List())
        {
            var firstUserMessage = conversation.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "New conversation";
            var title = firstUserMessage.Length > 48 ? firstUserMessage[..48] + "..." : firstUserMessage;
            _history.Add(new ConversationSummary(title, conversation.UpdatedAt.ToLocalTime().ToString("MMM d, t"), path));
        }
    }

    private async void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_sending || e.CurrentSelection.FirstOrDefault() is not ConversationSummary selected) return;

        var loaded = await _sessions.LoadAsync(selected.Path, CancellationToken.None);
        if (loaded is null) return;

        _currentSessionPath = selected.Path;
        _agent = CreateAgent(loaded.Model);
        _agent.LoadConversation(loaded.Messages);

        var modelIndex = _models.FindIndex(m => m == loaded.Model);
        if (modelIndex >= 0) ModelPicker.SelectedIndex = modelIndex;

        _messages.Clear();
        foreach (var message in loaded.Messages.Where(m => m.Role is "user" or "assistant" && m.Content.Length > 0))
        {
            _messages.Add(new ChatMessageViewModel(isUser: message.Role == "user")
            {
                Text = message.Content,
                IsComplete = true
            });
        }
    }

    private void OnDeleteConversationClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.BindingContext is not ConversationSummary summary) return;
        _sessions.Delete(summary.Path);
        if (_currentSessionPath == summary.Path)
        {
            _messages.Clear();
            _currentSessionPath = null;
        }
        RefreshHistory();
    }

    private static string NormalizeModel(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? model[..^7] : model;
}
