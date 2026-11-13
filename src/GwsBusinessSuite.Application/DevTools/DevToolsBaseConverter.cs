using System.Globalization;
using System.Numerics;
using System.Text;

namespace GwsBusinessSuite.Application.DevTools;

// The Number & Binary Converter in Dev Tools (12-11-26): any whole number in any base from 2 to 36
// (no 64-bit ceiling), every common base at once, two's complement at a chosen width, text to and
// from binary/hex/decimal/octal bytes (UTF-8), and a calculator with bitwise operators.
public static class DevToolsBaseConverter
{
    public const int MinBase = 2;
    public const int MaxBase = 36;
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int MaxDigits = 4096;

    public sealed record NumberViews(BigInteger Value, string Binary, string Octal, string Decimal, string Hex, string Custom, int CustomBase, int BitLength);

    public sealed record TwosComplementView(int Bits, string Binary, string Hex, string Signed, string Unsigned);

    // Accepts a leading sign, 0b/0o/0x prefixes (which must match the base when one is given) and
    // spaces, underscores or commas between digit groups, so pasted values just work.
    public static bool TryParse(string? input, int numberBase, out BigInteger value, out string error)
    {
        value = BigInteger.Zero;
        error = string.Empty;
        if (numberBase is < MinBase or > MaxBase) { error = $"The base has to be between {MinBase} and {MaxBase}."; return false; }
        var text = (input ?? string.Empty).Trim().Replace("_", "").Replace(" ", "").Replace(",", "").ToLowerInvariant();
        if (text.Length == 0) { error = "Enter a number."; return false; }
        var negative = false;
        if (text[0] is '-' or '+') { negative = text[0] == '-'; text = text[1..]; }
        // A prefix only when its letter isn't a digit here: in hex, "0b1" is the number 0xB1.
        if (text.Length > 2 && text[0] == '0' && text[1] is 'b' or 'o' or 'x' && Digits.IndexOf(text[1], StringComparison.Ordinal) >= numberBase)
        {
            var prefixBase = text[1] switch { 'b' => 2, 'o' => 8, _ => 16 };
            if (prefixBase != numberBase) { error = $"'0{text[1]}' means base {prefixBase}, but the input base is {numberBase}."; return false; }
            text = text[2..];
        }
        if (text.Length == 0) { error = "Enter a number."; return false; }
        if (text.Length > MaxDigits) { error = $"That's more than {MaxDigits} digits."; return false; }
        foreach (var character in text)
        {
            var digit = Digits.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0 || digit >= numberBase)
            {
                error = $"'{character}' isn't a base-{numberBase} digit (use {DigitRange(numberBase)}).";
                return false;
            }
            value = value * numberBase + digit;
        }
        if (negative) value = -value;
        return true;
    }

    public static string ToBase(BigInteger value, int numberBase)
    {
        if (numberBase is < MinBase or > MaxBase) throw new ArgumentOutOfRangeException(nameof(numberBase));
        if (value.IsZero) return "0";
        var negative = value.Sign < 0;
        var remaining = BigInteger.Abs(value);
        var output = new StringBuilder();
        while (!remaining.IsZero)
        {
            remaining = BigInteger.DivRem(remaining, numberBase, out var digit);
            output.Insert(0, Digits[(int)digit]);
        }
        if (numberBase == 16) output = new StringBuilder(output.ToString().ToUpperInvariant());
        return negative ? "-" + output : output.ToString();
    }

    public static NumberViews Views(BigInteger value, int customBase) => new(
        value,
        ToBase(value, 2),
        ToBase(value, 8),
        value.ToString(CultureInfo.InvariantCulture),
        ToBase(value, 16),
        ToBase(value, Math.Clamp(customBase, MinBase, MaxBase)),
        Math.Clamp(customBase, MinBase, MaxBase),
        value.IsZero ? 0 : (int)BigInteger.Abs(value).GetBitLength());

    // "11111111" -> "1111 1111": binary in nibbles, hex in bytes, decimal in thousands, from the right.
    public static string Group(string digits, int size, char separator = ' ')
    {
        if (size <= 0 || digits.Length <= size) return digits;
        var sign = digits.StartsWith('-') ? "-" : string.Empty;
        var body = sign.Length > 0 ? digits[1..] : digits;
        var output = new StringBuilder();
        for (var index = 0; index < body.Length; index++)
        {
            if (index > 0 && (body.Length - index) % size == 0) output.Append(separator);
            output.Append(body[index]);
        }
        return sign + output;
    }

    // The value as a fixed-width register: negative numbers in two's complement. Null when the value
    // doesn't fit (signed or unsigned) in that many bits.
    public static TwosComplementView? TwosComplement(BigInteger value, int bits)
    {
        if (bits is < 1 or > 1024) return null;
        var modulus = BigInteger.One << bits;
        var signedMin = -(BigInteger.One << (bits - 1));
        if (value < signedMin || value >= modulus) return null;
        var unsigned = value.Sign < 0 ? value + modulus : value;
        var signed = unsigned >= (BigInteger.One << (bits - 1)) ? unsigned - modulus : unsigned;
        return new TwosComplementView(
            bits,
            ToBase(unsigned, 2).PadLeft(bits, '0'),
            ToBase(unsigned, 16).PadLeft((bits + 3) / 4, '0'),
            signed.ToString(CultureInfo.InvariantCulture),
            unsigned.ToString(CultureInfo.InvariantCulture));
    }

    // Text as UTF-8 bytes, each byte written in the base: binary pads to 8 digits, hex to 2,
    // octal to 3; decimal is unpadded. "Hi" -> "01001000 01101001".
    public static string TextToBytes(string text, int numberBase)
    {
        var width = numberBase switch { 2 => 8, 8 => 3, 16 => 2, _ => 0 };
        return string.Join(' ', Encoding.UTF8.GetBytes(text ?? string.Empty).Select(b => width == 0 ? ToBase(b, numberBase) : ToBase(b, numberBase).PadLeft(width, '0')));
    }

    // The reverse: bytes separated by spaces, commas or new lines. Binary without separators is
    // read 8 digits at a time and hex 2 at a time, so "0100100001101001" and "4869" both work.
    public static bool TryBytesToText(string? input, int numberBase, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        var raw = (input ?? string.Empty).Trim();
        if (raw.Length == 0) { error = "Enter some bytes."; return false; }
        var parts = raw.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => (numberBase == 16 && part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                || (numberBase == 2 && part.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) ? part[2..] : part)
            .ToList();
        var chunk = numberBase switch { 2 => 8, 16 => 2, _ => 0 };
        if (parts.Count == 1 && chunk > 0 && parts[0].Length > chunk)
        {
            if (parts[0].Length % chunk != 0) { error = $"{parts[0].Length} digits don't split into whole {chunk}-digit bytes."; return false; }
            parts = Enumerable.Range(0, parts[0].Length / chunk).Select(index => parts[0].Substring(index * chunk, chunk)).ToList();
        }
        var bytes = new byte[parts.Count];
        for (var index = 0; index < parts.Count; index++)
        {
            if (!TryParse(parts[index], numberBase, out var value, out var parseError)) { error = $"Byte {index + 1}: {parseError}"; return false; }
            if (value < 0 || value > 255) { error = $"Byte {index + 1} ({parts[index]}) is outside 0-255."; return false; }
            bytes[index] = (byte)value;
        }
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            error = "Those bytes aren't valid UTF-8 text.";
            return false;
        }
    }

    public static readonly IReadOnlyList<(string Key, string Label)> Operators =
    [
        ("+", "+"), ("-", "−"), ("*", "×"), ("/", "÷ (whole)"), ("%", "mod"), ("pow", "power"),
        ("and", "AND"), ("or", "OR"), ("xor", "XOR"), ("nand", "NAND"), ("nor", "NOR"),
        ("shl", "<< shift left"), ("shr", ">> shift right"), ("not", "NOT A")
    ];

    public static bool TryCalculate(BigInteger a, string op, BigInteger b, out BigInteger result, out string error)
    {
        result = BigInteger.Zero;
        error = string.Empty;
        switch (op)
        {
            case "+": result = a + b; return true;
            case "-": result = a - b; return true;
            case "*": result = a * b; return true;
            case "/" or "%" when b.IsZero: error = "Can't divide by zero."; return false;
            case "/": result = BigInteger.Divide(a, b); return true;
            case "%": result = BigInteger.Remainder(a, b); return true;
            case "pow" when b.Sign < 0 || b > 4096: error = "The power has to be between 0 and 4096."; return false;
            case "pow": result = BigInteger.Pow(a, (int)b); return true;
            case "and": result = a & b; return true;
            case "or": result = a | b; return true;
            case "xor": result = a ^ b; return true;
            case "nand": result = ~(a & b); return true;
            case "nor": result = ~(a | b); return true;
            case "shl" or "shr" when b.Sign < 0 || b > 4096: error = "Shift by 0 to 4096 bits."; return false;
            case "shl": result = a << (int)b; return true;
            case "shr": result = a >> (int)b; return true;
            case "not": result = ~a; return true;
            default: error = $"Unknown operator '{op}'."; return false;
        }
    }

    private static string DigitRange(int numberBase) =>
        numberBase <= 10 ? $"0-{numberBase - 1}" : $"0-9 and a-{Digits[numberBase - 1]}";
}
