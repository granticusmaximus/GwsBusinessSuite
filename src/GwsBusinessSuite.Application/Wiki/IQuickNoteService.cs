using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Backs the dashboard's "Quick Note" mini-modal: every note becomes a real Sentinel page
// nested under a single well-known "Quick Notes" folder page, which itself is kept as a
// live, clickable index of every note title - see QuickNoteService for how.
public interface IQuickNoteService
{
    Task<WikiPage> AddQuickNoteAsync(string title, string markdownBody, string performedBy, CancellationToken cancellationToken = default);
}
