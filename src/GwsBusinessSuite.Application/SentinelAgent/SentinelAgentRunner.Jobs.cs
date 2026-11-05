using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.SentinelAgent;

// Jobs (05-11-26). Grant: "allow for the LLM to think about what the task is asking for, to
// deliberate over what can be done, to consider an appropriate plan, and to execute the steps of
// that plan. And if it needs to be broken into different sections of work, do so and build the
// pathway for SentinelGPT to traverse." Before this a turn had 8 rounds, then gave up ("I couldn't
// finish that in 8 steps") or answered "Done: ..." for half a job.
//
// 1. Think: one call with no tools works out what's asked, what done looks like, what this person
//    may do, the approach, and a plan in sections. Each step says how to check it worked.
// 2. The plan is a show_plan action whose ArgumentsJson keeps the whole job (the ledger), so the
//    job outlives the turn. Go (a plan over RoutineChangePlanThreshold steps waits for it) and
//    "carry on" resume it from there.
// 3. Each section runs as its own leg of up to MaxRounds rounds, with a fresh context: the goal,
//    the approach, the plan with each step's state, and what earlier sections changed. That way a
//    small local model never holds the whole job in its head. complete_step and skip_step move the
//    ledger. An unfinished section gets another leg.
// 4. A checking leg with only the read tools compares what was made with each step's check.
//    Problems get one repair leg and one more check. Only then is it "Done and checked".
// Budget: MaxLegs legs and JobBudget a turn. Past that the job stops as "Partly done" with exactly
// what's left, and "carry on" continues it.
public sealed partial class SentinelAgentRunner
{
    public const string PlanJobToolName = "plan_job";
    public const string CompleteStepToolName = "complete_step";
    public const string SkipStepToolName = "skip_step";
    public const string ContinueMessage = "Carry on with the job.";
    private const int MaxSections = 8;
    private const int MaxStepsPerSection = 10;
    private const int MaxJobSteps = 40;
    private const int LegsPerSection = 2;
    private const int MaxLegs = 16;
    private static readonly TimeSpan JobBudget = TimeSpan.FromMinutes(30);
    private static readonly string[] CarryOnPhrases =
        ["carry on", "continue", "keep going", "go on", "finish it", "finish the job", "carry on with the job"];
    private static readonly JsonSerializerOptions JobJson = new(JsonSerializerDefaults.Web);

    private static readonly OllamaToolDefinition PlanJobDefinition = new(
        PlanJobToolName,
        "Think a bigger job through and plan it before changing anything: any request with more than 3 steps, more than one record or more than one part of the app. The app plans it in sections with you, then carries it out step by step, with no limit on steps.",
        SentinelAgentJson.Schema(("request", "string", "What the person wants done, in your own words", true)));

    private static readonly OllamaToolDefinition CompleteStepDefinition = new(
        CompleteStepToolName,
        "Mark a step of the plan done once its change is made (or its answer found). Call it straight after each step.",
        SentinelAgentJson.Schema(("step", "integer", "The step's number", true), ("result", "string", "What you did, in a few words", true)));

    private static readonly OllamaToolDefinition SkipStepDefinition = new(
        SkipStepToolName,
        "Mark a step that can't or shouldn't be done (not allowed, doesn't apply, or waiting for the person's Confirm), with why.",
        SentinelAgentJson.Schema(("step", "integer", "The step's number", true), ("why", "string", "Why it's skipped", true)));

    private sealed record JobStart(
        IServiceProvider Services,
        IAppDbContext Db,
        IReadOnlyDictionary<string, ISentinelAgentTool> Tools,
        SentinelAgentToolContext Context,
        SentinelAgentRequest Request,
        List<SentinelAgentActionView> Actions,
        IReadOnlyList<OllamaChatMessage> Messages);

    private enum LegMode { Do, Repair, Check }

    public static class JobStates
    {
        public const string Pending = "pending";
        public const string Done = "done";
        public const string Checked = "checked";
        public const string Skipped = "skipped";
        public const string Failed = "failed";
    }

