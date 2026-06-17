namespace GwsBusinessSuite.Application.Blog;

public sealed record SanityImportResult(int Imported, int Skipped, int Errors, string Message);

public interface ISanityImportService
{
    Task<SanityImportResult> ImportAsync(CancellationToken ct = default);
}
