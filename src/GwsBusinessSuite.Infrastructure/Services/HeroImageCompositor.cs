using GwsBusinessSuite.Application.Abstractions;
using SkiaSharp;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class HeroImageCompositor : IHeroImageCompositor
{
    public string CompositeTitle(string dataUri, string title)
    {
        if (string.IsNullOrWhiteSpace(dataUri) || string.IsNullOrWhiteSpace(title))
            return dataUri;

        try
        {
            return CompositeInternal(dataUri, title);
        }
        catch
        {
            return dataUri;
        }
    }

    private static string CompositeInternal(string dataUri, string title)
    {
        var commaIndex = dataUri.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0) return dataUri;

        var bytes = Convert.FromBase64String(dataUri[(commaIndex + 1)..]);

        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null) return dataUri;

        var w = bitmap.Width;
        var h = bitmap.Height;
        var padding = w * 0.04f;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        canvas.DrawBitmap(bitmap, 0, 0);

        // Dark navy overlay on bottom 48%
        using var overlayPaint = new SKPaint { Color = new SKColor(8, 12, 28, 215) };
        canvas.DrawRect(new SKRect(0, h * 0.52f, w, h), overlayPaint);

        // Orange accent bar (grantwatson.dev brand colour)
        using var accentPaint = new SKPaint { Color = new SKColor(245, 158, 11, 255) };
        canvas.DrawRect(new SKRect(padding, h * 0.555f, padding + w * 0.13f, h * 0.555f + 4f), accentPaint);

        var typeface = ResolveTypeface();
        var upperTitle = title.ToUpperInvariant();
        var fontSize = upperTitle.Length switch
        {
            <= 20 => 68f,
            <= 35 => 56f,
            <= 50 => 46f,
            _ => 36f,
        };

        using var font = new SKFont(typeface, fontSize) { Edging = SKFontEdging.Antialias };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        var lines = WrapText(upperTitle, font, w - padding * 2f);

        var metrics = font.Metrics;
        var lineHeight = (metrics.Descent - metrics.Ascent) * 1.2f;
        var baselineY = h * 0.605f - metrics.Ascent;

        foreach (var line in lines)
        {
            canvas.DrawText(line, padding, baselineY, font, textPaint);
            baselineY += lineHeight;
        }

        using var snapshot = surface.Snapshot();
        using var pngData = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return $"data:image/png;base64,{Convert.ToBase64String(pngData.ToArray())}";
    }

    private static SKTypeface ResolveTypeface()
    {
        string[] candidates = ["Inter", "Liberation Sans", "Arial", "DejaVu Sans", "FreeSans"];

        foreach (var name in candidates)
        {
            var tf = SKTypeface.FromFamilyName(name, SKFontStyle.Bold);
            if (tf is not null &&
                !string.Equals(tf.FamilyName, SKTypeface.Default.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return tf;
            }
        }

        return SKTypeface.Default;
    }

    private static IReadOnlyList<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (font.MeasureText(candidate) <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
            }
            else
            {
                if (current.Length > 0) lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines;
    }
}