    public static class JobStatuses
    {
        public const string AwaitingGo = "awaiting-go";
        public const string Running = "running";
        public const string Partial = "partial";
        public const string Done = "done";
    }

    public sealed class AgentJob
    {
        public string Request { get; set; } = string.Empty;
        public string Understanding { get; set; } = string.Empty;
        public string Approach { get; set; } = string.Empty;
        public List<string> Cannot { get; set; } = [];
        public List<AgentJobSection> Sections { get; set; } = [];
        public string Status { get; set; } = JobStatuses.Running;

        [JsonIgnore] public IEnumerable<AgentJobStep> Steps => Sections.SelectMany(section => section.Steps);
    }

    public sealed class AgentJobSection
    {
        public string Title { get; set; } = string.Empty;
        public List<AgentJobStep> Steps { get; set; } = [];
    }

    public sealed class AgentJobStep
    {
        public int Number { get; set; }
        public string Do { get; set; } = string.Empty;
        public string Check { get; set; } = string.Empty;
        public string State { get; set; } = JobStates.Pending;
        public string? Note { get; set; }
    }

    // Think it through and plan, then carry it out unless the plan is big enough to need Go.
    private async Task<SentinelAgentTurn> RunJobAsync(JobStart start, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        await onEvent(new SentinelAgentStep("Thinking the job through"));
        var job = await DeliberateAsync(start, cancellationToken);
        var needsGo = job.Steps.Count() > RoutineChangePlanThreshold;
        job.Status = needsGo ? JobStatuses.AwaitingGo : JobStatuses.Running;
        var plan = new SentinelAgentAction
        {
            ConversationId = start.Context.ConversationId,
            Tool = PlanToolName,
            ArgumentsJson = SaveJob(job),
            Status = needsGo ? SentinelAgentActionStatuses.AwaitingConfirm : SentinelAgentActionStatuses.Done,
            Summary = RenderJob(job),
            CreatedAt = time.GetUtcNow(),
            CreatedBy = start.Context.User.Username
        };
        start.Db.SentinelAgentActions.Add(plan);
        await start.Db.SaveChangesAsync(cancellationToken);
        var view = View(plan, null);
        start.Actions.Add(view);
        await onEvent(new SentinelAgentActed(view));
        await onEvent(new SentinelAgentStep($"Planned {Plural(job.Sections.Count, "section")}, {Plural(job.Steps.Count(), "step")}"));
        if (needsGo)
            return await FinishJobTurnAsync(start, "Here's my plan. Press Go and I'll carry it out, or tell me what to change.", cancellationToken);
        return await CarryOutAsync(start, plan, job, onEvent, cancellationToken);
    }

    // "Go" on a job's plan, or "carry on": pick the saved job up where it stopped. Null when this
    // message isn't one of those, or the chat has no unfinished job (an old show_plan carries on as
    // before, through the working set).
    private async Task<SentinelAgentTurn?> ResumeJobAsync(
        IServiceProvider services, IAppDbContext db, IReadOnlyDictionary<string, ISentinelAgentTool> tools, SentinelAgentToolContext context,
        SentinelAgentRequest request, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        var message = request.Message.Trim();
        var asked = message == GoMessage || message == ContinueMessage
            || CarryOnPhrases.Contains(message.TrimEnd('.', '!').ToLowerInvariant(), StringComparer.Ordinal);
        if (!asked) return null;
        var plan = (await db.SentinelAgentActions
                .Where(action => action.ConversationId == request.ConversationId && action.CreatedBy == request.User.Username && action.Tool == PlanToolName)
                .ToListAsync(cancellationToken))
            .OrderByDescending(action => action.CreatedAt)
            .FirstOrDefault();
        // Go is pressed through ConfirmAsync (the plan turns Done) before the panel sends GoMessage.
        if (plan is not { Status: SentinelAgentActionStatuses.Done } || LoadJob(plan) is not { } job || job.Status == JobStatuses.Done) return null;

        job.Status = JobStatuses.Running;
        var view = View(plan, null);
        await onEvent(new SentinelAgentActed(view));
        var start = new JobStart(services, db, tools, context, request, [view], []);
        return await CarryOutAsync(start, plan, job, onEvent, cancellationToken);
    }

