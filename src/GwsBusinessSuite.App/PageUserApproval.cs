using GwsBusinessSuite.SentinelAgentKit;

namespace GwsBusinessSuite.App;

// The first dialog/confirmation surface in this app. Developer Mode's WorkspaceTools requires a
// real human answer before any file write or command runs, so this must return the user's actual
// choice to the awaiting tool call - hence InvokeOnMainThreadAsync (awaitable) rather than the
// fire-and-forget BeginInvokeOnMainThread this page uses for its other, result-less UI updates.
public sealed class PageUserApproval(Page page) : IUserApproval
{
    private const int MaxDialogCharacters = 3000;

    public Task<bool> ConfirmAsync(string action, string details, CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var message = details.Length <= MaxDialogCharacters
                ? details
                : details[..MaxDialogCharacters] + "\n... truncated ...";
            return await page.DisplayAlertAsync($"Approve {action}?", message, "Approve", "Decline");
        });
}
