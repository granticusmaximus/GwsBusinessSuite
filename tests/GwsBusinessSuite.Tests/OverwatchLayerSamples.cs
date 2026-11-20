using System.Text.Json;

namespace GwsBusinessSuite.Tests;

// One sample per Overwatch layer, in the shape OverwatchGrid sends it (TestData/overwatch-layer-samples.json),
// shared by the web globe's check and the Mac map's check so both apps are tested on the same data
// (20-11-26, Grant: every layer visible on the Mac app and the web/PWA app).
internal sealed record OverwatchLayerSample(string Layer, string Op, JsonElement Args, bool NeedsCameras)
{
    public override string ToString() => Layer;

    public static string FilePath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../TestData/overwatch-layer-samples.json"));

    public static IReadOnlyList<OverwatchLayerSample> All { get; } = Load();

    private static List<OverwatchLayerSample> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
        return document.RootElement.EnumerateArray().Select(entry => new OverwatchLayerSample(
            entry.GetProperty("layer").GetString()!,
            entry.GetProperty("op").GetString()!,
            entry.GetProperty("args").Clone(),
            entry.TryGetProperty("needsCameras", out var cameras) && cameras.GetBoolean())).ToList();
    }

    public static IEnumerable<object[]> Names() => All.Select(sample => new object[] { sample.Layer });

    public static OverwatchLayerSample Named(string layer) => All.Single(sample => sample.Layer == layer);

    // The arguments as the Mac bridge gets them from WebKit: dictionaries, object arrays, doubles,
    // strings and booleans.
    public object?[] MacArguments() => (object?[])Convert(Args)!;

    private static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToArray(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
