using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Boards;

// A board's look (Boards K7), kept in its settings: card size, an accent colour and a background -
// one of a few gradients that suit both themes, or an image.
public static partial class KanbanLook
{
    public const string NoBackground = "none";
    public const string ImageBackground = "image";

    public static readonly IReadOnlyList<(string Key, string Label)> CardSizes =
    [
        (KanbanCardSizes.Compact, "Compact"), (KanbanCardSizes.Comfortable, "Comfortable"), (KanbanCardSizes.Rich, "Rich")
    ];

    public static readonly IReadOnlyList<(string Color, string Name)> Accents =
    [
        ("#3b82f6", "Blue"), ("#8b5cf6", "Violet"), ("#ec4899", "Pink"), ("#ef4444", "Red"),
        ("#f97316", "Orange"), ("#eab308", "Gold"), ("#10b981", "Green"), ("#06b6d4", "Cyan")
    ];

    public static readonly IReadOnlyList<(string Key, string Label)> Backgrounds =
    [
        (NoBackground, "None"), ("aurora", "Aurora"), ("dusk", "Dusk"), ("meadow", "Meadow"), ("ocean", "Ocean"), ("grid", "Dots")
    ];

    public static bool IsPreset(string? background) => Backgrounds.Any(item => item.Key == background);

    // An image the board can show behind it: https:// or a path on this site (/media/...), with
    // nothing that could step out of a CSS url() - no quotes, spaces, brackets or backslashes.
    public static bool IsImage(string? background) =>
        background is { Length: > 1 and <= 500 } && SafeImage().IsMatch(background);

    [GeneratedRegex(@"^(https://[A-Za-z0-9.-]+(:\d+)?/|/(?!/))[^\s""'()<>\\;{}]*$")]
    private static partial Regex SafeImage();
}