    private async Task<SentinelAgentTurn> CarryOutAsync(
        JobStart start, SentinelAgentAction plan, AgentJob job, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        bool WithinBudget() => time.GetUtcNow() - started < JobBudget;
        var legs = 0;
        for (var index = 0; index < job.Sections.Count; index++)
        {
            var section = job.Sections[index];
            for (var leg = 0; leg < LegsPerSection && legs < MaxLegs && WithinBudget()
                 && section.Steps.Any(step => step.State == JobStates.Pending); leg++, legs++)
            {
                await onEvent(new SentinelAgentStep($"Section {index + 1} of {job.Sections.Count}: {section.Title}"));
                await RunLegAsync(start, plan, job, LegMode.Do, index, onEvent, cancellationToken);
            }
        }

        // Check what was done; fix what didn't check out (and anything a previous check failed), then check that again.
        if (WithinBudget() && job.Steps.Any(step => step.State == JobStates.Done)) await CheckAsync(start, plan, job, onEvent, cancellationToken);
        var failed = job.Steps.Where(step => step.State == JobStates.Failed).Select(step => step.Number).ToList();
        if (failed.Count > 0 && legs < MaxLegs && WithinBudget())
        {
            await onEvent(new SentinelAgentStep("Fixing what didn't check out"));
            await RunLegAsync(start, plan, job, LegMode.Repair, null, onEvent, cancellationToken);
            if (WithinBudget()) await CheckAsync(start, plan, job, onEvent, cancellationToken, failed);
        }

        job.Status = job.Steps.All(step => step.State is JobStates.Checked or JobStates.Skipped) ? JobStatuses.Done : JobStatuses.Partial;
        await SaveJobAsync(start, plan, job, onEvent, cancellationToken);
        return await FinishJobTurnAsync(start, JobOutcome(job, start.Actions), cancellationToken);
    }

    // The thinking step: what's asked, what's possible for this person, the approach, and the plan.
    private async Task<AgentJob> DeliberateAsync(JobStart start, CancellationToken cancellationToken)
    {
        var request = start.Request;
        var areas = AreasFor(start.Services, request.User);
        var capabilities = string.Join("\n", areas.Select(area =>
            $"- {area.Area.Name}: " + string.Join("; ", area.Tools.Select(tool => $"{tool.Name} ({FirstSentence(tool.Description)})"))));
        var today = time.GetUtcNow().ToLocalTime().ToString("dddd dd-MM-yy", CultureInfo.InvariantCulture);
        var system = $$"""
            You are SentinelGPT, planning a job in GWS Business Suite for {{request.User.Username}} ({{request.User.RoleLabel}}). Today is {{today}}.
            Think the job through before anything is changed:
            1. What exactly is being asked, and what does "done" look like?
            2. What's known already (the screen, earlier results), and what still has to be looked up?
            3. What may this person do? Plan only with the capabilities below. "not_possible" lists only parts of the request that no capability below can do (for example "send a text message"); it's empty when every part can be done. Doubts go in the steps' checks, not there.
            4. Weigh the ways to do it and choose the simplest one that gets all of it done.
            5. Break it into sections of related steps, in the order they must happen. Each step is one thing done with one capability, with how to check it worked.
            Deleting, publishing, sending and running automations wait for the person's Confirm: plan them as steps anyway.
            Reply with JSON only, no other text:
            {"understanding": "...", "approach": "...", "not_possible": [], "sections": [{"title": "...", "steps": [{"do": "...", "check": "..."}]}]}
            At most {{MaxSections}} sections, {{MaxStepsPerSection}} steps a section and {{MaxJobSteps}} steps in all.

            Capabilities:
            {{capabilities}}
            """;
        var user = new StringBuilder(request.Message.Trim());
        if (!string.IsNullOrWhiteSpace(request.PageContext)) user.Append("\n\nOn the person's screen:\n").Append(Limit(request.PageContext));
        var done = start.Actions.Where(action => action.Tool != PlanToolName).ToList();
        if (done.Count > 0) user.Append("\n\nAlready done in this turn (they count as done: don't plan them, and don't list them as not possible):\n").AppendJoin("\n", done.Select(action => $"- {action.Summary}"));
        var found = start.Messages.Where(message => message.Role == "tool").TakeLast(6).Select(message => Limit(message.Content)).ToList();
        if (found.Count > 0) user.Append("\n\nWhat the tools have returned so far:\n").AppendJoin("\n", found);

        var messages = new List<OllamaChatMessage> { new("system", system), new("user", user.ToString()) };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reply = await request.Model.ChatAsync(request.ModelName, messages, null, cancellationToken);
            if (ParseJob(reply.Content, request.Message.Trim()) is { } job) return job;
            messages.Add(new OllamaChatMessage("assistant", reply.Content));
            messages.Add(new OllamaChatMessage("user", "Reply with the JSON only, in exactly the shape asked for."));
        }

