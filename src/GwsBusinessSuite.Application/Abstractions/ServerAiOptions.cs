using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.Abstractions;

public enum ServerAiMode
{
    // Every AI feature runs against the Ollama instance this server is configured with. The
    // default, so local development (dotnet run against a local Ollama) and tests are unchanged.
    Full,

    // The production droplet. Its Ollama holds only a small model. Summaries (Media Watch, Civic
    // Watch overviews, Content Studio trend research), SentinelGPT chat and ai.* automation nodes
    // run on it. Refused here: SentinelGPT panel actions other than Summarize, app generation,
    // and server-side article generation/revision/hero images - articles are written only in
    // the Mac app (NativeContentStudioAccess), on that Mac's own Ollama via
    // BrowserLocalOllamaService.
    Light
}

public sealed class ServerAiOptions
{
    public const string SectionName = "ServerAi";

    public ServerAiMode Mode { get; init; } = ServerAiMode.Full;

    public bool IsLight => Mode == ServerAiMode.Light;
}

// Derives from InvalidOperationException on purpose: every UI surface that can reach one of
// these gated features already catches InvalidOperationException and shows its message, so the
// refusal reads as a clear explanation instead of a crash.
public sealed class ServerAiUnavailableException(string feature, string alternative)
    : InvalidOperationException(
        $"{feature} is turned off on this server to keep the site fast. {alternative}");

public static class ServerAiGuard
{
    public const string UseLocalSentinelGpt =
        "Use SentinelGPT in the GWS Suite Mac app, which runs on your own machine. Chat and summaries still work here.";

    public static bool IsLight(this IOptions<ServerAiOptions>? options) => options?.Value.IsLight == true;

    public static void EnsureHeavyAiAllowed(
        this IOptions<ServerAiOptions>? options,
        string feature,
        string alternative = UseLocalSentinelGpt)
    {
        if (options.IsLight())
        {
            throw new ServerAiUnavailableException(feature, alternative);
        }
    }
}
