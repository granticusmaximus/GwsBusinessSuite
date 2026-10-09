using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.MessageSigns;

// A highway dynamic message sign and what it says right now. Pages holds each screen the sign
// cycles through, each a list of lines exactly as shown (signs alternate between pages).
public sealed record MessageSign(
    string Id,
    string Name,
    double Latitude,
    double Longitude,
    string? Road,
    string? Direction,
    IReadOnlyList<IReadOnlyList<string>> Pages,
    string SourceName,
    string SourceAttributionUrl)
{
    public string Text => string.Join(" / ", Pages.Select(page => string.Join(" ", page)));
}

// One per agency feed. Only signs currently showing a message are returned - a blank sign has
// nothing to tell a driver, and most feeds list every sign whether lit or not.
public interface IMessageSignProvider
{
    string SourceName { get; }

    BoundingBox Coverage { get; }

    Task<IReadOnlyList<MessageSign>> GetActiveSignsAsync(CancellationToken cancellationToken = default);
}

public sealed class MessageSignDirectoryService(IEnumerable<IMessageSignProvider> providers, ILogger<MessageSignDirectoryService> logger)
{
    public async Task<IReadOnlyList<MessageSign>> GetSignsInBoundingBoxAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var relevant = providers.Where(p => Overlaps(p.Coverage, bbox)).ToList();
        var results = await Task.WhenAll(relevant.Select(async provider =>
        {
            try
            {
                return await provider.GetActiveSignsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Message sign source {Source} failed.", provider.SourceName);
                return (IReadOnlyList<MessageSign>)[];
            }
        }));
        return results.SelectMany(r => r).Where(sign => bbox.Contains(sign.Latitude, sign.Longitude)).ToList();
    }

    private static bool Overlaps(BoundingBox a, BoundingBox b) =>
        a.South <= b.North && a.North >= b.South && a.West <= b.East && a.East >= b.West;
}

internal static class SignText
{
    // Placeholders some feeds put in empty fields.
    private static readonly HashSet<string> Blank = new(StringComparer.OrdinalIgnoreCase) { "", "Not Reported", "Blank" };

    public static IReadOnlyList<string> CleanLines(IEnumerable<string?> lines) =>
        lines.Select(line => System.Net.WebUtility.HtmlDecode(line ?? string.Empty).Trim())
            .Where(line => !Blank.Contains(line))
            .ToList();
}
