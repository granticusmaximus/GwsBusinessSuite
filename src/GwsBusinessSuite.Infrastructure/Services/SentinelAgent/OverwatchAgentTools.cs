using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Briefings;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Geocoding;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Overwatch for the agent (Phase D, 30-10-26): read only, from the same numbered facts "Brief me"
// and the morning briefing use (incidents, weather alerts, hazards, cameras, World News). Overwatch
// is an Admin page. World News search itself is ResearchAgentTools' search_world_news.
public sealed class OverwatchAgentTools(OverwatchBriefingService briefings, IGeocodingService geocoding) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    // About 30 km each way: a town and its surroundings.
    private const double PlaceRadiusDegrees = 0.27;
    private const int FactsPerArea = 25;

    public SentinelAgentArea Area { get; } = new("overwatch", "Overwatch: what's happening around a place", ["overwatch", "traffic", "weather", "incident", "road", "camera", "hazard", "storm", "watched area", "trip"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "overwatch_briefing",
            "What's happening now around a place (traffic incidents, weather alerts, hazards, cameras, local news), or, with no place, in your Overwatch watched areas and saved trips.",
            SentinelAgentJson.Schema(("place", "string", "A town, city or address; leave out for your watched areas", false)),
            SentinelAgentRisk.Read, Roles,
            args => args.OptionalText("place") is { } place ? $"Checking Overwatch around {place}" : "Checking your Overwatch areas",
            BriefingAsync)
    ];

    private async Task<SentinelAgentToolResult> BriefingAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        if (args.OptionalText("place") is { } place)
        {
            var spot = await geocoding.GeocodeAsync(place, ct)
                ?? throw new SentinelAgentToolException($"Couldn't find '{place}' on the map. Try a town and state.");
            var bounds = new BoundingBox(
                spot.Latitude + PlaceRadiusDegrees, spot.Latitude - PlaceRadiusDegrees,
                spot.Longitude + PlaceRadiusDegrees, spot.Longitude - PlaceRadiusDegrees);
            return SentinelAgentToolResult.Of(new { areas = new[] { AreaFacts(place, await briefings.GetViewFactsAsync(context.User.Username, place, bounds, ct)) } });
        }

        var sections = await briefings.ComposeMorningAsync(context.User.Username, ct);
        if (sections.Count == 0)
            throw new SentinelAgentToolException("There are no watched areas or saved trips in Overwatch yet. Name a place instead.");
        return SentinelAgentToolResult.Of(new
        {
            areas = sections.Select(section => section.Facts is { } facts ? AreaFacts(section.Name, facts) : new { name = section.Name, facts = (IEnumerable<string>?)null, error = section.Error })
        });
    }

    private static object AreaFacts(string name, BriefingFacts facts) =>
        new { name, facts = facts.Facts.Take(FactsPerArea).Select(fact => $"[{fact.Kind}] {fact.Text}"), error = (string?)null };
}
