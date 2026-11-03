using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Billing;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

public sealed class BillingAgentTools(IBillingService billing) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    public SentinelAgentArea Area { get; } = new("billing", "invoices, draft line items and payment status", ["invoice", "billing", "payment", "paid", "overdue"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool("list_invoices", "List invoices and their current status, client, due date and total.", SentinelAgentJson.Schema(), SentinelAgentRisk.Read, Roles,
            _ => "Listing invoices", ListAsync),
        new SentinelAgentTool("read_invoice", "Read an invoice including its line items.", SentinelAgentJson.Schema(("invoiceId", "string", "The invoice id", true)), SentinelAgentRisk.Read, Roles,
            _ => "Reading an invoice", ReadAsync),
        new SentinelAgentTool("create_invoice_draft", "Create an invoice draft. Line items is JSON: [{description,quantity,unitPrice}].", Schema(), SentinelAgentRisk.Change, Roles,
            args => $"Creating invoice draft '{args.OptionalText("title")}'", CreateAsync, UndoCreateAsync),
        new SentinelAgentTool("update_invoice_draft", "Replace an invoice draft's editable fields and line items. Read it first; line items is JSON: [{description,quantity,unitPrice}].", UpdateSchema(), SentinelAgentRisk.Change, Roles,
            args => $"Updating invoice draft '{args.OptionalText("title")}'", UpdateAsync, UndoUpdateAsync),
        new SentinelAgentTool("delete_invoice_draft", "Permanently delete a draft invoice. Nothing happens until Confirm.", SentinelAgentJson.Schema(("invoiceId", "string", "The draft invoice id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to delete an invoice draft", DeleteAsync,
            preview: async (args, _, ct) => $"Permanently delete the draft invoice '{(await InvoiceAsync(args.Id("invoiceId"), ct)).Title}'"),
        new SentinelAgentTool("send_invoice", "Send a draft invoice through Stripe. This has an external financial effect and waits for Confirm.", SentinelAgentJson.Schema(("invoiceId", "string", "The draft invoice id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to send an invoice", SendAsync,
            preview: async (args, _, ct) => { var invoice = await InvoiceAsync(args.Id("invoiceId"), ct); return $"Send invoice '{invoice.Title}' for {invoice.TotalText} to {invoice.ContactName}"; }),
        new SentinelAgentTool("void_invoice", "Void a sent invoice in Stripe. Nothing happens until Confirm.", SentinelAgentJson.Schema(("invoiceId", "string", "The sent invoice id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to void an invoice", VoidAsync,
            preview: async (args, _, ct) => $"Void the sent invoice '{(await InvoiceAsync(args.Id("invoiceId"), ct)).Title}'")
    ];

    private static string Schema() => SentinelAgentJson.Schema(("contactId", "string", "The CRM contact id", true), ("dealId", "string", "Optional CRM deal id", false),
        ("title", "string", "Invoice title", true), ("currency", "string", "Currency code such as usd", false), ("dueDate", "string", "Optional date as YYYY-MM-DD", false),
        ("notes", "string", "Optional notes", false), ("lineItems", "string", "JSON array of line items", true));
    private static string UpdateSchema() => SentinelAgentJson.Schema(("invoiceId", "string", "The invoice id", true), ("contactId", "string", "The CRM contact id", true),
        ("dealId", "string", "Optional CRM deal id", false), ("title", "string", "Invoice title", true), ("currency", "string", "Currency code", false),
        ("dueDate", "string", "Optional date as YYYY-MM-DD", false), ("notes", "string", "Optional notes", false), ("lineItems", "string", "JSON array of line items", true));

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct) => SentinelAgentToolResult.Of(new
    {
        invoices = (await billing.ListInvoicesAsync(ct)).Select(i => new { i.Id, i.Title, i.ContactName, i.Status, i.Currency, i.TotalText, i.DueDate })
    });
    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct) =>
        SentinelAgentToolResult.Of(await InvoiceAsync(args.Id("invoiceId"), ct));

    private async Task<SentinelAgentToolResult> CreateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var saved = await billing.SaveDraftAsync(Editor(args), context.User.Username, ct);
        return SentinelAgentToolResult.Of(saved, Change($"Created invoice draft '{saved.Title}'", saved, SentinelAgentJson.Serialize(new { saved.Id })));
    }
    private async Task UndoCreateAsync(string json, SentinelAgentToolContext context, CancellationToken ct) =>
        await billing.DeleteDraftAsync(SentinelAgentJson.Deserialize<IdUndo>(json).Id, context.User.Username, ct);

    private async Task<SentinelAgentToolResult> UpdateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var before = await InvoiceAsync(args.Id("invoiceId"), ct);
        var editor = Editor(args); editor.Id = before.Id;
        var saved = await billing.SaveDraftAsync(editor, context.User.Username, ct);
        return SentinelAgentToolResult.Of(saved, Change($"Updated invoice draft '{saved.Title}'", saved, SentinelAgentJson.Serialize(Snapshot(before))));
    }
    private async Task UndoUpdateAsync(string json, SentinelAgentToolContext context, CancellationToken ct) =>
        _ = await billing.SaveDraftAsync(SentinelAgentJson.Deserialize<InvoiceEditorModel>(json), context.User.Username, ct);
    private async Task<SentinelAgentToolResult> DeleteAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var invoice = await InvoiceAsync(args.Id("invoiceId"), ct); await billing.DeleteDraftAsync(invoice.Id, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { deleted = invoice.Title }, new SentinelAgentChange($"Deleted invoice draft '{invoice.Title}'", "invoice", invoice.Id.ToString(), invoice.Title, "/admin/billing", null));
    }
    private async Task<SentinelAgentToolResult> SendAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var invoice = await billing.SendInvoiceAsync(args.Id("invoiceId"), context.User.Username, ct);
        return SentinelAgentToolResult.Of(invoice, new SentinelAgentChange($"Sent invoice '{invoice.Title}'", "invoice", invoice.Id.ToString(), invoice.Title, "/admin/billing", null));
    }
    private async Task<SentinelAgentToolResult> VoidAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var invoice = await billing.VoidInvoiceAsync(args.Id("invoiceId"), context.User.Username, ct);
        return SentinelAgentToolResult.Of(invoice, new SentinelAgentChange($"Voided invoice '{invoice.Title}'", "invoice", invoice.Id.ToString(), invoice.Title, "/admin/billing", null));
    }

    private async Task<InvoiceView> InvoiceAsync(Guid id, CancellationToken ct) => await billing.GetInvoiceAsync(id, ct) ?? throw new SentinelAgentToolException($"There's no invoice with id {id}.");
    private static InvoiceEditorModel Editor(JsonObject args) => new()
    {
        ContactId = args.Id("contactId"), DealId = args.OptionalId("dealId"), Title = args.Text("title"), Currency = args.OptionalText("currency") ?? "usd",
        DueDate = args.OptionalDate("dueDate"), Notes = args.OptionalText("notes") ?? string.Empty, LineItems = ParseLines(args.Text("lineItems"))
    };
    private static List<InvoiceLineItemEditorModel> ParseLines(string json)
    {
        try
        {
            return (JsonNode.Parse(json) as JsonArray ?? throw new Exception()).Select(node => node as JsonObject ?? throw new Exception()).Select(item => new InvoiceLineItemEditorModel
            {
                Description = item["description"]?.GetValue<string>()?.Trim() ?? string.Empty,
                Quantity = item["quantity"]?.GetValue<int>() ?? 1,
                UnitPriceUsd = item["unitPrice"]?.GetValue<decimal>() ?? 0
            }).ToList();
        }
        catch { throw new SentinelAgentToolException("'lineItems' must be JSON like [{\"description\":\"Work\",\"quantity\":1,\"unitPrice\":100}]."); }
    }
    private static InvoiceEditorModel Snapshot(InvoiceView invoice) => new()
    {
        Id = invoice.Id, ContactId = invoice.ContactId, DealId = invoice.DealId, Title = invoice.Title, DueDate = invoice.DueDate, Notes = invoice.Notes,
        Currency = invoice.Currency, AutoRemind = invoice.AutoRemind,
        LineItems = invoice.LineItems.Select(line => new InvoiceLineItemEditorModel { Id = line.Id, Description = line.Description, Quantity = line.Quantity, UnitPriceUsd = line.UnitPriceUsd }).ToList()
    };
    private static SentinelAgentChange Change(string summary, InvoiceView invoice, string undo) => new(summary, "invoice", invoice.Id.ToString(), invoice.Title, "/admin/billing", undo);
    private sealed record IdUndo(Guid Id);
}
