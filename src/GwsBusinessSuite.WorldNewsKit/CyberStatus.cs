using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// How sure a cyber story is (06-11-26), using globalhackmap's honest scale, read from what the
// headlines say:
//   Confirmed     acknowledged by the organisation, a regulator or a filing
//   Reported      covered by the press, not yet confirmed by the organisation
//   Claimed       asserted by the attackers, unverified
//   Precautionary systems taken offline as a precaution, no compromise stated
//   Disputed      reported, and denied by the organisation
// A denial outweighs a confirmation, which outweighs an attacker's claim. Nothing said means Reported.
public static partial class CyberStatus
{
    public const string Confirmed = "confirmed";
    public const string Reported = "reported";
    public const string Claimed = "claimed";
    public const string Precautionary = "precautionary";
    public const string Disputed = "disputed";

    public static string Of(IEnumerable<string?> headlines)
    {
        var text = string.Join("\n", headlines.Where(h => !string.IsNullOrWhiteSpace(h)));
        if (Denied().IsMatch(text)) return Disputed;
        if (Acknowledged().IsMatch(text)) return Confirmed;
        if (AttackerClaim().IsMatch(text)) return Claimed;
        if (Precaution().IsMatch(text)) return Precautionary;
        return Reported;
    }

    public static string Label(string status) => status switch
    {
        Confirmed => "Confirmed",
        Claimed => "Claimed",
        Precautionary => "Precautionary",
        Disputed => "Disputed",
        _ => "Reported"
    };

    public static string Meaning(string status) => status switch
    {
        Confirmed => "acknowledged by the organisation, a regulator or a filing",
        Claimed => "asserted by the attackers, unverified",
        Precautionary => "systems taken offline as a precaution, no compromise stated",
        Disputed => "reported, and denied by the organisation",
        _ => "covered by the press, not yet confirmed by the organisation"
    };

    [GeneratedRegex(@"\b(denies|denied|deny|refutes?|refuted|disputes?|disputed|rejects? (the )?claims?|no evidence (of|that))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Denied();

    [GeneratedRegex(@"\b(confirms?|confirmed|acknowledges?|acknowledged|admits?|admitted|discloses?|disclosed|notif(y|ies|ied) (its )?(customers|users|patients)|8-K|SEC filing|regulatory filing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Acknowledged();

    [GeneratedRegex(@"\b(claims?|claimed)\b[^.\n]{0,60}\b(attack|hack|hacked|breach|responsibility|data|stole|stolen)\b|\bleak site\b|\bthreatens? to (leak|publish|release)\b|\b(gang|group|hackers) (says|say)\b|\bclaimed by\b", RegexOptions.IgnoreCase)]
    private static partial Regex AttackerClaim();

    [GeneratedRegex(@"\b(as a precaution|precautionary|proactively|taken offline|took (its |some |their )?systems offline|shut down (its |some |their )?systems)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Precaution();
}
