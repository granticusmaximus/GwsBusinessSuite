using System.Security.Cryptography;
using System.Text;

namespace GwsBusinessSuite.Application.DevTools;

public static class DevToolsGenerators
{
    // Lowercased to match the conventional display of tools like sha256sum/git, not Convert's
    // default uppercase hex.
    public static string HashMd5(string input) => ToLowerHex(MD5.HashData(Encoding.UTF8.GetBytes(input)));
    public static string HashSha1(string input) => ToLowerHex(SHA1.HashData(Encoding.UTF8.GetBytes(input)));
    public static string HashSha256(string input) => ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public static string HashSha512(string input) => ToLowerHex(SHA512.HashData(Encoding.UTF8.GetBytes(input)));

    private static string ToLowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string NewGuid(bool uppercase, bool includeHyphens)
    {
        var value = Guid.NewGuid().ToString(includeHyphens ? "D" : "N");
        return uppercase ? value.ToUpperInvariant() : value;
    }

    private const string LowerChars = "abcdefghijklmnopqrstuvwxyz";
    private const string UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitChars = "0123456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{}";

    public static DevToolsResult GeneratePassword(int length, bool includeUpper, bool includeDigits, bool includeSymbols)
    {
        if (length is < 4 or > 256)
        {
            return DevToolsResult.Fail("Password length must be between 4 and 256 characters.");
        }

        var alphabet = LowerChars
            + (includeUpper ? UpperChars : string.Empty)
            + (includeDigits ? DigitChars : string.Empty)
            + (includeSymbols ? SymbolChars : string.Empty);

        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return DevToolsResult.Ok(new string(result));
    }

    private static readonly string[] LoremWords =
    [
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do",
        "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim",
        "ad", "minim", "veniam", "quis", "nostrud", "exercitation", "ullamco", "laboris", "nisi",
        "aliquip", "ex", "ea", "commodo", "consequat", "duis", "aute", "irure", "in", "reprehenderit",
        "voluptate", "velit", "esse", "cillum", "eu", "fugiat", "nulla", "pariatur", "excepteur",
        "sint", "occaecat", "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia",
        "deserunt", "mollit", "anim", "id", "est", "laborum"
    ];

    public static string GenerateLoremIpsum(int paragraphs, int sentencesPerParagraph)
    {
        var random = Random.Shared;
        var result = new StringBuilder();
        for (var p = 0; p < paragraphs; p++)
        {
            if (p > 0) result.Append("\n\n");
            for (var s = 0; s < sentencesPerParagraph; s++)
            {
                var wordCount = random.Next(6, 14);
                var words = new string[wordCount];
                for (var w = 0; w < wordCount; w++)
                {
                    words[w] = LoremWords[random.Next(LoremWords.Length)];
                }
                words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
                result.Append(string.Join(' ', words)).Append(". ");
            }
        }
        return result.ToString().TrimEnd();
    }
}
