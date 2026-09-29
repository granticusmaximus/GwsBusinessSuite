namespace GwsBusinessSuite.Application.Community;

// Workstream E, Phase 2 (activity feed) - populated by hooking into existing actions rather than
// a bolt-on separate tracking system, per the master plan's own framing. Concrete hook points
// wired up so far: WikiService.SavePageAsync (an explicit "Save changes", not every autosave
// tick - see its own comment) and CrmService.SaveContactAsync (new contact creation only, not
// every field edit - a clean, unambiguous single moment rather than instrumenting every CRM
// mutation path). Injected as an OPTIONAL dependency into those services (same convention as
// IAutomationTriggerService in CmsBuilderService / ICurrentUserAccessor in CrmService) so
// existing tests that construct them directly, without this new dependency, keep compiling and
// behaving identically - recording simply no-ops when unset.
public interface IActivityFeedService
{
    Task RecordAsync(string performedBy, string verb, string targetLabel, string targetUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityEventView>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default);
}
