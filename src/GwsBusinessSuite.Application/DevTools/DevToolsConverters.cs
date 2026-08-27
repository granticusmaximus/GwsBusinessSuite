namespace GwsBusinessSuite.Application.DevTools;

public static class DevToolsConverters
{
    public static DevToolsResult ConvertNumberBase(string input, int fromBase, int toBase)
    {
        long value;
        try
        {
            value = fromBase == 10 ? long.Parse(input.Trim()) : Convert.ToInt64(input.Trim(), fromBase);
        }
        catch (FormatException)
        {
            return DevToolsResult.Fail($"'{input}' isn't a valid base-{fromBase} number.");
        }
        catch (OverflowException)
        {
            return DevToolsResult.Fail("That number is too large to convert (max 64-bit signed).");
        }

        var output = toBase switch
        {
            10 => value.ToString(),
            16 => Convert.ToString(value, 16).ToUpperInvariant(),
            _ => Convert.ToString(value, toBase)
        };
        return DevToolsResult.Ok(output);
    }

    public static DevToolsResult UnixTimestampToDateTime(long unixSeconds)
    {
        try
        {
            return DevToolsResult.Ok(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DevToolsResult.Fail("That timestamp is out of range.");
        }
    }

    public static string DateTimeToUnixTimestamp(DateTimeOffset value) => value.ToUnixTimeSeconds().ToString();

    public static DevToolsResult HexToRgb(string hex)
    {
        var normalized = hex.Trim().TrimStart('#');
        if (normalized.Length != 6 || !normalized.All(Uri.IsHexDigit))
        {
            return DevToolsResult.Fail("Enter a 6-digit hex color, e.g. 3366FF.");
        }

        var r = Convert.ToInt32(normalized[..2], 16);
        var g = Convert.ToInt32(normalized[2..4], 16);
        var b = Convert.ToInt32(normalized[4..6], 16);
        var (h, s, l) = RgbToHsl(r, g, b);
        return DevToolsResult.Ok($"RGB: rgb({r}, {g}, {b})\nHSL: hsl({h:0}, {s:0}%, {l:0}%)");
    }

    private static (double H, double S, double L) RgbToHsl(int r, int g, int b)
    {
        var rn = r / 255.0;
        var gn = g / 255.0;
        var bn = b / 255.0;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        var l = (max + min) / 2;

        if (max == min)
        {
            return (0, 0, l * 100);
        }

        var delta = max - min;
        var s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double h;
        if (max == rn) h = (gn - bn) / delta + (gn < bn ? 6 : 0);
        else if (max == gn) h = (bn - rn) / delta + 2;
        else h = (rn - gn) / delta + 4;
        h *= 60;

        return (h, s * 100, l * 100);
    }
}
