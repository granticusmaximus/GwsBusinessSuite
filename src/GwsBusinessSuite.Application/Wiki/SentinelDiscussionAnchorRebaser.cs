namespace GwsBusinessSuite.Application.Wiki;

public static class SentinelDiscussionAnchorRebaser
{
    public static SentinelDiscussionAnchor? Rebase(
        SentinelDiscussionAnchor anchor,
        string previousText,
        string currentText)
    {
        if (string.IsNullOrEmpty(anchor.Text) || string.Equals(previousText, currentText, StringComparison.Ordinal))
        {
            return anchor;
        }

        var candidates = new List<int>();
        for (var index = currentText.IndexOf(anchor.Text, StringComparison.Ordinal);
             index >= 0;
             index = currentText.IndexOf(anchor.Text, index + 1, StringComparison.Ordinal))
        {
            candidates.Add(index);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestStart = candidates
            .OrderBy(index => Math.Abs(index - anchor.Start))
            .First();
        return anchor with
        {
            Start = bestStart,
            End = bestStart + anchor.Text.Length
        };
    }
}
