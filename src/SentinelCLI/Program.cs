using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.SentinelAgentKit;

namespace GwsBusinessSuite.SentinelCLI;

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
                    $"Model '{options.Model}' is not installed. Run 'sentinelcli models sync' or choose --model <installed-model>.");

            var tools = new WorkspaceTools(options.WorkspaceRoot, approval, options.ReadOnly);
            var agent = new SentinelCodingAgent(ollama, tools, options.Model, options.MaxRounds);
            var sessionStore = new SessionStore(SessionsDirectory());
            var skills = new SkillLibrary(SkillsDirectory());
            PrintBanner(options, tools);
            if (!options.IsInteractive)
            {
                Console.WriteLine(await agent.RunTurnAsync(options.Prompt, cancellation.Token));
                return 0;
            }

            string? currentSessionPath = null;

            // Shared by the main loop and /skills so a turn is always saved the same way,
            // regardless of what triggered it - and so a Ctrl-C or a --max-rounds overrun during
            // *one* turn lands back at the prompt instead of ending the whole session (both
            // previously propagated out of this loop to Main's outer catch).
            async Task RunTurnSafelyAsync(string turnPrompt)
            {
                try
                {
                    Console.WriteLine(await agent.RunTurnAsync(turnPrompt, cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled.");
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                }
                finally
                {
                    try
                    {
                        currentSessionPath = await sessionStore.SaveAsync(
                            currentSessionPath, tools.Root, options.Model, agent.Messages, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    {
                        Console.Error.WriteLine($"Warning: could not save this session ({ex.Message}).");
                    }
                }
            }

            while (!cancellation.IsCancellationRequested)
            {
                Console.Write("\nsentinelcli> ");
                var rawPrompt = Console.ReadLine();
                if (rawPrompt is null || rawPrompt.Trim() is "/exit" or "/quit") break;
                if (string.IsNullOrWhiteSpace(rawPrompt)) continue;
                var prompt = rawPrompt.Trim();

                if (prompt == "/clear")
                {
                    agent.ClearConversation();
                    currentSessionPath = null;
                    Console.WriteLine("Conversation cleared.");
                    continue;
                }
                if (prompt == "/help")
                {
                    PrintSessionHelp();
                    continue;
                }
                if (prompt == "/models")
                {
                    await modelManager.ListAsync(cancellation.Token);
                    continue;
                }
                if (prompt == "/availablemodels")
                {
                    await BrowseAvailableModelsAsync(ollama, modelManager, cancellation.Token);
                    continue;
                }
                if (prompt == "/plan")
                {
                    tools.SetPlanMode(true);
                    agent.RefreshSystemPrompt();
                    Console.WriteLine("Plan mode on - edits and commands are disabled; ask for a plan, then /act to apply it.");
                    continue;
                }
                if (prompt == "/act")
                {
                    tools.SetPlanMode(false);
                    agent.RefreshSystemPrompt();
                    Console.WriteLine(tools.EffectiveReadOnly
                        ? "Plan mode off, but --read-only is still active for this session."
                        : "Plan mode off - edits and commands are enabled again.");
                    continue;
                }
                if (TryMatchCommand(prompt, "/agent", out var agentArg))
                {
                    HandleAgentCommand(agent, agentArg);
                    continue;
                }
                if (TryMatchCommand(prompt, "/skills", out var skillArg))
                {
                    var invocation = ParseSkillInvocation(skillArg, skills);
                    if (invocation is { } skillPrompt)
                        await RunTurnSafelyAsync(skillPrompt);
                    continue;
                }
                if (prompt == "/resume")
                {
                    currentSessionPath = await ResumeAsync(sessionStore, tools.Root, options.Model, agent, cancellation.Token) ?? currentSessionPath;
                    continue;
                }
                if (TryMatchCommand(prompt, "/fleet", out var fleetArg))
                {
                    await HandleFleetCommandAsync(ollama, tools.Root, options.MaxRounds, fleetArg, cancellation.Token);
                    continue;
                }

                await RunTurnSafelyAsync(prompt);
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
            Console.Error.WriteLine("Run 'sentinelcli help' for usage.");
            return 2;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // Environment.SpecialFolder.LocalApplicationData resolves to ~/Library/Application Support
    // on macOS, not ~/.local/share - built explicitly instead, to land alongside the installed
    // binary itself (install-sentinelcli.sh's own $HOME/.local/share/gws/sentinelcli default).
    private static string SessionsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "gws", "sentinelcli", "sessions");

    private static string SkillsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sentinelcli", "skills");

    // Matches "/command" (no argument) or "/command <rest of the line>" - used by the three
    // commands that carry an argument. The plain single-string-equality commands elsewhere in
    // the loop don't need this since they never take one.
    private static bool TryMatchCommand(string prompt, string command, out string argument)
    {
        if (prompt == command)
        {
            argument = string.Empty;
            return true;
        }
        if (prompt.StartsWith(command + " ", StringComparison.Ordinal))
        {
            argument = prompt[(command.Length + 1)..].Trim();
            return true;
        }
        argument = string.Empty;
        return false;
    }

    private static void HandleAgentCommand(SentinelCodingAgent agent, string agentArg)
    {
        if (string.IsNullOrEmpty(agentArg))
        {
            Console.WriteLine("\nAvailable agents:");
            foreach (var persona in AgentPersonas.All)
            {
                var marker = persona == agent.ActivePersona ? "  [active]" : "";
                Console.WriteLine($"  {persona.Name,-14}{persona.Description}{marker}");
            }
            return;
        }

        var selected = AgentPersonas.Find(agentArg);
        if (selected is null)
        {
            Console.Error.WriteLine($"Unknown agent: {agentArg}. Run /agent to see the list.");
            return;
        }
        agent.SetPersona(selected);
        Console.WriteLine($"Agent set to '{selected.Name}'.");
    }

    // Returns the turn to run through RunTurnSafelyAsync, or null if this call was just listing
    // skills / reporting a usage error and there's nothing to run.
    private static string? ParseSkillInvocation(string skillArg, SkillLibrary skills)
    {
        if (string.IsNullOrEmpty(skillArg))
        {
            var names = skills.List();
            Console.WriteLine($"\nSkills in {skills.Directory}:");
            Console.WriteLine(names.Count == 0
                ? "  (none found - add a .md file there; its filename becomes the skill name)"
                : string.Join(Environment.NewLine, names.Select(name => $"  {name}")));
            Console.WriteLine("\nUse /skills <name> <prompt> to apply one for a single turn.");
            return null;
        }

        var spaceIndex = skillArg.IndexOf(' ');
        var skillName = spaceIndex < 0 ? skillArg : skillArg[..spaceIndex];
        var skillPrompt = spaceIndex < 0 ? string.Empty : skillArg[(spaceIndex + 1)..].Trim();
        var instructions = skills.Load(skillName);
        if (instructions is null)
        {
            Console.Error.WriteLine($"Unknown skill: {skillName}. Run /skills to see what's available.");
            return null;
        }
        if (string.IsNullOrEmpty(skillPrompt))
        {
            Console.Error.WriteLine("Usage: /skills <name> <prompt>");
            return null;
        }
        return $"Follow this skill's instructions for this request.\n\nSkill '{skillName}':\n{instructions}\n\nRequest: {skillPrompt}";
    }

    private static async Task<string?> ResumeAsync(
        SessionStore sessionStore, string workspaceRoot, string currentModel, SentinelCodingAgent agent, CancellationToken cancellationToken)
    {
        var saved = sessionStore.ListForWorkspace(workspaceRoot);
        if (saved.Count == 0)
        {
            Console.WriteLine("No saved sessions for this workspace yet.");
            return null;
        }

        Console.WriteLine("\nSaved sessions for this workspace:");
        for (var i = 0; i < saved.Count; i++)
        {
            var session = saved[i].Session;
            var preview = session.Messages.LastOrDefault(message => message.Role == "user")?.Content ?? "(no messages yet)";
            if (preview.Length > 60) preview = preview[..60] + "...";
            Console.WriteLine($"  {i + 1,2}. {session.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {preview}");
        }

        Console.Write("\nResume which one? (number, blank to cancel): ");
        var selection = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(selection)) return null;
        if (!int.TryParse(selection, out var index) || index < 1 || index > saved.Count)
        {
            Console.Error.WriteLine("Not a valid selection.");
            return null;
        }

        var (path, loaded) = saved[index - 1];
        agent.LoadConversation(loaded.Messages);
        Console.WriteLine($"Resumed session from {loaded.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}.");
        if (!string.Equals(loaded.Model, currentModel, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Note: recorded with model '{loaded.Model}'; this session is using '{currentModel}'.");
        await Task.CompletedTask;
        return path;
    }

    private static async Task HandleFleetCommandAsync(
        OllamaClient ollama, string workspaceRoot, int maxRounds, string fleetArg, CancellationToken cancellationToken)
    {
        var spaceIndex = fleetArg.IndexOf(' ');
        var modelsPart = spaceIndex < 0 ? fleetArg : fleetArg[..spaceIndex];
        var fleetPrompt = spaceIndex < 0 ? string.Empty : fleetArg[(spaceIndex + 1)..].Trim();
        var models = modelsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (models.Length == 0 || string.IsNullOrEmpty(fleetPrompt))
        {
            Console.Error.WriteLine("Usage: /fleet <model,model,...> <prompt>");
            return;
        }

        // Always read-only, unconditionally, independent of /plan or --read-only: N concurrent
        // agents could otherwise each try to prompt "Apply? [y/N]" on the same stdin, and
        // concurrent writes to the same files from different models is a real correctness
        // hazard. One shared instance across all N agents is safe - its read methods touch no
        // mutable instance state - and UnreachableApproval turns "fleet never needs approval"
        // from an assumption into an assertion.
        var fleetTools = new WorkspaceTools(workspaceRoot, new UnreachableApproval(), readOnly: true, quiet: true);
        Console.WriteLine($"\nRunning {models.Length} model(s) in parallel (read-only)...");
        var results = await Task.WhenAll(models.Select(model => RunOneAsync(ollama, fleetTools, model, fleetPrompt, maxRounds, cancellationToken)));
        foreach (var (model, answer, error) in results)
        {
            Console.WriteLine($"\n=== {model} ===");
            Console.WriteLine(error is null ? answer : $"Error: {error}");
        }

        static async Task<(string Model, string? Answer, string? Error)> RunOneAsync(
            OllamaClient ollama, WorkspaceTools fleetTools, string model, string prompt, int maxRounds, CancellationToken cancellationToken)
        {
            try
            {
                var agent = new SentinelCodingAgent(ollama, fleetTools, model, maxRounds);
                return (model, await agent.RunTurnAsync(prompt, cancellationToken), null);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // OperationCanceledException (Ctrl-C) is deliberately not caught here - a
                // cancellation should abort the whole fleet run, not read as "one model failed."
                return (model, null, ex.Message);
            }
        }
    }

    private static void PrintBanner(CliOptions options, WorkspaceTools tools)
    {
        Console.WriteLine("SentinelCLI · local Ollama");
        Console.WriteLine($"Model: {options.Model}");
        Console.WriteLine(tools.DescribeWorkspace());
        Console.WriteLine("Type /help to see session commands.");
    }

    private static void PrintSessionHelp() => Console.WriteLine(
        """

        Session commands:
          /help                        Show this list
          /models                      List Ollama models installed locally
          /availablemodels             Browse and download additional free Ollama models
          /plan                        Switch to read-only planning (no edits or commands)
          /act                         Switch back to normal (edits/commands allowed again)
          /agent [name]                List agents, or switch to one (shapes tone/priorities)
          /skills [name] [prompt]      List skills, or apply one to a single request
          /resume                      Continue a previous session for this workspace
          /fleet <models> <prompt>     Run the same prompt across several models, e.g.
                                        /fleet llama3.2,deepseek-r1 explain this bug
          /clear                       Start a fresh conversation (keeps the directory and model)
          /quit, /exit                 End the session
          Control-C                    Cancel the active local model request

        Every turn is saved automatically so /resume can pick it back up later - one JSON file
        per session under ~/.local/share/gws/sentinelcli/sessions, no locking between concurrent
        terminals against the same workspace (last write wins). Skills are markdown files you add
        yourself under ~/.config/sentinelcli/skills/. Fleet runs may feel serialized rather than
        truly parallel on constrained hardware - that's Ollama's own model-loading limits, not
        this tool.
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
        SentinelCLI - a repository-scoped local coding agent powered by Ollama.

        Usage:
          sentinelcli [chat] [options] [prompt]
          sentinelcli models list
          sentinelcli models sync [--yes]
          sentinelcli doctor

        Agent options:
          -C, --repo <path>       Workspace or repository root (default: current directory)
          -m, --model <name>      Ollama model (default: qwen2.5-coder)
          --ollama-url <url>      Loopback Ollama URL (default: http://127.0.0.1:11434)
          --read-only             Disable edit and command tools
          -y, --yes                Auto-approve proposed edits and allowlisted commands
          --max-rounds <1-30>      Tool-call round limit (default: 12)

        Examples:
          cd ~/Development && sentinelcli "Find which repo contains the billing API and analyze it"
          sentinelcli -C ~/Development/MyRepo "Add tests for the failing parser"
          sentinelcli --read-only "Review this repository for correctness risks"

        Once inside an interactive session, type /help for session-only commands - including
        /plan (plan before editing), /agent and /skills (shape behavior), /resume (continue a
        previous session), and /fleet (compare several models on the same prompt at once).

        SentinelCLI never reads known secret files, cannot escape the selected workspace, and
        cannot commit, push, deploy, or run destructive git commands.
        """);
}
