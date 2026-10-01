using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Application.Abstractions;

public enum ServerAiMode
{
    // Every AI feature runs against the Ollama instance this server is configured with. The
    // default, so local development (dotnet run against a local Ollama) and tests are unchanged.
    Full,

    // The production droplet. Its Ollama holds only a small model, enough for short summaries
    // (Media Watch, Civic Watch overviews, Content Studio trend research). Heavy generation -
    // SentinelGPT chat/agents, multi-model advisors, app generation, and server-side article
    // generation - is refused here; Content Studio drafts and revisions instead run on the Ollama
    // of the machine the browser is on (see BrowserLocalOllamaService).
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
        "Use SentinelGPT in the GWS Suite Mac app, which runs on your own machine. Summaries still work here.";

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
