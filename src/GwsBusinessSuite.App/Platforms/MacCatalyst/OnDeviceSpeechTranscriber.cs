using AVFoundation;
using Foundation;
using Speech;

namespace GwsBusinessSuite.App;

// Speech to text on this Mac only. Every request sets RequiresOnDeviceRecognition, so audio never
// goes to Apple's servers - if this Mac can't recognise the current language on-device, it says
// so instead of quietly falling back to the cloud. A live recognition task can end on its own
// (long silences, very long recordings); while still recording, a new one starts and the text so
// far is kept, so a meeting-length recording keeps going.
public sealed class OnDeviceSpeechTranscriber : IDisposable
{
    private readonly SFSpeechRecognizer? _recognizer = new(NSLocale.CurrentLocale);
    private AVAudioEngine? _engine;
    private SFSpeechAudioBufferRecognitionRequest? _request;
    private SFSpeechRecognitionTask? _task;
    private string _committed = string.Empty;
    private bool _recording;

    public bool IsRecording => _recording;

    // Null when ready; otherwise the reason it can't transcribe on this Mac.
    public async Task<string?> PrepareAsync()
    {
        if (_recognizer is null || !_recognizer.Available) return "Speech recognition isn't available for this Mac's language.";
        if (!_recognizer.SupportsOnDeviceRecognition)
            return "This Mac can't recognise speech on-device for its current language, and notes never use Apple's servers. Add the language under System Settings > Keyboard > Dictation.";

        var speech = await RequestSpeechAuthorizationAsync();
        if (speech != SFSpeechRecognizerAuthorizationStatus.Authorized)
            return "Speech recognition isn't allowed - turn it on for this app in System Settings > Privacy & Security > Speech Recognition.";
        return null;
    }

    public async Task<string?> StartAsync(Action<string> onText, Action<string> onStopped)
    {
        if (await PrepareAsync() is { } problem) return problem;
        if (!await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio))
            return "Microphone access isn't allowed - turn it on in System Settings > Privacy & Security > Microphone.";

        _committed = string.Empty;
        _engine = new AVAudioEngine();
        var input = _engine.InputNode;
        var format = input.GetBusOutputFormat(0);
        input.InstallTapOnBus(0, 1024, format, (buffer, _) => _request?.Append(buffer));
        _engine.Prepare();
        if (!_engine.StartAndReturnError(out var error))
        {
            input.RemoveTapOnBus(0);
            return $"The microphone couldn't start: {error?.LocalizedDescription}";
        }

        _recording = true;
        BeginTask(onText, onStopped);
        return null;
    }

    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        _engine?.Stop();
        _engine?.InputNode.RemoveTapOnBus(0);
        _request?.EndAudio();
    }

    // Transcribes an existing recording (m4a, mp3, wav...) - also on-device only.
    public async Task<(string? Text, string? Error)> TranscribeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (await PrepareAsync() is { } problem) return (null, problem);
        var request = new SFSpeechUrlRecognitionRequest(NSUrl.FromFilename(path))
        {
            RequiresOnDeviceRecognition = true,
            ShouldReportPartialResults = false
        };
        AddPunctuation(request);
        var done = new TaskCompletionSource<(string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _recognizer!.GetRecognitionTask(request, (result, error) =>
        {
            if (error is not null) done.TrySetResult((null, error.LocalizedDescription));
            else if (result?.Final == true) done.TrySetResult((result.BestTranscription.FormattedString, null));
        });
        using (cancellationToken.Register(() => { task.Cancel(); done.TrySetCanceled(cancellationToken); }))
        {
            return await done.Task;
        }
    }

    private void BeginTask(Action<string> onText, Action<string> onStopped)
    {
        _request = new SFSpeechAudioBufferRecognitionRequest
        {
            RequiresOnDeviceRecognition = true,
            ShouldReportPartialResults = true
        };
        AddPunctuation(_request);
        _task = _recognizer!.GetRecognitionTask(_request, (result, error) =>
        {
            if (result is not null)
            {
                var text = Join(_committed, result.BestTranscription.FormattedString);
                onText(text);
                if (result.Final)
                {
                    _committed = text;
                    if (_recording) BeginTask(onText, onStopped);
                    else onStopped(_committed);
                }
            }
            else if (error is not null)
            {
                if (_recording) BeginTask(onText, onStopped);
                else onStopped(_committed);
            }
        });
    }

    // Automatic punctuation arrived in macOS 13 (Mac Catalyst 16); on older systems the transcript
    // is simply unpunctuated.
    private static void AddPunctuation(SFSpeechRecognitionRequest request)
    {
        if (OperatingSystem.IsMacCatalystVersionAtLeast(16))
        {
            request.AddsPunctuation = true;
        }
    }

    private static string Join(string committed, string next) =>
        string.IsNullOrWhiteSpace(committed) ? next : string.IsNullOrWhiteSpace(next) ? committed : committed + " " + next;

    private static Task<SFSpeechRecognizerAuthorizationStatus> RequestSpeechAuthorizationAsync()
    {
        var done = new TaskCompletionSource<SFSpeechRecognizerAuthorizationStatus>();
        SFSpeechRecognizer.RequestAuthorization(status => done.TrySetResult(status));
        return done.Task;
    }

    public void Dispose()
    {
        Stop();
        _task?.Cancel();
        _engine?.Dispose();
        _recognizer?.Dispose();
    }
}
