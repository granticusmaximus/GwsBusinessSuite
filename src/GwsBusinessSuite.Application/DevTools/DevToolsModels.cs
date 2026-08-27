using GwsBusinessSuite.Application.ContentStudio;

namespace GwsBusinessSuite.Application.DevTools;

public sealed record DevToolsResult(bool Success, string Output, string? Error = null)
{
    public static DevToolsResult Ok(string output) => new(true, output);
    public static DevToolsResult Fail(string error) => new(false, string.Empty, error);
}

public sealed record DevToolsImageResult(bool Success, byte[]? Bytes, string? Error = null)
{
    public static DevToolsImageResult Ok(byte[] bytes) => new(true, bytes);
    public static DevToolsImageResult Fail(string error) => new(false, null, error);
}

// Kept structured (not a rendered string like DevToolsResult) so the UI can color added/removed
// lines the same way ContentStudioRevisionHistory.razor already does for its own diff view.
public sealed record DevToolsDiffResult(bool Success, IReadOnlyList<ContentStudioDiffLine> Lines, string? Error = null)
{
    public static DevToolsDiffResult Ok(IReadOnlyList<ContentStudioDiffLine> lines) => new(true, lines);
    public static DevToolsDiffResult Fail(string error) => new(false, [], error);
}
