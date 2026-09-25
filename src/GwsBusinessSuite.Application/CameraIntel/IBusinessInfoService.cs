namespace GwsBusinessSuite.Application.CameraIntel;

// Powers Overwatch's hover-identify enrichment: when hovering near a real, named point of
// interest, show its name/website/photos instead of (or alongside) the plain reverse-geocoded
// address already shown today. Deliberately returns null rather than throwing when nothing is
// found nearby - a genuinely empty area is the common case, not an error.
public interface IBusinessInfoService
{
    Task<BusinessCardInfo?> GetBusinessCardAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}

public sealed record BusinessCardInfo(
    string Name,
    string? Website,
    string? Category,
    IReadOnlyList<string> PhotoUrls);
