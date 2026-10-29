using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// A throwaway World News database with situations seeded as a read would leave them.
internal sealed class WorldNewsTestStore : IDisposable
{
    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wn-map-{Guid.NewGuid():N}.db");

    public WorldNewsTestStore() => Store = new WorldNewsStore(_path);

    public WorldNewsStore Store { get; }

    // Two events of three outlets each, linked into one situation.
    public void SeedSituation(string id, string firstTitle, string secondTitle)
    {
        SeedEvent(id, $"{id}-1", firstTitle, ["Outlet 0", "Outlet 1", "Outlet 2"], Now.AddHours(-4));
        SeedEvent(id, $"{id}-2", secondTitle, ["Outlet 0", "Outlet 1", "Outlet 2"], Now.AddHours(-4));
    }

    public void SeedEvent(string situationId, string eventId, string title, string[] outlets, DateTimeOffset seen)
    {
        var situation = new NewsSituation { Id = situationId, Sum = [1f, 0f], LastSeen = Now.AddHours(-1) };
        var reports = outlets.Select((outlet, i) => new WorldArticle($"{eventId}-{i}", $"https://example.com/{eventId}/{i}",
            title, outlet, "English", "United Kingdom", seen, "test", null)).ToArray();
        Store.AddArticles(reports);
        Store.SaveClustering(new ClusterResult(reports.Select(r => (r.Id, eventId)).ToList(),
        [
            new NewsEvent
            {
                Id = eventId, Title = title, TitleLanguage = "English", Sum = [reports.Length, 0f], ArticleCount = reports.Length,
                FirstSeen = seen.AddHours(-1), LastSeen = Now.AddHours(-1)
            }
        ], 1));
        Store.SaveLinks([new SituationLink(eventId, situation)]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(file);
    }
}
