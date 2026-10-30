using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// The CRM for the agent (Phase C, 30-10-26), through ICrmService like the CRM pages, so every save
// rule and the deal-stage automation trigger still apply. The CRM pages are AdminOnly, so these are
// too. A company is the Company field on a contact; there's no separate company record.
public sealed class CrmAgentTools(ICrmService crm, IAppDbContext db) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int ContactsShown = 20;
    private const int NotesShown = 10;
    private const int DealsShown = 30;

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "find_contacts",
            "Find CRM contacts by name, email or company (leave the search out for the most recent). Gives ids, status, follow-up date and open deals.",
            SentinelAgentJson.Schema(("search", "string", "Words in the name, email or company", false)),
            SentinelAgentRisk.Read, Roles,
            args => args.OptionalText("search") is { } search ? $"Looking for contacts matching '{search}'" : "Looking at the contacts",
            FindContactsAsync),
        new SentinelAgentTool(
            "read_contact",
            "Read one CRM contact: details, the latest notes and their deals (with ids).",
            SentinelAgentJson.Schema(("contactId", "string", "The contact id", true)),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading a contact",
            ReadContactAsync),
        new SentinelAgentTool(
            "create_contact",
            "Add a CRM contact. Status: Lead (the default), Prospect, Customer or Inactive. Find first: an email already in the CRM isn't added twice.",
            SentinelAgentJson.Schema(
                ("fullName", "string", "The person's name", true),
                ("email", "string", "Their email", false),
                ("company", "string", "Their company", false),
                ("status", "string", "Lead, Prospect, Customer or Inactive", false),
                ("followUpDate", "string", "When to follow up, as 2026-11-04", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Adding the contact '{args.OptionalText("fullName")}'",
            CreateContactAsync,
            UndoCreateContactAsync),
        new SentinelAgentTool(
            "update_contact",
            "Change a CRM contact's name, email, company, status or follow-up date. Leave out what stays the same; followUpDate 'none' clears it.",
            SentinelAgentJson.Schema(
                ("contactId", "string", "The contact id", true),
                ("fullName", "string", "The new name", false),
                ("email", "string", "The new email", false),
                ("company", "string", "The new company", false),
                ("status", "string", "Lead, Prospect, Customer or Inactive", false),
                ("followUpDate", "string", "When to follow up, as 2026-11-04, or none", false)),
            SentinelAgentRisk.Change, Roles,
            _ => "Updating a contact",
            UpdateContactAsync,
            UndoUpdateContactAsync),
        new SentinelAgentTool(
            "add_contact_note",
            "Add a note to a CRM contact's activity log: a call, a meeting, what was agreed.",
            SentinelAgentJson.Schema(
                ("contactId", "string", "The contact id", true),
                ("note", "string", "The note", true)),
            SentinelAgentRisk.Change, Roles,
            _ => "Adding a note to a contact",
            AddNoteAsync,
            UndoAddNoteAsync),
        new SentinelAgentTool(
            "trash_contact",
            "Move a CRM contact to the trash. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(("contactId", "string", "The contact id", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to trash a contact",
            TrashContactAsync,
            UndoTrashContactAsync,
            async (args, _, ct) => $"Move the CRM contact '{(await ContactAsync(args.Id("contactId"), ct)).FullName}' to the trash"),
        new SentinelAgentTool(
            "find_deals",
            "List CRM deals, optionally only one stage (Lead, Qualified, ProposalSent, Negotiation, Won, Lost) or one contact's. Gives ids, value and stage.",
            SentinelAgentJson.Schema(
                ("stage", "string", "Only deals in this stage", false),
                ("contactId", "string", "Only this contact's deals", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at the deals",
            FindDealsAsync),
        new SentinelAgentTool(
            "create_deal",
            "Add a deal for a CRM contact. Stage: Lead (the default), Qualified, ProposalSent, Negotiation, Won or Lost.",
            SentinelAgentJson.Schema(
                ("contactId", "string", "Whose deal it is: the contact id", true),
                ("title", "string", "What the deal is", true),
                ("valueUsd", "number", "What it's worth in US dollars", false),
                ("stage", "string", "The pipeline stage", false),
                ("expectedCloseDate", "string", "When it should close, as 2026-11-04", false),
                ("notes", "string", "Notes on the deal", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Adding the deal '{args.OptionalText("title")}'",
            CreateDealAsync,
            UndoCreateDealAsync),
        new SentinelAgentTool(
            "update_deal",
            "Change a deal's title, value, expected close date or notes. Leave out what stays the same. To move it along the pipeline use set_deal_stage.",
            SentinelAgentJson.Schema(
                ("dealId", "string", "The deal id", true),
                ("title", "string", "The new title", false),
                ("valueUsd", "number", "The new value in US dollars", false),
                ("expectedCloseDate", "string", "When it should close, as 2026-11-04, or none", false),
                ("notes", "string", "The deal's notes (replaces them)", false)),
            SentinelAgentRisk.Change, Roles,
            _ => "Updating a deal",
            UpdateDealAsync,
            UndoUpdateDealAsync),
        new SentinelAgentTool(
            "set_deal_stage",
            "Move a deal to another pipeline stage: Lead, Qualified, ProposalSent, Negotiation, Won or Lost.",
            SentinelAgentJson.Schema(
                ("dealId", "string", "The deal id", true),
                ("stage", "string", "The stage to move it to", true)),
            SentinelAgentRisk.Change, Roles,
            args => $"Moving a deal to {args.OptionalText("stage")}",
            SetStageAsync,
            UndoSetStageAsync)
    ];

    public static string LinkTo(Guid contactId) => $"/admin/crm/contacts/{contactId}";

    private async Task<SentinelAgentToolResult> FindContactsAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var words = (args.OptionalText("search") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contacts = (await crm.ListContactsAsync(cancellationToken: ct))
            .Where(contact => words.All(word =>
                contact.FullName.Contains(word, StringComparison.OrdinalIgnoreCase)
                || contact.Email?.Contains(word, StringComparison.OrdinalIgnoreCase) == true
                || contact.Company?.Contains(word, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
        var openDeals = (await crm.ListDealsAsync(ct))
            .Where(deal => DealStages.Open.Contains(deal.Stage))
            .GroupBy(deal => deal.ContactId)
            .ToDictionary(group => group.Key, group => group.Count());
        return SentinelAgentToolResult.Of(new
        {
            found = contacts.Count,
            contacts = contacts.Take(ContactsShown).Select(contact => new
            {
                id = contact.Id,
                name = contact.FullName,
                email = contact.Email,
                company = contact.Company,
                status = contact.Status,
                followUp = Day(contact.FollowUpDate),
                openDeals = openDeals.GetValueOrDefault(contact.Id)
            })
        });
    }

    private async Task<SentinelAgentToolResult> ReadContactAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        var notes = await crm.ListActivitiesAsync(contact.Id, ct);
        var deals = await crm.ListDealsForContactAsync(contact.Id, ct);
        return SentinelAgentToolResult.Of(new
        {
            id = contact.Id,
            name = contact.FullName,
            email = contact.Email,
            company = contact.Company,
            status = contact.Status,
            followUp = Day(contact.FollowUpDate),
            notes = notes.OrderByDescending(note => note.CreatedAt).Take(NotesShown)
                .Select(note => new { on = Day(note.CreatedAt), by = note.CreatedBy, note = note.Note }),
            deals = deals.Select(DealForModel)
        });
    }

    private async Task<SentinelAgentToolResult> CreateContactAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var email = args.OptionalText("email");
        if (email is not null && (await crm.ListContactsAsync(cancellationToken: ct))
                .FirstOrDefault(contact => string.Equals(contact.Email, email, StringComparison.OrdinalIgnoreCase)) is { } existing)
            throw new SentinelAgentToolException($"{email} is already in the CRM as '{existing.FullName}' (contact id {existing.Id}). Use that contact.");

        var contact = await crm.SaveContactAsync(new ContactEditorModel
        {
            FullName = args.Text("fullName"),
            Email = email,
            Company = args.OptionalText("company"),
            Status = Status(args.OptionalText("status")) ?? ContactStatuses.Lead,
            FollowUpDate = args.OptionalDate("followUpDate")
        }, ct);
        return SentinelAgentToolResult.Of(
            new { created = true, contactId = contact.Id, name = contact.FullName, status = contact.Status },
            new SentinelAgentChange($"Added the contact '{contact.FullName}'", "contact", contact.Id.ToString(), contact.FullName, LinkTo(contact.Id),
                SentinelAgentJson.Serialize(new ContactUndo(contact.Id))));
    }

    private Task UndoCreateContactAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        crm.TrashContactAsync(SentinelAgentJson.Deserialize<ContactUndo>(undoJson).ContactId, ct);

    private async Task<SentinelAgentToolResult> UpdateContactAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        var before = ContactFields.Of(contact);
        var after = before with
        {
            FullName = args.OptionalText("fullName") ?? before.FullName,
            Email = args.OptionalText("email") ?? before.Email,
            Company = args.OptionalText("company") ?? before.Company,
            Status = Status(args.OptionalText("status")) ?? before.Status,
            FollowUpDate = Cleared(args, "followUpDate") ? null : args.OptionalDate("followUpDate") ?? before.FollowUpDate
        };
        if (after == before) throw new SentinelAgentToolException($"Nothing to change: give the new values for '{contact.FullName}'.");

        var saved = ContactFields.Of(await crm.SaveContactAsync(after.Editor(contact.Id), ct));
        return SentinelAgentToolResult.Of(
            new { updated = true, contactId = contact.Id, name = saved.FullName, email = saved.Email, company = saved.Company, status = saved.Status, followUp = Day(saved.FollowUpDate) },
            new SentinelAgentChange($"Updated the contact '{saved.FullName}' ({Changed(before, saved)})", "contact", contact.Id.ToString(), saved.FullName, LinkTo(contact.Id),
                SentinelAgentJson.Serialize(new ContactEditUndo(contact.Id, before, saved))));
    }

    private async Task UndoUpdateContactAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<ContactEditUndo>(undoJson);
        var contact = await ContactAsync(undo.ContactId, ct);
        if (ContactFields.Of(contact) != undo.After)
            throw new SentinelAgentToolException($"'{contact.FullName}' has changed since, so undoing would lose that. Edit the contact instead.");
        await crm.SaveContactAsync(undo.Before.Editor(contact.Id), ct);
    }

    private async Task<SentinelAgentToolResult> AddNoteAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        var note = await crm.AddActivityAsync(contact.Id, args.Text("note"), ct);
        return SentinelAgentToolResult.Of(
            new { added = true, contact = contact.FullName, note = note.Note },
            new SentinelAgentChange($"Added a note to '{contact.FullName}'", "contact-note", note.Id.ToString(), contact.FullName, LinkTo(contact.Id),
                SentinelAgentJson.Serialize(new NoteUndo(note.Id))));
    }

    // The activity log is append-only for people; this only takes back the agent's own note.
    private async Task UndoAddNoteAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<NoteUndo>(undoJson);
        var note = await db.ContactActivities.FirstOrDefaultAsync(activity => activity.Id == undo.NoteId, ct)
            ?? throw new SentinelAgentToolException("That note is already gone.");
        db.ContactActivities.Remove(note);
        await db.SaveChangesAsync(ct);
    }

    private async Task<SentinelAgentToolResult> TrashContactAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        await crm.TrashContactAsync(contact.Id, ct);
        return SentinelAgentToolResult.Of(new { trashed = contact.FullName },
            new SentinelAgentChange($"Moved the contact '{contact.FullName}' to the trash", "contact", contact.Id.ToString(), contact.FullName, "/admin/crm",
                SentinelAgentJson.Serialize(new ContactUndo(contact.Id))));
    }

    private Task UndoTrashContactAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        crm.RestoreContactAsync(SentinelAgentJson.Deserialize<ContactUndo>(undoJson).ContactId, ct);

    private async Task<SentinelAgentToolResult> FindDealsAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var stage = args.OptionalText("stage") is { } named ? Stage(named) : null;
        var contactId = args.OptionalId("contactId");
        var deals = (contactId is { } id ? await crm.ListDealsForContactAsync(id, ct) : await crm.ListDealsAsync(ct))
            .Where(deal => stage is null || deal.Stage == stage)
            .ToList();
        return SentinelAgentToolResult.Of(new
        {
            found = deals.Count,
            totalUsd = deals.Sum(deal => deal.ValueUsd),
            deals = deals.Take(DealsShown).Select(DealForModel)
        });
    }

    private async Task<SentinelAgentToolResult> CreateDealAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        var deal = await crm.SaveDealAsync(new DealEditorModel
        {
            ContactId = contact.Id,
            Title = args.Text("title"),
            ValueUsd = args.OptionalNumber("valueUsd") ?? 0,
            Stage = args.OptionalText("stage") is { } stage ? Stage(stage) : DealStages.Lead,
            ExpectedCloseDate = args.OptionalDate("expectedCloseDate"),
            Notes = args.OptionalText("notes") ?? string.Empty
        }, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { created = true, dealId = deal.Id, deal.Title, contact = deal.ContactName, stage = deal.Stage, valueUsd = deal.ValueUsd },
            new SentinelAgentChange($"Added the deal '{deal.Title}' for '{deal.ContactName}' ({deal.Stage}, {Money(deal.ValueUsd)})", "deal", deal.Id.ToString(), deal.Title, LinkTo(deal.ContactId),
                SentinelAgentJson.Serialize(new DealUndo(deal.Id))));
    }

    private Task UndoCreateDealAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        crm.DeleteDealAsync(SentinelAgentJson.Deserialize<DealUndo>(undoJson).DealId, ct);

    private async Task<SentinelAgentToolResult> UpdateDealAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var deal = await DealAsync(args.Id("dealId"), ct);
        var before = DealFields.Of(deal);
        var after = before with
        {
            Title = args.OptionalText("title") ?? before.Title,
            ValueUsd = args.OptionalNumber("valueUsd") ?? before.ValueUsd,
            ExpectedCloseDate = Cleared(args, "expectedCloseDate") ? null : args.OptionalDate("expectedCloseDate") ?? before.ExpectedCloseDate,
            Notes = args.OptionalText("notes") ?? before.Notes
        };
        if (after == before) throw new SentinelAgentToolException($"Nothing to change: give the new values for '{deal.Title}'.");

        var saved = await crm.SaveDealAsync(after.Editor(deal), context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { updated = true, dealId = saved.Id, saved.Title, valueUsd = saved.ValueUsd, expectedClose = Day(saved.ExpectedCloseDate) },
            new SentinelAgentChange($"Updated the deal '{saved.Title}'", "deal", saved.Id.ToString(), saved.Title, LinkTo(saved.ContactId),
                SentinelAgentJson.Serialize(new DealEditUndo(saved.Id, before, DealFields.Of(saved)))));
    }

    private async Task UndoUpdateDealAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<DealEditUndo>(undoJson);
        var deal = await DealAsync(undo.DealId, ct);
        if (DealFields.Of(deal) != undo.After)
            throw new SentinelAgentToolException($"'{deal.Title}' has changed since, so undoing would lose that. Edit the deal instead.");
        await crm.SaveDealAsync(undo.Before.Editor(deal), context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> SetStageAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var deal = await DealAsync(args.Id("dealId"), ct);
        var stage = Stage(args.Text("stage"));
        if (deal.Stage == stage) throw new SentinelAgentToolException($"'{deal.Title}' is already at {stage}.");
        var moved = await crm.SetDealStageAsync(deal.Id, stage, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { moved = moved.Title, from = deal.Stage, to = moved.Stage },
            new SentinelAgentChange($"Moved the deal '{moved.Title}' from {deal.Stage} to {moved.Stage}", "deal", moved.Id.ToString(), moved.Title, LinkTo(moved.ContactId),
                SentinelAgentJson.Serialize(new StageUndo(moved.Id, deal.Stage, moved.Stage))));
    }

    private async Task UndoSetStageAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<StageUndo>(undoJson);
        var deal = await DealAsync(undo.DealId, ct);
        if (deal.Stage != undo.Stage)
            throw new SentinelAgentToolException($"'{deal.Title}' has moved to {deal.Stage} since, so it's left there.");
        await crm.SetDealStageAsync(deal.Id, undo.PreviousStage, context.User.Username, ct);
    }

    private async Task<Contact> ContactAsync(Guid contactId, CancellationToken ct) =>
        await crm.GetContactAsync(contactId, ct)
            ?? throw new SentinelAgentToolException($"There's no contact with id {contactId}. Find the contact first.");

    private async Task<Deal> DealAsync(Guid dealId, CancellationToken ct) =>
        await db.Deals.AsNoTracking().FirstOrDefaultAsync(deal => deal.Id == dealId, ct)
            ?? throw new SentinelAgentToolException($"There's no deal with id {dealId}. Find the deals first.");

    private static object DealForModel(DealView deal) => new
    {
        id = deal.Id,
        title = deal.Title,
        contact = deal.ContactName,
        contactId = deal.ContactId,
        stage = deal.Stage,
        valueUsd = deal.ValueUsd,
        expectedClose = Day(deal.ExpectedCloseDate)
    };

    // "proposal sent" -> ProposalSent.
    private static string Stage(string name) =>
        DealStages.All.FirstOrDefault(stage => string.Equals(stage, name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase))
        ?? throw new SentinelAgentToolException($"'{name}' isn't a deal stage. The stages are: {string.Join(", ", DealStages.All)}.");

    private static string? Status(string? name) =>
        name is null
            ? null
            : ContactStatuses.All.FirstOrDefault(status => string.Equals(status, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new SentinelAgentToolException($"'{name}' isn't a contact status. They are: {string.Join(", ", ContactStatuses.All)}.");

    private static bool Cleared(JsonObject args, string name) => args.OptionalText(name)?.ToLowerInvariant() is "none" or "clear" or "null";

    private static string? Day(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd");

    private static string Money(decimal value) => value.ToString("$#,0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Changed(ContactFields before, ContactFields after)
    {
        var changed = new List<string>();
        if (before.FullName != after.FullName) changed.Add("name");
        if (before.Email != after.Email) changed.Add("email");
        if (before.Company != after.Company) changed.Add("company");
        if (before.Status != after.Status) changed.Add($"status {after.Status}");
        if (before.FollowUpDate != after.FollowUpDate) changed.Add(after.FollowUpDate is { } date ? $"follow up {date:dd-MM-yy}" : "no follow-up");
        return string.Join(", ", changed);
    }

    private sealed record ContactFields(string FullName, string? Email, string? Company, string Status, DateTimeOffset? FollowUpDate)
    {
        // Follow-ups are stored as UTC midnight; comparing the day keeps "changed since" honest.
        public static ContactFields Of(Contact contact) =>
            new(contact.FullName, contact.Email, contact.Company, contact.Status, contact.FollowUpDate is { } date ? new DateTimeOffset(date.UtcDateTime.Date, TimeSpan.Zero) : null);

        public ContactEditorModel Editor(Guid contactId) =>
            new() { ContactId = contactId, FullName = FullName, Email = Email, Company = Company, Status = Status, FollowUpDate = FollowUpDate };
    }

    private sealed record DealFields(string Title, decimal ValueUsd, DateTimeOffset? ExpectedCloseDate, string Notes)
    {
        public static DealFields Of(Deal deal) => new(deal.Title, deal.ValueUsd, Midnight(deal.ExpectedCloseDate), deal.Notes);

        public static DealFields Of(DealView deal) => new(deal.Title, deal.ValueUsd, Midnight(deal.ExpectedCloseDate), deal.Notes);

        // Stage stays what it is now: undoing an edit never moves the deal.
        public DealEditorModel Editor(Deal deal) =>
            new() { DealId = deal.Id, ContactId = deal.ContactId, Title = Title, Stage = deal.Stage, ValueUsd = ValueUsd, ExpectedCloseDate = ExpectedCloseDate, Notes = Notes };

        private static DateTimeOffset? Midnight(DateTimeOffset? date) => date is { } day ? new DateTimeOffset(day.UtcDateTime.Date, TimeSpan.Zero) : null;
    }

    private sealed record ContactUndo(Guid ContactId);

    private sealed record ContactEditUndo(Guid ContactId, ContactFields Before, ContactFields After);

    private sealed record NoteUndo(Guid NoteId);

    private sealed record DealUndo(Guid DealId);

    private sealed record DealEditUndo(Guid DealId, DealFields Before, DealFields After);

    private sealed record StageUndo(Guid DealId, string PreviousStage, string Stage);
}
