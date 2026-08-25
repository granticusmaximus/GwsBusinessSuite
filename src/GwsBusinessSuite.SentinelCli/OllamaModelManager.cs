using System.Diagnostics;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.SentinelAgentKit;

namespace GwsBusinessSuite.SentinelCli;

public sealed class OllamaModelManager(OllamaClient client, IUserApproval approval)
{
    public async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        var models = await client.ListModelsAsync(cancellationToken);
        Console.WriteLine(models.Count == 0 ? "No Ollama models are installed." : string.Join(Environment.NewLine, models));
        return 0;
    }

    public async Task<int> DoctorAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"SentinelGPT CLI: {typeof(OllamaModelManager).Assembly.GetName().Version}");
        Console.WriteLine($"Operating system: {Environment.OSVersion}");
        Console.WriteLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        try
        {
            var models = await client.ListModelsAsync(cancellationToken);
            Console.WriteLine("Ollama API: ready");
            var missing = ModelCatalog.ExpectedInstalledModels
                .Where(required => !HasModel(models, required))
                .ToArray();
            if (missing.Length == 0)
            {
                Console.WriteLine("Canonical GWS models: synchronized");
                return 0;
            }
            Console.WriteLine("Missing models: " + string.Join(", ", missing));
            Console.WriteLine("Run: sentinelgpt models sync");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Console.Error.WriteLine("Ollama API: unavailable");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Start the Ollama macOS app, then run 'sentinelgpt doctor' again.");
            return 1;
        }
    }

    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        var details = "The following local models will be installed or refreshed:\n  " +
                      string.Join("\n  ", ModelCatalog.RequiredModels) +
                      "\n  sentinelgpt (rebuilt from the version-controlled profile)\n\n" +
                      "Model downloads can consume several gigabytes of network traffic and disk space.";
        if (!await approval.ConfirmAsync("Ollama model synchronization", details, cancellationToken))
        {
            Console.WriteLine("Model synchronization cancelled.");
            return 2;
        }

        await EnsureOllamaCommandAsync(cancellationToken);
        foreach (var model in ModelCatalog.RequiredModels)
        {
            Console.WriteLine($"\nPulling {model}...");
            var exitCode = await RunVisibleAsync("ollama", ["pull", model], cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException($"ollama pull {model} failed with exit code {exitCode}.");
        }

        var modelfile = Path.Combine(Path.GetTempPath(), $"SentinelGPT-{Guid.NewGuid():N}.Modelfile");
        try
        {
            await File.WriteAllTextAsync(modelfile, ModelCatalog.SentinelProfile, cancellationToken);
            Console.WriteLine("\nCreating the SentinelGPT profile...");
            var exitCode = await RunVisibleAsync("ollama", ["create", "sentinelgpt", "-f", modelfile], cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException($"ollama create sentinelgpt failed with exit code {exitCode}.");
        }
        finally
        {
            if (File.Exists(modelfile))
                File.Delete(modelfile);
        }

        var installed = await client.ListModelsAsync(cancellationToken);
        var missing = ModelCatalog.ExpectedInstalledModels.Where(required => !HasModel(installed, required)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Ollama did not report the expected models after synchronization: " + string.Join(", ", missing));
        Console.WriteLine("\nAll canonical GWS Ollama models are installed locally.");
        return 0;
    }

    public async Task<bool> PullAsync(string model, CancellationToken cancellationToken)
    {
        var details = $"Download and install the Ollama model '{model}'.\n" +
                      "This can consume several gigabytes of network traffic and disk space.";
        if (!await approval.ConfirmAsync("Ollama model download", details, cancellationToken))
        {
            Console.WriteLine("Download cancelled.");
            return false;
        }

        await EnsureOllamaCommandAsync(cancellationToken);
        Console.WriteLine($"\nPulling {model}...");
        var exitCode = await RunVisibleAsync("ollama", ["pull", model], cancellationToken);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"ollama pull {model} failed with exit code {exitCode}.");
            return false;
        }
        Console.WriteLine($"{model} is installed.");
        return true;
    }

    public static bool HasModel(IEnumerable<string> installedModels, string requiredModel)
    {
        var required = NormalizeModel(requiredModel);
        return installedModels.Any(installed => string.Equals(NormalizeModel(installed), required, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeModel(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? model[..^7] : model;

    private static async Task EnsureOllamaCommandAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await RunVisibleAsync("ollama", ["--version"], cancellationToken);
            if (exitCode != 0) throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "The ollama command was not found. Install and start Ollama for macOS, then retry.", ex);
        }
    }

    private static async Task<int> RunVisibleAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {program}.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
