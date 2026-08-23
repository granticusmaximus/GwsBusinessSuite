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
        Console.WriteLine("Commands: /clear, /quit");
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

        SentinelGPT never reads known secret files, cannot escape the selected workspace, and
        cannot commit, push, deploy, or run destructive git commands.
        """);
}
