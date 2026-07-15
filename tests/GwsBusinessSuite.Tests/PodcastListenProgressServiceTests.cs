using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Podcasts;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class PodcastListenProgressServiceTests
{
    [Fact]
    public async Task SaveProgressAsync_ShouldCreateNewRow_OnFirstSave()
    {
        await using var db = await CreateDbAsync();
        var episode = await CreateEpisodeAsync(db);
        var service = CreateService(db, "grantwatson");

        await service.SaveProgressAsync(episode.Id, 42, 1800);

        var progress = await service.GetProgressForEpisodesAsync([episode.Id]);
        progress.Should().ContainKey(episode.Id);
        progress[episode.Id].PositionSeconds.Should().Be(42);
        progress[episode.Id].DurationSeconds.Should().Be(1800);
        progress[episode.Id].IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task SaveProgressAsync_ShouldUpdateExistingRow_OnSubsequentSaves()
    {
        await using var db = await CreateDbAsync();
        var episode = await CreateEpisodeAsync(db);
        var service = CreateService(db, "grantwatson");

        await service.SaveProgressAsync(episode.Id, 42, 1800);
        await service.SaveProgressAsync(episode.Id, 100, 1800);

        var progress = await service.GetProgressForEpisodesAsync([episode.Id]);
        progress[episode.Id].PositionSeconds.Should().Be(100);
        (await db.PodcastListenProgresses.CountAsync(p => p.EpisodeId == episode.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SaveProgressAsync_ShouldMarkCompleted_WhenPositionCrossesTheThreshold()
    {
        await using var db = await CreateDbAsync();
        var episode = await CreateEpisodeAsync(db);
        var service = CreateService(db, "grantwatson");

        // 96% of a 1000s episode - past the 95% completion threshold.
        await service.SaveProgressAsync(episode.Id, 960, 1000);

        var progress = await service.GetProgressForEpisodesAsync([episode.Id]);
        progress[episode.Id].IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task SaveProgressAsync_ShouldNotMarkCompleted_WhenBelowThreshold()
    {
        await using var db = await CreateDbAsync();
        var episode = await CreateEpisodeAsync(db);
        var service = CreateService(db, "grantwatson");

        await service.SaveProgressAsync(episode.Id, 500, 1000);

        var progress = await service.GetProgressForEpisodesAsync([episode.Id]);
        progress[episode.Id].IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task MarkCompletedAsync_ShouldSetCompleted_EvenWithoutAPriorSave()
    {
        await using var db = await CreateDbAsync();
        var episode = await CreateEpisodeAsync(db);
        var service = CreateService(db, "grantwatson");

        await service.MarkCompletedAsync(episode.Id);

        var progress = await service.GetProgressForEpisodesAsync([episode.Id]);
        progress[episode.Id].IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task GetProgressForEpisodesAsync_ShouldOnlyReturnTheCurrentUsersRows()
    {
        await using var db = await CreateDbAsync();
        var episode = await CreateEpisodeAsync(db);
        var grantService = CreateService(db, "grantwatson");
        var otherService = CreateService(db, "someoneelse");

        await grantService.SaveProgressAsync(episode.Id, 42, 1800);

        (await otherService.GetProgressForEpisodesAsync([episode.Id])).Should().BeEmpty();
        (await grantService.GetProgressForEpisodesAsync([episode.Id])).Should().ContainKey(episode.Id);
    }

    [Fact]
    public async Task GetProgressForEpisodesAsync_ShouldReturnEmpty_ForNoEpisodeIds()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, "grantwatson");

        var result = await service.GetProgressForEpisodesAsync([]);

        result.Should().BeEmpty();
    }

    private static PodcastListenProgressService CreateService(ApplicationDbContext db, string username) =>
        new(db, new FixedCurrentUserAccessor(username));

    private static async Task<PodcastEpisode> CreateEpisodeAsync(ApplicationDbContext db)
    {
        var show = new PodcastShow { Title = "Test Show" };
        db.PodcastShows.Add(show);
        var episode = new PodcastEpisode { PodcastShowId = show.Id, Title = "Test Episode", AudioUrl = "https://example.com/ep.mp3" };
        db.PodcastEpisodes.Add(episode);
        await db.SaveChangesAsync();
        return episode;
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
