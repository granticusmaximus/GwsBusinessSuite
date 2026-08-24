namespace GwsBusinessSuite.App;

// Thin wrapper over MAUI's built-in cross-platform TextToSpeech (native AVSpeechSynthesizer on
// Mac Catalyst/iOS, TextToSpeech on Android, SpeechSynthesizer on Windows) - already exactly
// what "speak responses" needs on every platform this app targets, so there's no reason to hand-
// write platform-specific AVFoundation interop for the Mac Catalyst case alone.
public sealed class SentinelVoiceService
{
    private CancellationTokenSource? _speaking;

    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Stop();
        _speaking = new CancellationTokenSource();
        try
        {
            await TextToSpeech.Default.SpeakAsync(text, cancelToken: _speaking.Token);
        }
        catch (OperationCanceledException)
        {
            // Stop() was called mid-utterance - expected, not an error.
        }
    }

    public void Stop()
    {
        _speaking?.Cancel();
        _speaking?.Dispose();
        _speaking = null;
    }
}
