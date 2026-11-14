using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;
using SkiaSharp;

namespace GwsBusinessSuite.Tests;

// B3: recycled photos: fingerprints, the matching rule, share-image discovery and C2PA presence.
public sealed class WorldNewsRecycledPhotoTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static byte[] Picture(int width, int height, bool flip = false, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            for (var x = 0; x < width; x++)
            {
                var shade = (byte)(flip ? 255 - x * 255 / width : x * 255 / width);
                using var paint = new SKPaint { Color = new SKColor(shade, (byte)(shade / 2), (byte)(255 - shade)) };
                canvas.DrawRect(x, 0, 1, height, paint);
            }
            using var block = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(width / 3f, height / 3f, width / 4f, height / 4f, block);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 80).ToArray();
    }

    [Fact]
    public void DHash_ShouldMatchAResizedRecompressedCopy_AndNotADifferentPicture()
    {
        var original = PhotoFingerprint.DHash(Picture(640, 400))!.Value;
        var smaller = PhotoFingerprint.DHash(Picture(320, 200, format: SKEncodedImageFormat.Png))!.Value;
        var different = PhotoFingerprint.DHash(Picture(640, 400, flip: true))!.Value;

        Assert.True(RecycledPhotos.Distance(original, smaller) <= RecycledPhotos.MaxDistance);
        Assert.True(RecycledPhotos.Distance(original, different) > RecycledPhotos.MaxDistance);
        Assert.Null(PhotoFingerprint.DHash([1, 2, 3]));
    }

    private static PhotoPrint Print(string article, string eventId, string outlet, ulong hash, int daysAgo) =>
        new(article, eventId, outlet, $"Story {eventId}", $"https://img.example/{article}.jpg", hash, false, Now.AddDays(-daysAgo));

    [Fact]
    public void Find_ShouldFlagAPictureUsedDaysEarlierInAnotherStoryByAnotherOutlet()
    {
        var story = new[] { Print("new", "flood-2026", "Daily Example", 0xF0F0_F0F0_F0F0_F0F0, 0) };
        var archive = new[]
        {
            story[0],
            Print("old", "flood-2019", "Other Paper", 0xF0F0_F0F0_F0F0_F0F1, 30),      // 1 bit off, a month earlier
            Print("own", "storm-2025", "Daily Example", 0xF0F0_F0F0_F0F0_F0F0, 60),    // its own file photo: not flagged
            Print("fresh", "flood-2026b", "Third Paper", 0xF0F0_F0F0_F0F0_F0F0, 1),    // a day old: same news cycle
        };

        var found = Assert.Single(RecycledPhotos.Find(story, archive, Now));

        Assert.Equal("old", found.Earlier.ArticleId);
        Assert.Contains("30 days earlier by Other Paper", RecycledPhotos.Describe(found));
    }

    [Fact]
    public void Find_ShouldIgnoreLogosAndStockCards_SeenInManyStories()
    {
        const ulong logo = 0x1234_5678_9ABC_DEF0;
        var story = new[] { Print("new", "e-new", "Paper A", logo, 0) };
        var archive = Enumerable.Range(0, RecycledPhotos.StockEvents).Select(i => Print($"a{i}", $"e{i}", $"Paper {i}", logo, 10 + i)).ToList();

        Assert.Empty(RecycledPhotos.Find(story, archive, Now));
    }

    [Theory]
    [InlineData("""<meta property="og:image" content="https://cdn.example/a.jpg">""", "https://cdn.example/a.jpg")]
    [InlineData("""<meta content="/img/b.png" property='og:image' />""", "https://news.example/img/b.png")]
    [InlineData("""<meta name="twitter:image" content="https://cdn.example/t.jpg">""", "https://cdn.example/t.jpg")]
    [InlineData("""<meta property="og:image" content="data:image/png;base64,AAAA">""", null)]
    [InlineData("""<p>no picture</p>""", null)]
    public void ShareImage_ShouldFindThePagesSharePicture(string html, string? expected) =>
        Assert.Equal(expected, RecycledPhotos.ShareImage(html, new Uri("https://news.example/story/1")));

    [Fact]
    public void HasContentCredentials_ShouldSpotAC2paBox()
    {
        var with = new byte[] { 0xFF, 0xD8, 0xFF, 0xEB, 0, 0 }.Concat("jumb"u8.ToArray()).Concat(new byte[] { 0, 1 }).Concat("c2pa"u8.ToArray()).ToArray();

        Assert.True(RecycledPhotos.HasContentCredentials(with));
        Assert.False(RecycledPhotos.HasContentCredentials(Picture(64, 64)));
    }
}
