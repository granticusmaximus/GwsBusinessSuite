using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.RouteWatch;

// What a vision model read from one camera frame. Level is the overall call for the route strip:
// "bad" (snow/ice/flooding on the road, or stopped traffic), "caution" (wet road, fog, falling
// precipitation, heavy traffic), "ok", or "unknown" (the frame showed nothing usable).
public sealed record CameraConditionReading(string Road, string Visibility, string Traffic, string Note, string Answer, string Level);

// "Ask the cameras": the prompt for a local vision model and a lenient reader for its answer.
// Small vision models don't always obey a schema - stray prose around the JSON, capitalised or
// unexpected values - so every field is normalised to a known value or "unknown".
public static partial class CameraConditions
{
    public const string BadLevel = "bad";
    public const string CautionLevel = "caution";
    public const string OkLevel = "ok";
    public const string UnknownLevel = "unknown";

    private static readonly string[] RoadValues = ["dry", "wet", "snow", "ice", "flooded"];
    private static readonly string[] VisibilityValues = ["clear", "rain", "fog", "snow", "dark"];
    private static readonly string[] TrafficValues = ["none", "light", "moderate", "heavy", "stopped"];

    public const string SystemPrompt =
        "You classify one still image from a highway traffic camera. Reply with JSON only, exactly these keys: " +
        "{\"road\":\"dry|wet|snow|ice|flooded|unknown\",\"visibility\":\"clear|rain|fog|snow|dark|unknown\"," +
        "\"traffic\":\"none|light|moderate|heavy|stopped|unknown\",\"note\":\"\",\"answer\":\"\"}. " +
        "note: at most 15 words on anything notable (a crash, debris, a closure, emergency vehicles), else empty. " +
        "answer: at most 20 words answering the user's question from this image alone, else empty. " +
        "Use unknown for anything the image doesn't show (a blank or error image, darkness, a camera pointed away). " +
        "Never guess beyond what is visible.";

    public static string UserPrompt(string? question) =>
        string.IsNullOrWhiteSpace(question)
            ? "Classify this camera image."
            : $"Classify this camera image. The user's question: {question.Trim()}";

    public static CameraConditionReading Parse(string? modelOutput)
    {
        var json = JsonObjectPattern().Match(modelOutput ?? string.Empty);
        if (!json.Success) return Unknown("The model didn't return a reading.");
        try
        {
            using var document = JsonDocument.Parse(json.Value);
            var root = document.RootElement;
            var road = Pick(root, "road", RoadValues);
            var visibility = Pick(root, "visibility", VisibilityValues);
            var traffic = Pick(root, "traffic", TrafficValues);
            return new CameraConditionReading(road, visibility, traffic,
                Text(root, "note", 160), Text(root, "answer", 200), LevelOf(road, visibility, traffic));
        }
        catch (JsonException)
        {
            return Unknown("The model's reading wasn't valid JSON.");
        }
    }

    public static string LevelOf(string road, string visibility, string traffic)
    {
        if (road is "snow" or "ice" or "flooded" || traffic == "stopped") return BadLevel;
        if (road == "wet" || visibility is "rain" or "fog" or "snow" || traffic == "heavy") return CautionLevel;
        if (road == "unknown" && visibility == "unknown" && traffic == "unknown") return UnknownLevel;
        return OkLevel;
    }

    private static CameraConditionReading Unknown(string note) =>
        new("unknown", "unknown", "unknown", note, string.Empty, UnknownLevel);

    private static string Pick(JsonElement root, string name, string[] allowed)
    {
        var value = root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim().ToLowerInvariant()
            : null;
        return value is not null && allowed.Contains(value) ? value : "unknown";
    }

    private static string Text(JsonElement root, string name, int max)
    {
        var value = root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    // Whatever the model writes around it, the reading is the first {...} block.
    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectPattern();

    // Local models with image input, matched by name - Ollama's tag list doesn't say which can
    // see. Used only to preselect a model; any installed model can still be picked.
    private static readonly string[] VisionModelHints =
        ["llava", "vision", "gemma3", "gemma4", "qwen2.5vl", "qwen2-vl", "qwen3-vl", "minicpm-v", "moondream", "bakllava", "mistral-small3"];

    public static bool LooksVisionCapable(string modelName) =>
        VisionModelHints.Any(hint => modelName.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
