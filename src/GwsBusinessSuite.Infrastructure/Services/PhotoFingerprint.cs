using SkiaSharp;

namespace GwsBusinessSuite.Infrastructure.Services;

// B3: a 64-bit difference hash of a picture: shrunk to 9x8 greys, each bit says whether a pixel is
// brighter than its right-hand neighbour. Survives resizing and recompression; null if not an image.
public static class PhotoFingerprint
{
    public const int MaxBytes = 6 * 1024 * 1024;

    public static ulong? DHash(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        using var original = SKBitmap.Decode(codec);
        if (original is null || original.Width < 32 || original.Height < 32) return null;
        using var small = original.Resize(new SKImageInfo(9, 8, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
        if (small is null) return null;
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (Grey(small.GetPixel(x, y)) > Grey(small.GetPixel(x + 1, y))) hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    private static int Grey(SKColor c) => (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000;
}
