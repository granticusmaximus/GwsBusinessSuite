using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.OllamaKit;
#if MACCATALYST
using UIKit;
#endif

namespace GwsBusinessSuite.App;

// Screen capture to text: paste a screenshot (Control-Shift-Command-4 copies a dragged area) or
// pick an image, read its text on this Mac (Apple Vision), then save it to Sentinel, ask the
// local model about it, or pull a contact out of it (e.g. an email signature) into the CRM. The
// app is sandboxed, so it can't run macOS's screencapture tool itself - the system shortcut does
// the capturing. The image never leaves the Mac; only text you choose to save does.
public partial class CapturePage : ContentPage
{
    private const string ContactPrompt =
        "Extract one person's contact details from the text (often an email signature). Reply with JSON only: " +
        "{\"fullName\":\"\",\"email\":\"\",\"company\":\"\"}. Use \"\" for anything the text doesn't contain - never guess.";

    private readonly OllamaClient _ollama;
    private readonly NativeSessionClient _session;

    public CapturePage(OllamaClient ollama, NativeSessionClient session)
    {
        InitializeComponent();
        _ollama = ollama;
        _session = session;
#if !MACCATALYST
        StatusLabel.Text = "Screen capture to text runs in the Mac app.";
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if MACCATALYST
        MacToolbarActions.SyncSelectedTab?.Invoke("CapturePage");
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
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // Reading text needs no model; only Ask and Find a contact do, and they say so.
        }
    }

    private async void OnPasteClicked(object? sender, EventArgs e)
    {
#if MACCATALYST
        if (UIPasteboard.General.Image is not UIImage image)
        {
            StatusLabel.Text = "There's no image on the clipboard - press Control-Shift-Command-4, drag over the area, then paste.";
            return;
        }
        await ReadImageAsync(image, $"Capture - {DateTime.Now:MMM d, h:mm tt}");
#else
        await Task.CompletedTask;
#endif
    }

    private async void OnChooseClicked(object? sender, EventArgs e)
    {
#if MACCATALYST
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose an image", FileTypes = FilePickerFileType.Images });
        if (file is null) return;
        if (UIImage.FromFile(file.FullPath) is not UIImage image)
        {
            StatusLabel.Text = "That file isn't an image this Mac can read.";
            return;
        }
        await ReadImageAsync(image, Path.GetFileNameWithoutExtension(file.FileName));
#else
        await Task.CompletedTask;
#endif
    }

#if MACCATALYST
    private async Task ReadImageAsync(UIImage image, string title)
    {
        if (image.AsPNG() is { } png)
        {
            var bytes = png.ToArray();
            PreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        StatusLabel.Text = "Reading the text on this Mac...";
        var (text, error) = await OnDeviceTextRecognizer.RecognizeAsync(image);
        if (error is not null)
        {
            StatusLabel.Text = error;
            return;
        }
        TextEditor.Text = text;
        TitleEntry.Text = title;
        AnswerLabel.Text = string.Empty;
        StatusLabel.Text = "Done. Edit the text if needed, then save, ask, or pull out a contact.";
    }
#endif

    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextEditor.Text)) return;
        await Clipboard.Default.SetTextAsync(TextEditor.Text);
        StatusLabel.Text = "Text copied.";
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var text = TextEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusLabel.Text = "There's no text to save yet.";
            return;
        }
        var title = string.IsNullOrWhiteSpace(TitleEntry.Text) ? $"Capture - {DateTime.Now:MMM d, h:mm tt}" : TitleEntry.Text.Trim();
        var toDailyNote = DailyNoteCheckBox.IsChecked;
        var result = await _session.SaveQuickNoteAsync(title, text, toDailyNote);
        StatusLabel.Text = result.Error ?? (toDailyNote
            ? $"Added \"{title}\" to today's daily note."
            : $"Saved to Sentinel under Quick Notes as \"{title}\".");
    }

    private async void OnAskClicked(object? sender, EventArgs e)
    {
        var text = TextEditor.Text?.Trim();
        var question = QuestionEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(question)) return;
        if (ModelPicker.SelectedItem is not string model)
        {
            AnswerLabel.Text = "Start Ollama on this Mac and pick a model to ask questions.";
            return;
        }
        AskButton.IsEnabled = false;
        AnswerLabel.Text = "Thinking...";
        try
        {
            var result = await _ollama.ChatAsync(model,
            [
                new OllamaChatMessage("system", "Answer the question using only the captured text. If the text doesn't say, reply that it doesn't. Be brief."),
                new OllamaChatMessage("user", $"Captured text:\n{text}\n\nQuestion: {question}")
            ], [], CancellationToken.None, think: false);
            AnswerLabel.Text = result.Content.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            AnswerLabel.Text = $"The local model couldn't answer: {ex.Message}";
        }
        finally
        {
            AskButton.IsEnabled = true;
        }
    }

    // Fills the contact fields for review; nothing is created until "Add to CRM".
    private async void OnFindContactClicked(object? sender, EventArgs e)
    {
        var text = TextEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        // An email address is found exactly, without a model; the model adds name and company.
        var email = EmailPattern().Match(text) is { Success: true } match ? match.Value : string.Empty;
        ContactEmailEntry.Text = email;
        if (ModelPicker.SelectedItem is not string model)
        {
            StatusLabel.Text = email.Length > 0 ? "Found an email address - add the name yourself (no local model to read the rest)." : "No email address found in the text.";
            return;
        }

        FindContactButton.IsEnabled = false;
        StatusLabel.Text = "Reading the contact details with the local model...";
        try
        {
            var result = await _ollama.ChatAsync(model,
                [new OllamaChatMessage("system", ContactPrompt), new OllamaChatMessage("user", text)], [], CancellationToken.None, think: false);
            var (name, foundEmail, company) = ParseContact(result.Content);
            ContactNameEntry.Text = name;
            if (email.Length == 0) ContactEmailEntry.Text = foundEmail;
            ContactCompanyEntry.Text = company;
            StatusLabel.Text = "Check the contact details, then Add to CRM.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusLabel.Text = $"The local model couldn't read it: {ex.Message}";
        }
        finally
        {
            FindContactButton.IsEnabled = true;
        }
    }

    private async void OnAddContactClicked(object? sender, EventArgs e)
    {
        var email = ContactEmailEntry.Text?.Trim() ?? string.Empty;
        if (!EmailPattern().IsMatch(email))
        {
            StatusLabel.Text = "A contact needs a valid email address.";
            return;
        }
        var result = await _session.CreateContactAsync(ContactNameEntry.Text?.Trim() ?? string.Empty, email, ContactCompanyEntry.Text?.Trim() ?? string.Empty);
        if (result.Error is not null)
        {
            StatusLabel.Text = result.Error;
            return;
        }
        StatusLabel.Text = $"{result.FullName} is in the CRM - opening them on the Workspace tab.";
        WorkspaceNavigation.Request(result.Url!);
        await Shell.Current.GoToAsync("//MainPage");
    }

    // Lenient: small models sometimes wrap the JSON in prose.
    public static (string Name, string Email, string Company) ParseContact(string modelOutput)
    {
        var json = JsonObjectPattern().Match(modelOutput ?? string.Empty);
        if (!json.Success) return (string.Empty, string.Empty, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(json.Value);
            string Read(string key) => document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim()
                : string.Empty;
            return (Read("fullName"), Read("email"), Read("company"));
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectPattern();
}
