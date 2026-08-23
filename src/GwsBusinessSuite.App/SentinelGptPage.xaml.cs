using Microsoft.Maui.Controls.Shapes;

namespace GwsBusinessSuite.App;

public partial class SentinelGptPage : ContentPage
{
    private const string PreferredModel = "sentinelgpt";
    private readonly OllamaClient _ollama = new();
    private readonly List<ChatMessage> _messages = [];
    private List<string> _models = [];
    private bool _sending;

    public SentinelGptPage() => InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_models.Count > 0) return;
        await LoadModelsAsync();
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

    private void OnModelChanged(object? sender, EventArgs e)
    {
        _messages.Clear();
        Transcript.Children.Clear();
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

        _sending = true;
        SendButton.IsEnabled = false;
        PromptEditor.Text = string.Empty;
        AppendMessage("You", prompt, isUser: true);
        _messages.Add(new ChatMessage("user", prompt));

        var answerLabel = AppendMessage("SentinelGPT", "Thinking...", isUser: false);
        try
        {
            var answer = await _ollama.ChatAsync(model, _messages, CancellationToken.None);
            answerLabel.Text = string.IsNullOrWhiteSpace(answer) ? "(no response)" : answer;
            _messages.Add(new ChatMessage("assistant", answer));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            answerLabel.Text = $"Request failed: {ex.Message}";
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
        }
    }

    private Label AppendMessage(string sender, string text, bool isUser)
    {
        var contentLabel = new Label { Text = text, TextColor = Color.FromArgb("#FAFAF9"), FontSize = 14 };
        var bubble = new Border
        {
            Padding = new Thickness(12, 8),
            BackgroundColor = Color.FromArgb(isUser ? "#2B2215" : "#1C1917"),
            Stroke = Color.FromArgb(isUser ? "#66501F" : "#292524"),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = sender, TextColor = Color.FromArgb("#A8A29E"), FontSize = 11, FontAttributes = FontAttributes.Bold },
                    contentLabel
                }
            }
        };
        Transcript.Children.Add(bubble);
        _ = TranscriptScroll.ScrollToAsync(bubble, ScrollToPosition.End, animated: true);
        return contentLabel;
    }

    private static string NormalizeModel(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? model[..^7] : model;
}
