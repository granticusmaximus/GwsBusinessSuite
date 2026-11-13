namespace GwsBusinessSuite.WorldNewsKit;

// B4 (13-11-26): whether an article's text changed materially between two reads. Word 3-gram overlap
// (Jaccard) under the threshold counts; a reworded sentence or two in a long piece doesn't.
public static class BodyChanges
{
    public const double SameThreshold = 0.85;

    public static double Similarity(string a, string b)
    {
        var first = Shingles(a);
        var second = Shingles(b);
        if (first.Count == 0 && second.Count == 0) return 1;
        var shared = first.Count(second.Contains);
        return (double)shared / (first.Count + second.Count - shared);
    }

    public static bool IsMaterial(string before, string after) => Similarity(before, after) < SameThreshold;

    private static HashSet<string> Shingles(string text)
    {
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray())).Where(word => word.Length > 0).ToList();
        var shingles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < words.Count; i++) shingles.Add(words[i] + " " + words[i + 1] + " " + words[i + 2]);
        return shingles;
    }
}
