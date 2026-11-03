using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

public sealed class FormSubmissionAgentTools(IFormSubmissionService forms) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    public SentinelAgentArea Area { get; } = new("forms", "website form submissions and their CRM links", ["form submission", "submission", "form inbox"]);
    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool("list_form_submissions", "List form submissions across the site.", SentinelAgentJson.Schema(), SentinelAgentRisk.Read, Roles,
            _ => "Listing form submissions", ListAsync),
        new SentinelAgentTool("read_form_submission", "Read one form submission and all submitted fields.", SentinelAgentJson.Schema(("submissionId", "string", "The submission id", true)), SentinelAgentRisk.Read, Roles,
            _ => "Reading a form submission", ReadAsync),
        new SentinelAgentTool("mark_form_submission_read", "Mark a form submission as read.", SentinelAgentJson.Schema(("submissionId", "string", "The submission id", true)), SentinelAgentRisk.Change, Roles,
            _ => "Marking a form submission read", MarkReadAsync),
        new SentinelAgentTool("link_form_submission_contact", "Link a form submission to an existing CRM contact.", SentinelAgentJson.Schema(("submissionId", "string", "The submission id", true), ("contactId", "string", "The CRM contact id", true)), SentinelAgentRisk.Change, Roles,
            _ => "Linking a form submission to a contact", LinkAsync),
        new SentinelAgentTool("delete_form_submission", "Permanently delete a form submission. Nothing happens until Confirm.", SentinelAgentJson.Schema(("submissionId", "string", "The submission id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to delete a form submission", DeleteAsync,
            preview: async (args, _, ct) => { var row = await GetAsync(args.Id("submissionId"), ct); return $"Permanently delete the form submission from {row.FullName ?? row.Email ?? row.Id.ToString()}"; })
    ];

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct) => SentinelAgentToolResult.Of(new
    {
        submissions = (await forms.ListAllAsync(ct)).Take(100).Select(row => new { row.Id, row.PageId, row.FullName, row.Email, row.Company, row.ContactId, row.IsRead, row.CreatedAt })
    });
    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var row = await GetAsync(args.Id("submissionId"), ct);
        return SentinelAgentToolResult.Of(new { row.Id, row.PageId, row.FullName, row.Email, row.Company, row.Phone, row.ContactId, row.IsRead, fields = JsonNode.Parse(row.FieldsJson), row.CreatedAt });
    }
    private async Task<SentinelAgentToolResult> MarkReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var row = await GetAsync(args.Id("submissionId"), ct); await forms.MarkReadAsync(row.Id, ct);
        return SentinelAgentToolResult.Of(new { markedRead = row.Id }, new SentinelAgentChange("Marked form submission read", "form-submission", row.Id.ToString(), row.FullName ?? row.Email ?? "Submission", $"/admin/form-submissions/{row.Id}", null));
    }
    private async Task<SentinelAgentToolResult> LinkAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var row = await GetAsync(args.Id("submissionId"), ct); var contactId = args.Id("contactId"); await forms.LinkToContactAsync(row.Id, contactId, ct);
        return SentinelAgentToolResult.Of(new { row.Id, contactId }, new SentinelAgentChange("Linked form submission to CRM contact", "form-submission", row.Id.ToString(), row.FullName ?? row.Email ?? "Submission", $"/admin/form-submissions/{row.Id}", null));
    }
    private async Task<SentinelAgentToolResult> DeleteAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var row = await GetAsync(args.Id("submissionId"), ct); await forms.DeleteAsync(row.Id, ct);
        return SentinelAgentToolResult.Of(new { deleted = row.Id }, new SentinelAgentChange("Deleted form submission", "form-submission", row.Id.ToString(), row.FullName ?? row.Email ?? "Submission", "/admin/form-submissions", null));
    }
    private async Task<FormSubmission> GetAsync(Guid id, CancellationToken ct) => await forms.GetAsync(id, ct) ?? throw new SentinelAgentToolException($"There's no form submission with id {id}.");
}
