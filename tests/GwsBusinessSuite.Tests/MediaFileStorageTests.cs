using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace GwsBusinessSuite.Tests;

// Media library images on disk: written on upload, served in smaller sizes and as WebP, moved off
// the database for older uploads, and the database copies cleared only after the grace period.
public sealed class MediaFileStorageTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-media-tests-{Guid.NewGuid():N}");
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ApplicationDbContext _db = null!;
    private FileSystemMediaFileStore _store = null!;
    private Clock _clock = null!;
    private MediaLibraryService _service = null!;

    private async Task InitAsync()
    {
        await _connection.OpenAsync();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _store = new FileSystemMediaFileStore(Options.Create(new MediaStorageOptions { StoragePath = _root }));
        _clock = new Clock(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        _service = new MediaLibraryService(_db, new SiteSettingsService(_db), _store, _clock);
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static int WidthOf(byte[] image) => SKBitmap.Decode(image).Width;

    private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    [Fact]
    public async Task Upload_ShouldWriteFiles_AndKeepTheDatabaseCopyForNow()
    {
        await InitAsync();
        var png = Png(1000, 500);
        var summary = await _service.UploadAsync("hero.png", png, "Hero");

        var asset = await _db.MediaAssets.AsNoTracking().SingleAsync();
        asset.StoragePath.Should().Be(MediaFilePaths.Original(summary.Id, "image/png"));
        asset.StoredAt.Should().Be(_clock.Now);
        (asset.Width, asset.Height).Should().Be((1000, 500));
        asset.DataUri.Should().StartWith("data:image/png;base64,", "kept until the grace period passes");
        (await _store.ReadAsync(asset.StoragePath!)).Should().Equal(png);
        _store.GetLength(MediaFilePaths.Thumbnail(summary.Id)).Should().BeGreaterThan(0);
        (await _service.GetContentAsync(summary.Id))!.Value.Content.Should().Equal(png);
    }

    [Fact]
    public async Task GetImage_ShouldServeSmallerCopiesAndWebp_ButNeverEnlarge()
    {
        await InitAsync();
        var id = (await _service.UploadAsync("hero.png", Png(1000, 500), "")).Id;

        var original = (await _service.GetImageAsync(id, null, acceptsWebp: false))!;
        original.ContentType.Should().Be("image/png");
        WidthOf(original.Content).Should().Be(1000);

        var small = (await _service.GetImageAsync(id, 600, acceptsWebp: false))!;
        small.ContentType.Should().Be("image/png");
        WidthOf(small.Content).Should().Be(640, "600 snaps up to the 640 size");
        _store.GetLength($"variants/{id:N}-{_clock.Now.UtcTicks}-w640.png").Should().BeGreaterThan(0, "cached on disk");

        var webp = (await _service.GetImageAsync(id, 320, acceptsWebp: true))!;
        webp.ContentType.Should().Be("image/webp");
        System.Text.Encoding.ASCII.GetString(webp.Content, 8, 4).Should().Be("WEBP");
        WidthOf(webp.Content).Should().Be(320);

        var huge = (await _service.GetImageAsync(id, 1920, acceptsWebp: false))!;
        WidthOf(huge.Content).Should().Be(1000, "never enlarged");
        small.ETag.Should().NotBe(webp.ETag).And.NotBe(original.ETag);

        var gif = (await _service.UploadAsync("spin.gif", Gif, "")).Id;
        (await _service.GetImageAsync(gif, 320, acceptsWebp: true))!.Content.Should().Equal(Gif, "GIFs keep their animation");
    }

    [Fact]
    public async Task Replace_ShouldDropOldSizes_AndChangeTheETag()
    {
        await InitAsync();
        var id = (await _service.UploadAsync("hero.png", Png(1000, 500), "")).Id;
        var before = (await _service.GetImageAsync(id, 640, acceptsWebp: false))!;
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _service.ReplaceAsync(id, "hero.png", Png(800, 800), "");

        Directory.EnumerateFiles(Path.Combine(_root, "variants"), $"{id:N}-{_clock.Start.UtcTicks}-*").Should().BeEmpty();
        var after = (await _service.GetImageAsync(id, 640, acceptsWebp: false))!;
        after.ETag.Should().NotBe(before.ETag);
        SKBitmap.Decode(after.Content).Height.Should().Be(640, "the new square picture");
    }

    [Fact]
    public async Task OlderUploads_ShouldMoveToDisk_AndDatabaseCopiesClearAfterTheGracePeriod()
    {
        await InitAsync();
        var png = Png(400, 400);
        var legacy = new MediaAsset
        {
            FileName = "old.png", ContentType = "image/png", SizeBytes = png.Length, CreatedBy = "test",
            DataUri = $"data:image/png;base64,{Convert.ToBase64String(png)}"
        };
        _db.MediaAssets.Add(legacy);
        await _db.SaveChangesAsync();

        (await _service.MoveToFilesAsync(25)).Should().Be(1);
        (await _service.MoveToFilesAsync(25)).Should().Be(0, "nothing left to move");
        var moved = await _db.MediaAssets.AsNoTracking().SingleAsync();
        moved.StoragePath.Should().NotBeNull();
        moved.DataUri.Should().NotBeEmpty();

        (await _service.ReleaseDatabaseCopiesAsync(TimeSpan.FromDays(7), 100)).Should().Be(0, "still inside the grace period");
        _clock.Advance(TimeSpan.FromDays(7));
        (await _service.ReleaseDatabaseCopiesAsync(TimeSpan.FromDays(7), 100)).Should().Be(1);

        var released = await _db.MediaAssets.AsNoTracking().SingleAsync();
        released.DataUri.Should().BeEmpty();
        (await _service.GetContentAsync(legacy.Id))!.Value.Content.Should().Equal(png, "served from the file now");
    }

    [Fact]
    public async Task DatabaseCopies_ShouldNotBeCleared_WhenTheFileIsMissingOrWrong_AndServingFallsBack()
    {
        await InitAsync();
        var png = Png(400, 400);
        var id = (await _service.UploadAsync("a.png", png, "")).Id;
        var path = (await _db.MediaAssets.AsNoTracking().SingleAsync()).StoragePath!;
        _store.Delete(path);

        (await _service.GetContentAsync(id))!.Value.Content.Should().Equal(png, "the database copy covers a missing file");
        _clock.Advance(TimeSpan.FromDays(30));
        (await _service.ReleaseDatabaseCopiesAsync(TimeSpan.FromDays(7), 100)).Should().Be(0);

        await _store.WriteAsync(path, [1, 2, 3]);
        (await _service.ReleaseDatabaseCopiesAsync(TimeSpan.FromDays(7), 100)).Should().Be(0, "wrong size");
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheFiles()
    {
        await InitAsync();
        var id = (await _service.UploadAsync("a.png", Png(800, 400), "")).Id;
        await _service.GetImageAsync(id, 320, acceptsWebp: true);

        await _service.DeleteAsync(id);

        Directory.EnumerateFiles(_root, $"{id:N}*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Store_ShouldRefusePathsOutsideTheMediaFolder()
    {
        var store = new FileSystemMediaFileStore(Options.Create(new MediaStorageOptions { StoragePath = _root }));
        store.Invoking(s => s.GetLength("../escape.png")).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Srcset_ShouldBeAddedToMediaImagesOnly()
    {
        var id = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
        var html = MediaSrcset.Apply($"""<img src="/media/{id}" alt="a"><img src="https://x.test/p.png" /><img src="/media/{id}" srcset="custom" />""");

        html.Should().Contain($"""<img src="/media/{id}" alt="a" srcset="/media/{id}?w=320 320w, /media/{id}?w=640 640w, /media/{id}?w=960 960w, /media/{id}?w=1280 1280w, /media/{id}?w=1920 1920w" sizes="(max-width: 768px) 100vw, 1200px" decoding="async">""");
        html.Should().Contain("""<img src="https://x.test/p.png" />""");
        html.Should().Contain($"""<img src="/media/{id}" srcset="custom" />""");
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
        await _connection.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Start { get; } = start;
        public DateTimeOffset Now { get; private set; } = start;
        public void Advance(TimeSpan by) => Now += by;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
