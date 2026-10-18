using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.LiveLayers;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// The Overwatch ships layer's aisstream.io handling, without opening the real stream.
public sealed class ShipServiceTests
{
    private const string Position = """
        {"MessageType":"PositionReport","MetaData":{"MMSI":367000001,"ShipName":"EVER GIVEN   @@@","latitude":32.08,"longitude":-81.09,"time_utc":"2026-10-18 12:00:00"},
         "Message":{"PositionReport":{"UserID":367000001,"Latitude":32.08,"Longitude":-81.09,"Sog":12.4,"Cog":95.5,"TrueHeading":511}}}
        """;

    private const string Static = """
        {"MessageType":"ShipStaticData","MetaData":{"MMSI":367000001,"ShipName":"EVER GIVEN"},
         "Message":{"ShipStaticData":{"UserID":367000001,"Name":"EVER GIVEN@@@@","Type":71,"Destination":"SAVANNAH   "}}}
        """;

    [Fact]
    public void ParseMessage_ShouldReadPositionsAndDropAisNotAvailableValues()
    {
        var parsed = ShipService.ParseMessage(Position)!;
        parsed.Mmsi.Should().Be(367000001);
        parsed.Name.Should().Be("EVER GIVEN");
        parsed.Latitude.Should().Be(32.08);
        parsed.SpeedKnots.Should().Be(12.4);
        parsed.CourseDegrees.Should().Be(95.5);
        parsed.HeadingDegrees.Should().BeNull("511 means no heading");

        ShipService.ParseMessage(Position.Replace("\"Latitude\":32.08", "\"Latitude\":91").Replace("\"latitude\":32.08", "\"latitude\":91"))
            .Should().BeNull("91 is AIS for no position");
        ShipService.ParseMessage("""{"MessageType":"PositionReport"}""").Should().BeNull();
        ShipService.ParseMessage("""{"error":"Api Key Is Not Valid"}""").Should().BeNull();
    }

    [Fact]
    public void ParseMessage_ShouldReadClassBAndStaticData()
    {
        var classB = ShipService.ParseMessage("""
            {"MessageType":"StandardClassBPositionReport","MetaData":{"MMSI":338000002},
             "Message":{"StandardClassBPositionReport":{"UserID":338000002,"Latitude":25.7,"Longitude":-80.1,"Sog":102.3,"Cog":360,"TrueHeading":180}}}
            """)!;
        classB.SpeedKnots.Should().BeNull("102.3 means no speed");
        classB.CourseDegrees.Should().BeNull("360 means no course");
        classB.HeadingDegrees.Should().Be(180);

        var details = ShipService.ParseMessage(Static)!;
        details.IsStatic.Should().BeTrue();
        details.ShipType.Should().Be("Cargo");
        details.Destination.Should().Be("SAVANNAH");
    }

    [Fact]
    public async Task Ingest_ShouldMergeDetailsAndOnlyReturnFreshShipsInView()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-18T12:00:00Z"));
        using var service = new ShipService(null, NullLogger<ShipService>.Instance, clock);
        service.Ingest(Static);
        service.Ingest(Position);
        service.Ingest("not json");

        var view = new BoundingBox(33, 31, -80, -82);
        var store = service.ShipsIn(view);
        store.Should().ContainSingle();
        store[0].ShipType.Should().Be("Cargo");
        store[0].Destination.Should().Be("SAVANNAH");
        service.ShipsIn(new BoundingBox(10, 0, 10, 0)).Should().BeEmpty("outside the view");

        // Ships not heard from in half an hour drop off.
        clock.Advance(TimeSpan.FromMinutes(31));
        service.ShipsIn(view).Should().BeEmpty();

        // Without a key the layer explains itself instead of trying to connect.
        var result = await service.GetAsync(view);
        result.Ships.Should().BeEmpty();
        result.Note.Should().Contain("aisstream.io key");
    }

    [Fact]
    public async Task GetAsync_ShouldRefuseHugeViews()
    {
        using var service = new ShipService("key", NullLogger<ShipService>.Instance, TimeProvider.System);
        var result = await service.GetAsync(new BoundingBox(60, 0, 40, -40));
        result.Ships.Should().BeEmpty();
        result.Note.Should().StartWith("Zoom in");
    }

    [Fact]
    public void BuildSubscription_ShouldSendLatLonCornersAndOnlyTheNeededMessageTypes()
    {
        using var doc = JsonDocument.Parse(ShipService.BuildSubscription("k", [new BoundingBox(33, 31, -80, -82)]));
        doc.RootElement.GetProperty("APIKey").GetString().Should().Be("k");
        var box = doc.RootElement.GetProperty("BoundingBoxes")[0];
        box[0][0].GetDouble().Should().Be(31);
        box[0][1].GetDouble().Should().Be(-82);
        box[1][0].GetDouble().Should().Be(33);
        box[1][1].GetDouble().Should().Be(-80);
        doc.RootElement.GetProperty("FilterMessageTypes").GetArrayLength().Should().Be(3);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public void Advance(TimeSpan by) => now += by;
        public override DateTimeOffset GetUtcNow() => now;
    }
}
