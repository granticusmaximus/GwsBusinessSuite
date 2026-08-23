namespace GwsBusinessSuite.SentinelCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var options = CliOptions.Parse(args, Environment.CurrentDirectory);
            if (options.Mode == CliMode.Help)
            {
                PrintHelp();
                return 0;
            }

            using var ollama = new OllamaClient(options.OllamaBaseUri);
            var approval = new ConsoleUserApproval(options.AutoApprove);
            var modelManager = new OllamaModelManager(ollama, approval);
            switch (options.Mode)
            {
                case CliMode.ModelsList:
                    return await modelManager.ListAsync(cancellation.Token);
                case CliMode.ModelsSync:
                    return await modelManager.SyncAsync(cancellation.Token);
                case CliMode.Doctor:
                    return await modelManager.DoctorAsync(cancellation.Token);
            }

            var installed = await ollama.ListModelsAsync(cancellation.Token);
            if (!OllamaModelManager.HasModel(installed, options.Model))
                throw new InvalidOperationException(
                    $"Model '{options.Model}' is not installed. Run 'sentinelgpt models sync' or choose --model <installed-model>.");

            var tools = new WorkspaceTools(options.WorkspaceRoot, approval, options.ReadOnly);
            var agent = new SentinelCodingAgent(ollama, tools, options.Model, options.MaxRounds);
            PrintBanner(options, tools);
            if (!options.IsInteractive)
            {
                Console.WriteLine(await agent.RunTurnAsync(options.Prompt, cancellation.Token));
                return 0;
            }

            while (!cancellation.IsCancellationRequested)
            {
                Console.Write("\nsentinelgpt> ");
                var prompt = Console.ReadLine();
                if (prompt is null || prompt.Trim() is "/exit" or "/quit") break;
                if (string.IsNullOrWhiteSpace(prompt)) continue;
                if (prompt.Trim() == "/clear")
                {
                    agent.ClearConversation();
                    Console.WriteLine("Conversation cleared.");
                    continue;
                }
                if (prompt.Trim() == "/help")
                {
                    PrintSessionHelp();
                    continue;
                }
                if (prompt.Trim() == "/models")
                {
                    await modelManager.ListAsync(cancellation.Token);
                    continue;
                }
                if (prompt.Trim() == "/availablemodels")
                {
                    await BrowseAvailableModelsAsync(ollama, modelManager, cancellation.Token);
                    continue;
                }
                Console.WriteLine(await agent.RunTurnAsync(prompt, cancellation.Token));
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run 'sentinelgpt help' for usage.");
            return 2;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintBanner(CliOptions options, WorkspaceTools tools)
    {
        Console.WriteLine("SentinelGPT Code · local Ollama");
        Console.WriteLine($"Model: {options.Model}");
        Console.WriteLine(tools.DescribeWorkspace());
        Console.WriteLine("Type /help to see session commands.");
    }

    private static void PrintSessionHelp() => Console.WriteLine(
        """

        Session commands:
          /help              Show this list
          /models            List Ollama models installed locally
          /availablemodels   Browse and download additional free Ollama models
          /clear             Start a fresh conversation (keeps the directory and model)
          /quit, /exit       End the session
          Control-C          Cancel the active local model request
        """);

    private static async Task BrowseAvailableModelsAsync(
        OllamaClient ollama, OllamaModelManager modelManager, CancellationToken cancellationToken)
    {
        var installed = await ollama.ListModelsAsync(cancellationToken);
        var suggestions = ModelCatalog.SuggestedFreeModels;
        Console.WriteLine(
            "\nSuggested free Ollama models (a starting point, not the full catalog -- see " +
            "https://ollama.com/library for everything available):\n");
        for (var i = 0; i < suggestions.Count; i++)
        {
            var (name, description) = suggestions[i];
            var installedMark = OllamaModelManager.HasModel(installed, name) ? "  [installed]" : "";
            Console.WriteLine($"  {i + 1,2}. {name,-18}{description}{installedMark}");
        }

        Console.Write("\nEnter a number, or any Ollama model name, to download (blank to cancel): ");
        var selection = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(selection)) return;

        var model = int.TryParse(selection, out var index) && index >= 1 && index <= suggestions.Count
            ? suggestions[index - 1].Name
            : selection;
        await modelManager.PullAsync(model, cancellationToken);
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        SentinelGPT Code - a repository-scoped local coding agent powered by Ollama.

        Usage:
          sentinelgpt [chat] [options] [prompt]
          sentinelgpt models list
          sentinelgpt models sync [--yes]
          sentinelgpt doctor

        Agent options:
          -C, --repo <path>       Workspace or repository root (default: current directory)
          -m, --model <name>      Ollama model (default: qwen2.5-coder)
          --ollama-url <url>      Loopback Ollama URL (default: http://127.0.0.1:11434)
          --read-only             Disable edit and command tools
          -y, --yes               Auto-approve proposed edits and allowlisted commands
          --max-rounds <1-30>     Tool-call round limit (default: 12)

        Examples:
          cd ~/Development && sentinelgpt "Find which repo contains the billing API and analyze it"
          sentinelgpt -C ~/Development/MyRepo "Add tests for the failing parser"
          sentinelgpt --read-only "Review this repository for correctness risks"

        Once inside an interactive session, type /help for session-only commands (/models,
        /availablemodels, /clear, /quit).

        SentinelGPT never reads known secret files, cannot escape the selected workspace, and
        cannot commit, push, deploy, or run destructive git commands.
        """);
}
