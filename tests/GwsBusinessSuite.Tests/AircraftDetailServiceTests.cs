using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.LiveLayers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class AircraftDetailServiceTests
{
    // Live adsbdb answers for Delta 1536 (2026-10-20), trimmed.
    private const string Aircraft = """
        {"response":{"aircraft":{"type":"737NG 932ER/W","icao_type":"B739","manufacturer":"Boeing","mode_s":"AB4B34","registration":"N827DN",
          "registered_owner_country_iso_name":"US","registered_owner_operator_flag_code":"DAL","registered_owner":"Delta Air Lines"}}}
        """;

    private const string Route = """
        {"response":{"flightroute":{"callsign":"DAL1536","callsign_icao":"DAL1536","callsign_iata":"DL1536",
          "airline":{"name":"Delta Air Lines","icao":"DAL","iata":"DL","country":"United States"},
          "origin":{"iata_code":"JFK","icao_code":"KJFK","latitude":40.6398,"longitude":-73.7789,"municipality":"New York","name":"John F Kennedy International Airport"},
          "destination":{"iata_code":"LAS","icao_code":"KLAS","latitude":36.0801,"longitude":-115.152,"municipality":"Las Vegas","name":"McCarran International Airport"}}}}
        """;

    // The same plane's real OpenSky track that day: it took off from St Louis and was landing in
    // Atlanta, so the JFK -> LAS route on file for its flight number was out of date.
    private const string Track = """
        {"icao24":"ab4b34","callsign":"DAL1536 ","startTime":1792497618,"endTime":1792501220,"path":[
          [1792497618,38.7466,-90.3644,0,122,false],[1792497792,38.6425,-90.1826,1524,130,false],
          [1792500000,null,null,10000,100,false],[1792501220,33.6495,-84.5336,609,89,false]]}
        """;

    private static readonly AirportInfo Jfk = new("JFK", "New York", 40.6398, -73.7789);
    private static readonly AirportInfo Las = new("LAS", "Las Vegas", 36.0801, -115.152);
    private static readonly AirportInfo Atl = new("ATL", "Atlanta", 33.6367, -84.4281);

    [Fact]
    public void Parsers_ShouldReadAdsbDbAndOpenSky()
    {
        var aircraft = AircraftDetailService.ParseAircraft(Aircraft)!;
        aircraft.Registration.Should().Be("N827DN");
        aircraft.TypeCode.Should().Be("B739");
        aircraft.Owner.Should().Be("Delta Air Lines");
        aircraft.OwnerCode.Should().Be("DAL");

        var route = AircraftDetailService.ParseRoute(Route)!;
        route.CallsignIata.Should().Be("DL1536");
        route.AirlineIcao.Should().Be("DAL");
        route.Origin.Should().Be(Jfk);
        route.Destination!.Code.Should().Be("LAS");

        var track = AircraftDetailService.ParseTrack(Track);
        track.Should().HaveCount(3, "points without a position are skipped");
        track[0].AltitudeFeet.Should().Be(0);
        track[1].AltitudeFeet.Should().BeApproximately(5000, 1);

        AircraftDetailService.ParseAircraft("""{"response":"unknown aircraft"}""").Should().BeNull();
        AircraftDetailService.ParseRoute("""{"response":"unknown callsign"}""").Should().BeNull();
        AircraftDetailService.ParseTrack("""{"path":null}""").Should().BeEmpty();
    }

    [Theory]
    [InlineData("ab4b34", "DAL1536", "N827DN", "B739", "DAL", null, null, AircraftUse.PassengerAirline)]
    [InlineData("ab4b34", "DAL1536", "N827DN", null, "DAL", null, null, AircraftUse.PassengerAirline)]
    [InlineData("a3f1c2", "1072", "N319NB", "A319", null, "Delta Air Lines", "DAL", AircraftUse.PassengerAirline)]
    [InlineData("a4d946", "EJA411", "N411QS", "E55P", "EJA", "NetJets", "EJA", AircraftUse.PrivateCharter)]
    [InlineData("a12345", "FDX1402", "N123FE", "B763", "FDX", "Federal Express", "FDX", AircraftUse.Cargo)]
    [InlineData("a76d1c", "N578LA", null, null, null, null, null, AircraftUse.Private)]
    [InlineData("aa2a75", "N754S", "N754S", "B738", null, "Someone", null, AircraftUse.Private)]
    [InlineData("a02a0d", "CTX55", "N11CA", "P28A", null, "Sky Dragon LLC", "P28A", AircraftUse.Private)]
    [InlineData("a835af", "N628TS", "N628TS", "G650", null, "Falcon Landing LLC", "G650", AircraftUse.Private)]
    [InlineData("4ca123", "GABCD", null, "C172", null, null, null, AircraftUse.Private)]
    [InlineData("ae1234", "RCH871", null, "C17", null, null, null, AircraftUse.Military)]
    [InlineData("a00001", "RCH871", null, null, null, null, null, AircraftUse.Military)]
    [InlineData("a00001", "TEST1", null, null, null, "United States Air Force", null, AircraftUse.Military)]
    [InlineData("0d1236", "AMX021", null, null, null, null, null, AircraftUse.Unknown)]
    [InlineData("a02a0d", "", null, null, null, null, null, AircraftUse.Unknown)]
    public void Classify_ShouldTellAirlinesCargoPrivateAndMilitaryApart(string hex, string callsign, string? registration,
        string? typeCode, string? airline, string? owner, string? ownerCode, AircraftUse expected) =>
        AircraftDetailService.Classify(hex, callsign, registration, typeCode, airline, owner, ownerCode).Should().Be(expected);

    [Fact]
    public void TypicalSeats_ShouldOnlyCoverAirlinerTypes()
    {
        AircraftDetailService.TypicalSeatsFor("B739").Should().Be("177-220");
        AircraftDetailService.TypicalSeatsFor("E145").Should().Be("50");
        AircraftDetailService.TypicalSeatsFor("E55P").Should().BeNull();
        AircraftDetailService.TypicalSeatsFor(null).Should().BeNull();
    }

    [Fact]
    public void PlanRoute_ShouldTrustTheTrackOverAnOutOfDateRouteOnFile()
    {
        var route = new FlightRoute("DAL1536", "DL1536", "Delta Air Lines", "DAL", Jfk, Las);

        // Took off from St Louis, landing in Atlanta: the JFK -> LAS route on file is wrong at both ends.
        var stale = AircraftDetailService.PlanRoute(route, AircraftDetailService.ParseTrack(Track), 33.6495, -84.5336);
        stale.Origin!.Code.Should().Be("STL");
        stale.OriginSource.Should().Be("TRACKED TAKEOFF");
        stale.Destination.Should().BeNull();
        stale.Note.Should().Contain("JFK → LAS").And.Contain("out of date");

        // Same flight number, but on file as STL -> ATL: both ends confirmed.
        var confirmed = AircraftDetailService.PlanRoute(route with { Origin = new AirportInfo("STL", "St Louis", 38.7487, -90.37), Destination = Atl },
            AircraftDetailService.ParseTrack(Track), 33.6495, -84.5336);
        confirmed.OriginSource.Should().Be("CONFIRMED BY TRACKED TAKEOFF");
        confirmed.Destination.Should().Be(Atl);
        confirmed.Note.Should().BeNull();

        // On file as JFK -> ATL (a multi-stop flight number): wrong origin, but it is heading for Atlanta.
        var heading = AircraftDetailService.PlanRoute(route with { Destination = Atl }, AircraftDetailService.ParseTrack(Track), 35.5, -87.0);
        heading.Origin!.Code.Should().Be("STL");
        heading.Destination.Should().Be(Atl);
        heading.DestinationSource.Should().Be("ON FILE, HEADING THERE");

        // Track picked up mid-flight: judged by whether the plane is on the JFK - LAS line.
        var cruising = new List<TrackPoint> { new(41.0, -88.0, 36000) };
        AircraftDetailService.PlanRoute(route, cruising, 41.0, -88.0).Destination.Should().Be(Las);
        AircraftDetailService.PlanRoute(route, [], 41.0, -88.0).OriginSource.Should().Be("ON FILE");
        var offLine = AircraftDetailService.PlanRoute(route, cruising, 33.6495, -84.5336);
        offLine.Origin.Should().BeNull();
        offLine.Destination.Should().BeNull();
        offLine.Note.Should().Contain("doesn't fit");

        // No route on file: the takeoff airport is still known.
        var noRoute = AircraftDetailService.PlanRoute(null, AircraftDetailService.ParseTrack(Track), 33.6495, -84.5336);
        noRoute.Origin!.Code.Should().Be("STL");
        noRoute.Destination.Should().BeNull();
        AircraftDetailService.PlanRoute(null, [], 41.0, -88.0).Should().Be(new RoutePlan(null, null, null, null, null));
    }

    [Fact]
    public void NearestAirport_ShouldNameTheFieldAPlaneTookOffFrom()
    {
        AircraftDetailService.NearestAirport(33.6347, -84.4256)!.Code.Should().Be("ATL");
        AircraftDetailService.NearestAirport(40.85, -74.06)!.Code.Should().Be("TEB", "private jets use airports without airline service too");
        AircraftDetailService.NearestAirport(35.0, -40.0).Should().BeNull("mid-Atlantic");
    }

    [Fact]
    public async Task GetAsync_ShouldCombineAllThreeLookups_AndCacheThem()
    {
        var handler = new RoutingHandler(url =>
            url.Contains("/aircraft/") ? (HttpStatusCode.OK, Aircraft)
            : url.Contains("/callsign/") ? (HttpStatusCode.OK, Route)
            : (HttpStatusCode.OK, Track));
        var service = new AircraftDetailService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<AircraftDetailService>.Instance);

        var detail = await service.GetAsync("AB4B34", "dal1536 ", 33.6495, -84.5336);

        detail.Use.Should().Be("COMMERCIAL - PASSENGER AIRLINE");
        detail.IsCommercial.Should().BeTrue();
        detail.FlightNumber.Should().Be("DL1536 (DAL1536)");
        detail.Airline.Should().Be("Delta Air Lines");
        detail.Registration.Should().Be("N827DN");
        detail.AircraftType.Should().Be("Boeing 737NG 932ER/W");
        detail.TypicalSeats.Should().Be("177-220");
        detail.Route.Origin!.Code.Should().Be("STL", "the track shows it took off from St Louis, not JFK");
        detail.Route.Destination.Should().BeNull();
        detail.Track.Should().HaveCount(3);
        handler.Urls.Should().BeEquivalentTo(
            "https://api.adsbdb.com/v0/aircraft/ab4b34",
            "https://api.adsbdb.com/v0/callsign/DAL1536",
            "https://opensky-network.org/api/tracks/all?icao24=ab4b34&time=0");

        await service.GetAsync("ab4b34", "DAL1536", 33.6, -84.5);
        handler.Urls.Should().HaveCount(3, "a second click within the minute is served from cache");
    }

    [Fact]
    public async Task GetAsync_ShouldCopeWithUnknownsAndKeepPrivateOwnersOut()
    {
        const string privatePlane = """
            {"response":{"aircraft":{"type":"172S","icao_type":"C172","manufacturer":"Cessna","mode_s":"A76D1C","registration":null,"registered_owner":"Jane Example"}}}
            """;
        var handler = new RoutingHandler(url =>
            url.Contains("/aircraft/") ? (HttpStatusCode.OK, privatePlane)
            : url.Contains("/callsign/") ? (HttpStatusCode.NotFound, """{"response":"unknown callsign"}""")
            : (HttpStatusCode.NotFound, ""));
        var service = new AircraftDetailService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<AircraftDetailService>.Instance);

        var detail = await service.GetAsync("a76d1c", "N578LA", 33.7, -84.3);

        detail.Use.Should().Be("PRIVATE - GENERAL AVIATION");
        detail.IsCommercial.Should().BeFalse();
        detail.Registration.Should().Be("N578LA", "a US private plane's callsign is its tail number");
        detail.Operator.Should().BeNull("a private plane's registered owner is often a person");
        detail.FlightNumber.Should().BeNull();
        detail.TypicalSeats.Should().BeNull();
        detail.Route.Should().Be(new RoutePlan(null, null, null, null, null));
        detail.Track.Should().BeEmpty();

        (await service.GetAsync("not-hex", "bad/../callsign", 0, 0)).Use.Should().Be("NOT IDENTIFIED");
        handler.Urls.Should().HaveCount(3, "malformed ids are never sent upstream");
    }

    private sealed class RoutingHandler(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Urls) Urls.Add(url);
            var (status, body) = answer(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
