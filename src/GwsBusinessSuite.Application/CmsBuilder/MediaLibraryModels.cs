namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class MediaAssetSummary
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string AltText { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Url { get; set; } = string.Empty;
}
