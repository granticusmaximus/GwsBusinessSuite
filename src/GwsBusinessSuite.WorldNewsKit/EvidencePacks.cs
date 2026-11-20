using System.Security.Cryptography;
using System.Text;

namespace GwsBusinessSuite.WorldNewsKit;

// An evidence pack (19-11-26): a story's dossier PDF whose SHA-256 is recorded in a hash chain, so a
// copy handed to a client can be checked later. Each entry's hash covers the one before it, so
// changing or removing an entry breaks every hash after it (Verify).
public sealed record EvidencePack(long Number, string EventId, string Sha256, string MadeBy, DateTimeOffset MadeAt, string PreviousHash, string Hash);

public static class EvidencePacks
{
    public static string Fingerprint(byte[] file) => Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();

    public static string HashOf(string previousHash, string eventId, string sha256, string madeBy, DateTimeOffset madeAt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{previousHash}|{eventId}|{sha256}|{madeBy}|{madeAt.ToUnixTimeSeconds()}"))).ToLowerInvariant();

    // True when every entry links to the one before and its hash is right.
    public static bool Verify(IReadOnlyList<EvidencePack> chain)
    {
        var previous = LabelLedger.Genesis;
        foreach (var pack in chain.OrderBy(p => p.Number))
        {
            if (pack.PreviousHash != previous || pack.Hash != HashOf(pack.PreviousHash, pack.EventId, pack.Sha256, pack.MadeBy, pack.MadeAt)) return false;
            previous = pack.Hash;
        }
        return true;
    }
}