        // No usable plan: one step, the request itself, so the job still runs and is checked.
        return new AgentJob
        {
            Request = request.Message.Trim(),
            Understanding = request.Message.Trim(),
            Sections = [new AgentJobSection { Title = "The request", Steps = [new AgentJobStep { Number = 1, Do = request.Message.Trim(), Check = "It's done as asked" }] }]
        };
    }

    // A leg: up to MaxRounds rounds on one section (Do), the failed steps (Repair) or the checks
    // (Check, read tools only). Returns the model's closing words, or null when the rounds ran out.
    private async Task<string?> RunLegAsync(
        JobStart start, SentinelAgentAction plan, AgentJob job, LegMode mode, int? sectionIndex,
        Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        var request = start.Request;
        var areas = AreasFor(start.Services, request.User);
        var offered = await OfferedAreasAsync(start.Db, request with { Message = job.Request + "\n" + RenderJob(job) }, areas, [], cancellationToken);
        var definitions = LegDefinitions(start.Tools, areas, offered, mode);
        // The leg's own instruction is the user message. In the first live run (05-11-26) each leg got
        // the original request instead, and the model redid the whole job every leg.
        var messages = new List<OllamaChatMessage>
        {
            new("system", await SystemPromptAsync(start.Db, request, cancellationToken) + "\n\n" + Brief(job, mode, sectionIndex, start.Actions)),
            new("user", LegInstruction(job, mode, sectionIndex))
        };
        // Every change this chat has made counts as done, so a leg can't make it again (that same run
        // re-created all five lists in each leg).
        var done = await DoneChangeKeysAsync(start, cancellationToken);
        for (var round = 0; round < Math.Max(1, request.MaxRounds); round++)
        {
            var response = await request.Model.ChatAsync(request.ModelName, messages, definitions, cancellationToken);
            if (response.ToolCalls.Count == 0) return response.Content.Trim();

            messages.Add(new OllamaChatMessage("assistant", response.Content, ToolCalls: response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                string result;
                if (mode != LegMode.Check && call.Name is CompleteStepToolName or SkipStepToolName)
                {
                    result = await MarkStepAsync(start, plan, job, call, onEvent, cancellationToken);
                }
                else if (call.Name == UseToolsName)
                {
                    result = UseTools(call, areas, offered);
                    definitions = LegDefinitions(start.Tools, areas, offered, mode);
                }
                else if (call.Name == DiscoverCapabilitiesToolName)
                {
                    result = DiscoverCapabilities(areas);
                }
                else if (call.Name is PlanJobToolName or PlanToolName)
                {
                    result = Problem("The job is already planned: carry on with its steps.");
                }
                else if (mode == LegMode.Check && start.Tools.GetValueOrDefault(call.Name)?.Risk != SentinelAgentRisk.Read)
                {
                    result = Problem("You're only checking now: look things up, don't change anything.");
                }
                else
                {
                    result = await CallAsync(start.Db, start.Tools, call, start.Context, start.Actions, done, onEvent, cancellationToken);
                }
                messages.Add(new OllamaChatMessage("tool", result, Name: call.Name));
            }

            // The section (or the repair) is finished: no need for more rounds.
            if (mode == LegMode.Do && sectionIndex is { } index && job.Sections[index].Steps.All(step => step.State != JobStates.Pending)) return null;
            if (mode == LegMode.Repair && job.Steps.All(step => step.State != JobStates.Failed)) return null;
        }
        return null;
    }

    private static string LegInstruction(AgentJob job, LegMode mode, int? sectionIndex)
    {
        if (mode == LegMode.Check) return "Check the finished steps now with the read tools, then reply with the JSON.";
        var steps = mode == LegMode.Repair
            ? job.Steps.Where(step => step.State == JobStates.Failed).ToList()
            : job.Sections[sectionIndex!.Value].Steps.Where(step => step.State == JobStates.Pending).ToList();
        var heading = mode == LegMode.Repair
            ? "Fix these steps now:"
            : $"Carry out section {sectionIndex + 1} of {job.Sections.Count} now, \"{job.Sections[sectionIndex!.Value].Title}\". Only these steps; everything else is done already:";
        return heading + "\n" + string.Join("\n", steps.Select(step => $"{step.Number}. {step.Do}" + (mode == LegMode.Repair ? $" (problem: {step.Note})" : string.Empty)))
            + "\nAfter each one, call complete_step with its number. Don't redo anything under \"Changed so far\".";
    }

    private static async Task<HashSet<string>> DoneChangeKeysAsync(JobStart start, CancellationToken cancellationToken)
    {
        var made = await start.Db.SentinelAgentActions.AsNoTracking()
            .Where(action => action.ConversationId == start.Context.ConversationId && action.CreatedBy == start.Context.User.Username
                && action.Status == SentinelAgentActionStatuses.Done && action.Tool != PlanToolName)
            .Select(action => new { action.Tool, action.ArgumentsJson })
            .ToListAsync(cancellationToken);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in made)
        {
            try
            {
                if (JsonNode.Parse(action.ArgumentsJson) is JsonObject arguments) keys.Add(ChangeKey(action.Tool, arguments));
            }
            catch (JsonException)
            {
                // An unreadable old row just isn't guarded.
            }
        }
        return keys;
    }

    // A change's identity: the tool and its arguments with keys in order, so the same change written
    // with its arguments in another order still counts as the same.
    private static string ChangeKey(string tool, JsonObject arguments) => tool + Canonical(arguments);

    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString()
    };

    // Checks the done steps (or just those given) against their checks; marks each checked or failed.
    private async Task CheckAsync(
        JobStart start, SentinelAgentAction plan, AgentJob job, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken,
        IReadOnlyCollection<int>? only = null)
    {
        var toCheck = job.Steps.Where(step => step.State == JobStates.Done && (only is null || only.Contains(step.Number))).ToList();
        if (toCheck.Count == 0) return;
        await onEvent(new SentinelAgentStep("Checking the work"));
        var reply = await RunLegAsync(start, plan, job, LegMode.Check, null, onEvent, cancellationToken);
        if (ParseProblems(reply) is not { } problems)
        {
            foreach (var step in toCheck) step.Note ??= "not checked";
            await SaveJobAsync(start, plan, job, onEvent, cancellationToken);
            return;
        }

        foreach (var step in toCheck)
        {
            if (problems.TryGetValue(step.Number, out var why))
            {
                step.State = JobStates.Failed;
                step.Note = why;
            }
            else
            {
                step.State = JobStates.Checked;
                step.Note = null;
            }
        }
        await SaveJobAsync(start, plan, job, onEvent, cancellationToken);
    }

    private async Task<string> MarkStepAsync(
        JobStart start, SentinelAgentAction plan, AgentJob job, OllamaToolCall call, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return Problem("The arguments weren't valid JSON.");
        }

        var number = StepNumber(arguments["step"]);
        var step = job.Steps.FirstOrDefault(item => item.Number == number);
        if (step is null) return Problem($"There's no step {arguments["step"]?.ToJsonString() ?? "(none)"} in the plan.");
        var skipping = call.Name == SkipStepToolName;
        var note = (skipping ? arguments["why"] : arguments["result"])?.GetValue<string>()?.Trim();
        step.State = skipping ? JobStates.Skipped : JobStates.Done;
        step.Note = string.IsNullOrWhiteSpace(note) ? null : note.Length > 200 ? note[..200] : note;
        await SaveJobAsync(start, plan, job, onEvent, cancellationToken);
        await onEvent(new SentinelAgentStep($"{(skipping ? "Skipped" : "Done")}: step {step.Number}, {step.Do}"));
        var left = job.Steps.Where(item => item.State is JobStates.Pending or JobStates.Failed).Select(item => item.Number).ToList();
        return SentinelAgentJson.Serialize(new { marked = step.Number, state = step.State, stepsLeft = left });
    }

    private async Task SaveJobAsync(JobStart start, SentinelAgentAction plan, AgentJob job, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        plan.ArgumentsJson = SaveJob(job);
        plan.Summary = RenderJob(job);
        plan.UpdatedAt = time.GetUtcNow();
        plan.UpdatedBy = start.Context.User.Username;
        await start.Db.SaveChangesAsync(cancellationToken);
        var view = View(plan, null);
        var index = start.Actions.FindIndex(action => action.Id == view.Id);
        if (index >= 0) start.Actions[index] = view;
        await onEvent(new SentinelAgentActed(view));
    }

    private async Task<SentinelAgentTurn> FinishJobTurnAsync(JobStart start, string answer, CancellationToken cancellationToken)
    {
        start.Db.SentinelAiRuns.Add(new SentinelAiRun
        {
            ConversationId = start.Request.ConversationId,
            Action = SentinelAiActions.Agent,
            Instruction = start.Request.Message.Trim(),
            Output = answer,
            Model = start.Request.ModelName,
            Status = SentinelAiRunStatuses.Completed,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = start.Request.User.Username
        });
        await start.Db.SaveChangesAsync(cancellationToken);
        return new SentinelAgentTurn(answer, start.Actions, start.Request.ModelName);
    }

    private static List<OllamaToolDefinition> LegDefinitions(
        IReadOnlyDictionary<string, ISentinelAgentTool> tools, IReadOnlyList<AgentArea> areas, IReadOnlySet<string> offered, LegMode mode)
    {
        var definitions = Definitions(tools, areas, offered).Where(definition => definition.Name is not (PlanToolName or PlanJobToolName)).ToList();
        if (mode == LegMode.Check)
            return definitions.Where(definition => definition.Name is UseToolsName or DiscoverCapabilitiesToolName
                || tools.GetValueOrDefault(definition.Name)?.Risk == SentinelAgentRisk.Read).ToList();
        definitions.Add(CompleteStepDefinition);
        definitions.Add(SkipStepDefinition);
        return definitions;
    }

    // What the leg is for, added to the usual system prompt.
    private static string Brief(AgentJob job, LegMode mode, int? sectionIndex, IReadOnlyList<SentinelAgentActionView> actions)
    {
        var brief = new StringBuilder();
        var changes = actions.Where(action => action.Tool != PlanToolName && action.Status == SentinelAgentActionStatuses.Done).ToList();
        if (mode == LegMode.Check)
        {
            brief.AppendLine("You're checking a finished job. Don't change anything. For each step below, use the read tools to look at what was made or changed, and compare it with its check:");
            foreach (var step in job.Steps.Where(step => step.State == JobStates.Done)) brief.AppendLine($"{step.Number}. {step.Do} (check: {step.Check})");
            if (changes.Count > 0) brief.AppendLine("Changes made:").AppendJoin("\n", changes.Select(action => $"- {action.Summary}")).AppendLine();
            brief.Append("""Then reply with JSON only: {"complete": true or false, "problems": [{"step": number, "why": "..."}]}. List every step that's wrong or missing; an empty list when all are right.""");
            return brief.ToString();
        }

        brief.AppendLine($"You're carrying out a planned job. As asked: {job.Request}");
        brief.AppendLine($"Goal: {job.Understanding}");
        if (job.Approach.Length > 0) brief.AppendLine($"Approach: {job.Approach}");
        brief.AppendLine("The plan (☐ to do, ☑ done, ✓ checked, ✗ didn't check out, – skipped):").AppendLine(RenderJob(job));
        if (changes.Count > 0) brief.AppendLine("Changed so far:").AppendJoin("\n", changes.TakeLast(WorkingSetSize).Select(action => $"- {action.Summary}")).AppendLine();
        if (mode == LegMode.Repair)
        {
            brief.AppendLine("These steps didn't check out:");
            foreach (var step in job.Steps.Where(step => step.State == JobStates.Failed)) brief.AppendLine($"{step.Number}. {step.Do}: {step.Note}");
            brief.Append("Fix each one with your tools, then call complete_step for it. Reply in one sentence when they're all fixed.");
        }
        else
        {
            var section = job.Sections[sectionIndex!.Value];
            brief.AppendLine($"Now: section {sectionIndex + 1} of {job.Sections.Count}, \"{section.Title}\". Do its ☐ steps in order with your tools; look things up first where a step needs an id.");
            brief.Append("After each step call complete_step with its number and what you did; if one can't be done call skip_step with why. When this section's steps are all marked, reply in one sentence.");
        }
        return brief.ToString();
    }

    private static string JobOutcome(AgentJob job, IReadOnlyList<SentinelAgentActionView> actions)
    {
        var steps = job.Steps.ToList();
        var waiting = actions.Count(action => action.Tool != PlanToolName && action.AwaitingConfirm);
        var tail = new StringBuilder();
        if (job.Cannot.Count > 0) tail.Append($" Not possible here: {string.Join("; ", job.Cannot.Select(text => text.TrimEnd('.')))}.");
        if (waiting > 0) tail.Append($" {Plural(waiting, "change")} {(waiting == 1 ? "waits" : "wait")} for your Confirm.");
        if (job.Status == JobStatuses.Done && steps.All(step => step.State == JobStates.Skipped))
            return "I didn't change anything: " + string.Join("; ", steps.Select(step => $"{step.Do} ({step.Note ?? "skipped"})").Take(4)) + "." + tail;
        if (job.Status == JobStatuses.Done)
        {
            var skipped = steps.Count(step => step.State == JobStates.Skipped);
            return $"Done and checked: {Plural(steps.Count - skipped, "step")} in {Plural(job.Sections.Count, "section")}" +
                (skipped > 0 ? $", {skipped} skipped" : string.Empty) + "." + tail;
        }

        var finished = steps.Count(step => step.State is JobStates.Checked or JobStates.Done or JobStates.Skipped);
        var notChecked = steps.Count(step => step.State == JobStates.Done);
        var left = steps.Where(step => step.State is JobStates.Pending or JobStates.Failed).ToList();
        var answer = new StringBuilder($"Partly done: {finished} of {steps.Count} steps.");
        if (notChecked > 0) answer.Append($" {notChecked} not checked yet.");
        if (left.Count > 0)
            answer.Append(" Still to do: ").Append(string.Join("; ", left.Take(4).Select(step => $"{step.Number}. {step.Do}"))).Append(left.Count > 4 ? $" and {left.Count - 4} more." : ".");
        answer.Append(" Say \"carry on\" and I'll pick up from there.").Append(tail);
        return answer.ToString();
    }

    // The plan card's lines: each section's title, then its steps with their state.
    public static string RenderJob(AgentJob job)
    {
        var lines = new List<string>();
        for (var index = 0; index < job.Sections.Count; index++)
        {
            var section = job.Sections[index];
            if (job.Sections.Count > 1) lines.Add($"Section {index + 1}: {section.Title}");
            lines.AddRange(section.Steps.Select(step =>
                $"{Mark(step.State)} {step.Number}. {step.Do}" + (string.IsNullOrWhiteSpace(step.Note) || step.State == JobStates.Checked ? string.Empty : $" ({step.Note})")));
        }
        return string.Join("\n", lines);
    }

    private static string Mark(string state) => state switch
    {
        JobStates.Done => "☑",
        JobStates.Checked => "✓",
        JobStates.Failed => "✗",
        JobStates.Skipped => "–",
        _ => "☐"
    };

    // The plan the model wrote, or null when it isn't usable.
    public static AgentJob? ParseJob(string? reply, string request)
    {
        if (JsonFrom(reply) is not JsonObject root) return null;
        var job = new AgentJob
        {
            Request = request,
            Understanding = Text(root["understanding"]) ?? request,
            Approach = Text(root["approach"]) ?? string.Empty,
            Cannot = ((root["not_possible"] ?? root["cannot"]) as JsonArray ?? []).Select(Text).OfType<string>().Where(text => text.Length > 0).Take(10).ToList()
        };
        var number = 0;
        foreach (var sectionNode in (root["sections"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxSections))
        {
            var section = new AgentJobSection { Title = Text(sectionNode["title"]) ?? $"Part {job.Sections.Count + 1}" };
            foreach (var stepNode in (sectionNode["steps"] as JsonArray ?? []).Take(MaxStepsPerSection))
            {
                if (number >= MaxJobSteps) break;
                var (todo, check) = stepNode is JsonObject step ? (Text(step["do"]), Text(step["check"])) : (Text(stepNode), null);
                if (string.IsNullOrWhiteSpace(todo)) continue;
                section.Steps.Add(new AgentJobStep { Number = ++number, Do = todo, Check = check ?? "It's done as described" });
            }
            if (section.Steps.Count > 0) job.Sections.Add(section);
        }
        return number == 0 ? null : job;
    }

    // The checker's answer as step -> why, an empty map when all is right, or null when unusable.
    public static Dictionary<int, string>? ParseProblems(string? reply)
    {
        if (JsonFrom(reply) is not JsonObject root) return null;
        var problems = new Dictionary<int, string>();
        foreach (var problem in (root["problems"] as JsonArray ?? []).OfType<JsonObject>())
            if (StepNumber(problem["step"]) is { } number) problems[number] = Text(problem["why"]) ?? "it didn't check out";
        var complete = root["complete"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        // "Not complete" with nothing named can't be acted on.
        return !complete && problems.Count == 0 ? null : problems;
    }

    public static AgentJob? LoadJob(SentinelAgentAction plan)
    {
        try
        {
            return (JsonNode.Parse(plan.ArgumentsJson) as JsonObject)?["job"] is JsonObject job ? job.Deserialize<AgentJob>(JobJson) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // "steps" keeps the plain list the plan card and older readers of a show_plan expect.
    private static string SaveJob(AgentJob job) => new JsonObject
    {
        ["steps"] = new JsonArray(job.Steps.Select(step => (JsonNode)JsonValue.Create(step.Do)!).ToArray()),
        ["job"] = JsonSerializer.SerializeToNode(job, JobJson)
    }.ToJsonString();

    private static JsonNode? JsonFrom(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonNode.Parse(reply[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static int? StepNumber(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<int>(out var number) => number,
        JsonValue value when value.TryGetValue<double>(out var real) => (int)real,
        JsonValue value when value.TryGetValue<string>(out var text) && int.TryParse(text.Trim().TrimStart('#'), out var parsed) => parsed,
        _ => null
    };

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end > 0 ? text[..end] : text.TrimEnd('.');
        return sentence.Length <= 90 ? sentence : sentence[..90] + "…";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
