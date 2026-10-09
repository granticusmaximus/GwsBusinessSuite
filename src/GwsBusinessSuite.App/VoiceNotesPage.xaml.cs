using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// Voice and meeting notes: record (or transcribe a recording), have a local model turn the
// transcript into a summary / decisions / action items, edit, and save it to Sentinel as a Quick
// Note. Speech recognition and the summary both run on this Mac (OnDeviceSpeechTranscriber,
// local Ollama) - only the finished note is sent to the hosted app.
public partial class VoiceNotesPage : ContentPage
{
    private const string SummaryPrompt =
        "You turn a meeting or voice-note transcript into notes. Write Markdown with exactly these sections: " +
        "'## Summary' (two to four sentences), '## Decisions' (bullets, or 'None recorded.'), and " +
        "'## Action items' (checkbox bullets like '- [ ] Dana: send the revised quote (Friday)', or 'None recorded.'). " +
        "Use only what the transcript says - never invent names, dates, owners or decisions.";

    private readonly OllamaClient _ollama;
    private readonly NativeSessionClient _session;
#if MACCATALYST
    private readonly OnDeviceSpeechTranscriber _transcriber = new();
#endif
    private IDispatcherTimer? _clock;
    private DateTimeOffset _recordingStarted;
    private string? _savedUrl;

    public VoiceNotesPage(OllamaClient ollama, NativeSessionClient session)
    {
        InitializeComponent();
        _ollama = ollama;
        _session = session;
        TitleEntry.Text = DefaultTitle();
#if !MACCATALYST
        RecordButton.IsEnabled = false;
        TranscribeFileButton.IsEnabled = false;
        StatusLabel.Text = "Voice notes record on the Mac app; here you can paste a transcript and summarise it.";
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if MACCATALYST
        MacToolbarActions.SyncSelectedTab?.Invoke("VoiceNotesPage");
#endif
        if (ModelPicker.ItemsSource is null) await LoadModelsAsync();
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var models = (await _ollama.ListModelsAsync(CancellationToken.None))
                .Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ModelPicker.ItemsSource = models;
            var preferred = models.FindIndex(m => m.StartsWith("sentinelgpt", StringComparison.OrdinalIgnoreCase));
            ModelPicker.SelectedIndex = preferred >= 0 ? preferred : models.Count > 0 ? 0 : -1;
            if (models.Count == 0) StatusLabel.Text = "No local models found - install one from the SentinelGPT tab's model library.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusLabel.Text = "Ollama isn't running on this Mac - start it to summarise notes.";
        }
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
#if MACCATALYST
        if (_transcriber.IsRecording)
        {
            _transcriber.Stop();
            return;
        }

        var problem = await _transcriber.StartAsync(
            text => MainThread.BeginInvokeOnMainThread(() => TranscriptEditor.Text = text),
            final => MainThread.BeginInvokeOnMainThread(() => OnRecordingStopped(final)));
        if (problem is not null)
        {
            StatusLabel.Text = problem;
            return;
        }

        _recordingStarted = DateTimeOffset.Now;
        RecordButton.Text = "■ Stop";
        TranscribeFileButton.IsEnabled = false;
        StatusLabel.Text = "Recording - transcribing on this Mac.";
        _clock = Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ElapsedLabel.Text = (DateTimeOffset.Now - _recordingStarted).ToString(@"h\:mm\:ss");
        _clock.Start();
#else
        await Task.CompletedTask;
#endif
    }

    private void OnRecordingStopped(string transcript)
    {
        _clock?.Stop();
        RecordButton.Text = "● Record";
        TranscribeFileButton.IsEnabled = true;
        if (!string.IsNullOrWhiteSpace(transcript)) TranscriptEditor.Text = transcript;
        StatusLabel.Text = "Recording stopped. Edit the transcript if needed, then summarise.";
    }

    private async void OnTranscribeFileClicked(object? sender, EventArgs e)
    {
#if MACCATALYST
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose a recording to transcribe" });
        if (file is null) return;
        StatusLabel.Text = $"Transcribing {file.FileName} on this Mac...";
        TranscribeFileButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        try
        {
            var (text, error) = await _transcriber.TranscribeFileAsync(file.FullPath);
            if (error is not null)
            {
                StatusLabel.Text = $"Couldn't transcribe it: {error}";
                return;
            }
            TranscriptEditor.Text = text;
            TitleEntry.Text = Path.GetFileNameWithoutExtension(file.FileName);
            StatusLabel.Text = "Transcribed. Summarise it, or edit first.";
        }
        finally
        {
            TranscribeFileButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async void OnSummarizeClicked(object? sender, EventArgs e)
    {
        var transcript = TranscriptEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            StatusLabel.Text = "There's no transcript to summarise yet.";
            return;
        }
        if (ModelPicker.SelectedItem is not string model)
        {
            StatusLabel.Text = "Pick a local model first.";
            return;
        }

        SummarizeButton.IsEnabled = false;
        StatusLabel.Text = $"Summarising with {model}...";
        try
        {
            var result = await _ollama.ChatAsync(model,
                [new OllamaChatMessage("system", SummaryPrompt), new OllamaChatMessage("user", transcript)],
                [], CancellationToken.None, think: false);
            SummaryEditor.Text = result.Content.Trim();
            StatusLabel.Text = "Done. Edit the notes if needed, then save.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusLabel.Text = $"The local model couldn't summarise it: {ex.Message}";
        }
        finally
        {
            SummarizeButton.IsEnabled = true;
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var transcript = TranscriptEditor.Text?.Trim() ?? string.Empty;
        var notes = SummaryEditor.Text?.Trim() ?? string.Empty;
        if (transcript.Length == 0 && notes.Length == 0)
        {
            SaveStatusLabel.Text = "Nothing to save yet.";
            return;
        }

        var title = string.IsNullOrWhiteSpace(TitleEntry.Text) ? DefaultTitle() : TitleEntry.Text.Trim();
        var markdown = string.Join("\n\n", new[] { notes, transcript.Length > 0 ? $"## Transcript\n\n{transcript}" : string.Empty }
            .Where(part => part.Length > 0));
        SaveButton.IsEnabled = false;
        SaveStatusLabel.Text = "Saving...";
        try
        {
            var result = await _session.SaveQuickNoteAsync(title, markdown);
            if (result.Error is not null)
            {
                SaveStatusLabel.Text = result.Error;
                return;
            }
            _savedUrl = result.Url;
            OpenSavedButton.IsVisible = _savedUrl is not null;
            SaveStatusLabel.Text = $"Saved to Sentinel under Quick Notes as \"{title}\".";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnOpenSavedClicked(object? sender, EventArgs e)
    {
        if (_savedUrl is null) return;
        WorkspaceNavigation.Request(_savedUrl);
        await Shell.Current.GoToAsync("//MainPage");
    }

    private static string DefaultTitle() => $"Voice note - {DateTime.Now:MMM d, h:mm tt}";
}
