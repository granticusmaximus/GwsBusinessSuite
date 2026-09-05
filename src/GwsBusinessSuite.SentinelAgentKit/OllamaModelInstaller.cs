using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.SentinelAgentKit;

// One suggested model, joined against what's actually on this machine.
public sealed record BrowsableModel(SuggestedModel Model, bool IsInstalled);

// Installs and repairs the canonical GWS model set over Ollama's HTTP API.
//
// SentinelCLI's OllamaModelManager does the same job by shelling out to `ollama pull` / `ollama
// create`, which is fine for a console tool but impossible in the sandboxed Mac app: App Sandbox
// denies process execution outright, so "run 'sentinelcli models sync' in Terminal" was the only
// advice the app could give. This does the identical work over the loopback API the app is
// already allowed to use, from the same embedded profile text, so the model set can be installed
// and repaired from inside the app.
public sealed class OllamaModelInstaller(OllamaClient client)
{
    public const string SentinelModelName = "sentinelgpt";

    // The suggested catalogue with an "already have it" flag. Returns the catalogue even when
    // Ollama can't be reached, so the browser can still show what's on offer rather than an
    // empty list - nothing is marked installed in that case, which is the safe direction to be
    // wrong in (an install of something already present is a fast no-op).
    public async Task<IReadOnlyList<BrowsableModel>> BrowseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> installed;
        try
        {
            installed = await client.ListModelsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            installed = [];
        }

        return ModelCatalog.SuggestedFreeModels
            .Select(model => new BrowsableModel(model, HasModel(installed, model.Name)))
            .ToArray();
    }

    public IAsyncEnumerable<OllamaProgress> InstallAsync(string model, CancellationToken cancellationToken) =>
        client.PullModelAsync(model, cancellationToken);

    // Pulls every canonical base model that's missing, then rebuilds the SentinelGPT profile.
    // Yields a human-readable line per step so a caller can narrate progress without knowing
    // anything about Ollama's own status vocabulary.
    public async IAsyncEnumerable<string> SyncAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var installed = await client.ListModelsAsync(cancellationToken);
        foreach (var model in ModelCatalog.RequiredModels)
        {
            if (HasModel(installed, model))
            {
                yield return $"{model} is already installed.";
                continue;
            }

            yield return $"Downloading {model}...";
            await foreach (var progress in client.PullModelAsync(model, cancellationToken))
            {
                if (progress.Fraction is { } fraction)
                    yield return $"{model}: {fraction:P0}";
            }
            yield return $"{model} installed.";
        }

        // Always rebuilt, never skipped when present: the profile is version-controlled and its
        // base model has changed before, so an existing sentinelgpt says nothing about whether
        // it matches the profile this build ships.
        yield return "Building the SentinelGPT profile...";
        var profile = OllamaModelfileParser.Parse(ModelCatalog.SentinelProfile);
        await foreach (var _ in client.CreateModelAsync(SentinelModelName, profile, cancellationToken)) { }
        yield return $"SentinelGPT is ready (based on {profile.From}).";
    }

    // Ollama reports "gemma4:latest" for a model requested as "gemma4"; comparing raw would
    // re-download everything on every sync.
    public static bool HasModel(IEnumerable<string> installedModels, string requiredModel)
    {
        var required = Normalize(requiredModel);
        return installedModels.Any(installed =>
            string.Equals(Normalize(installed), required, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? model[..^7] : model;
}
