using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services;

// Public forms (SentinelPublicForms holds the rules). Submissions are saved through
// WikiDatabaseService.SaveRowAsync like any other new row, so property validation, limits and
// row-added automations all apply.
public sealed class SentinelPublicFormService(
    ISentinelAccessService accessService,
    IWikiDatabaseService databaseService) : ISentinelPublicFormService
{
    public async Task<SentinelPublicFormView?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        var share = await accessService.ResolvePublicShareAsync(token, cancellationToken);
        if (share is not { IsDatabase: true, FormViewId: { } viewId }) return null;
        var database = await databaseService.GetDatabaseAsync(share.TargetId, cancellationToken);
        var view = database?.Views.FirstOrDefault(item => item.Id == viewId && item.Type == WikiDatabaseViewTypes.Form);
        if (database is null || view is null) return null;
        var settings = WikiDatabaseViewConfigJson.Parse(view.ConfigJson).FormSettings ?? new WikiDatabaseFormSettings();
        return new SentinelPublicFormView(share.Id, database, view, settings);
    }

    public async Task<SentinelPublicFormResult> SubmitAsync(string token, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, CancellationToken cancellationToken = default)
    {
        var form = await GetAsync(token, cancellationToken);
        if (form is null) return SentinelPublicFormResult.Failed("This form is no longer available.");
        var (values, errors) = SentinelPublicForm.BuildRow(form.Database, form.Settings, answers);
        if (errors.Count > 0) return new SentinelPublicFormResult(false, errors);
        try
        {
            await databaseService.SaveRowAsync(form.Database.Id,
                new WikiDatabaseRowEditor { Values = values.ToDictionary(item => item.Key, item => item.Value) },
                SentinelPublicForm.SubmittedBy, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // Property validation (a Required property the form doesn't ask, a pattern, a min/max)
            // or the database's row limit - all phrased for a person to read.
            return SentinelPublicFormResult.Failed(exception.Message);
        }
        await accessService.RecordShareViewAsync(form.ShareId, cancellationToken);
        return new SentinelPublicFormResult(true, []);
    }
}
