using FluentAssertions;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.LiveLayers;

namespace GwsBusinessSuite.Tests;

// Overwatch world signals, unit 3 (06-11-26): FAA airport status and the aurora. Fixtures are
// trimmed from the live feeds of 06-11-26.
public sealed class SkyLayerServiceTests
{
    [Fact]
    public void AirportStatus_ShouldPinEachAirportOnce_AtItsWorst()
    {
        const string xml = """
            <AIRPORT_STATUS_INFORMATION><Update_Time>Fri Nov 6 12:58:47 2026 GMT</Update_Time><Dtd_File>http://www.fly.faa.gov/AirportStatus.dtd</Dtd_File>
            <Delay_type><Name>Ground Stop Programs</Name><Ground_Stop_List><Program><ARPT>JFK</ARPT><Reason>thunderstorms</Reason><End_Time>9:00 am EST</End_Time></Program></Ground_Stop_List></Delay_type>
            <Delay_type><Name>Ground Delay Programs</Name><Ground_Delay_List><Ground_Delay><ARPT>SFO</ARPT><Reason>low ceilings</Reason><Avg>1 hour and 4 minutes</Avg><Max>2 hours</Max></Ground_Delay></Ground_Delay_List></Delay_type>
            <Delay_type><Name>General Arrival/Departure Delay Info</Name><Arrival_Departure_Delay_List>
              <Delay><ARPT>JFK</ARPT><Reason>TM Initiatives:SWAP:WX</Reason><Arrival_Departure Type="Departure"><Min>31 minutes</Min><Max>45 minutes</Max><Trend>Increasing</Trend></Arrival_Departure></Delay>
              <Delay><ARPT>DCA</ARPT><Reason>WX:Thunderstorms</Reason><Arrival_Departure Type="Departure"><Min>31 minutes</Min><Max>45 minutes</Max><Trend>Increasing</Trend></Arrival_Departure></Delay>
              <Delay><ARPT>LGA</ARPT><Reason>VOL:Volume</Reason><Arrival_Departure Type="Arrival"><Min>1 hour and 1 minute</Min><Max>1 hour and 15 minutes</Max><Trend>Decreasing</Trend></Arrival_Departure></Delay>
              <Delay><ARPT>ZZZ</ARPT><Reason>WX:Wind</Reason><Arrival_Departure Type="Arrival"><Min>15 minutes</Min><Max>30 minutes</Max></Arrival_Departure></Delay>
            </Arrival_Departure_Delay_List></Delay_type>
            <Delay_type><Name>Airport Closures</Name><Airport_Closure_List>
              <Airport><ARPT>LAX</ARPT><Reason>!LAX 05/277 LAX AD AP CLSD TO NON SKED TRANSIENT GA ACFT EXC 24HR PPR 2605271826-2705281600</Reason><Start>May 27 at 18:26 UTC.</Start><Reopen>May 28 at 16:00 UTC.</Reopen></Airport>
              <Airport><ARPT>SNA</ARPT><Reason>!sna 11/003 sna ad ap clsd dly 0730-1415 2611030730-2611081415</Reason><Start>Nov 03</Start><Reopen>Nov 08 at 14:15 UTC.</Reopen></Airport>
              <Airport><ARPT>ASE</ARPT><Reason>!ase 11/129 ase ad ap clsd exc life flt hel and sar hel 30min ppr 2611060600-2611061400</Reason><Start>Nov 06</Start><Reopen>Nov 06 at 14:00 UTC.</Reopen></Airport>
            </Airport_Closure_List></Delay_type>
            </AIRPORT_STATUS_INFORMATION>
            """;

        var airports = AirportStatusService.Parse(xml);

        airports.Select(a => a.Id).Should().Equal(["faa-ASE", "faa-DCA", "faa-JFK", "faa-LAX", "faa-LGA", "faa-SFO", "faa-SNA"], "ZZZ isn't an airport we can place");
        airports.Should().OnlyContain(a => a.Kind == HazardKinds.Airport && a.ObservedAt == new DateTimeOffset(2026, 11, 6, 12, 58, 47, TimeSpan.Zero));
        var jfk = airports.Single(a => a.Id == "faa-JFK");
        jfk.Should().Match<HazardFeature>(a => a.Severity == "major" && a.Title == "JFK (New York): ground stop" && a.Latitude == 40.6394);
        jfk.Detail.Should().Be("Ground stop (thunderstorms) until 9:00 am EST · Departure delays 31 minutes to 45 minutes, increasing (routing around severe weather)");
        airports.Single(a => a.Id == "faa-DCA").Should().Match<HazardFeature>(a => a.Severity == "minor" && a.Detail.EndsWith("(thunderstorms)"));
        airports.Single(a => a.Id == "faa-LGA").Should().Match<HazardFeature>(a => a.Severity == "moderate" && a.Detail.StartsWith("Arrival delays") && a.Detail.EndsWith("(traffic volume)"));
        airports.Single(a => a.Id == "faa-SFO").Detail.Should().Be("Ground delay program (low ceilings): average 1 hour and 4 minutes, up to 2 hours");
        airports.Single(a => a.Id == "faa-LAX").Should().Match<HazardFeature>(a => a.Severity == "minor" && a.Title == "LAX (Los Angeles): closed to some traffic"
            && a.Detail.StartsWith("Closed to some traffic (NOTAM LAX 05/277 LAX AD AP CLSD TO NON SKED TRANSIENT GA ACFT") && a.Detail.EndsWith("reopening May 28 at 16:00 UTC."));
        airports.Single(a => a.Id == "faa-SNA").Should().Match<HazardFeature>(a => a.Severity == "minor" && a.Title.EndsWith("closed at set hours each day"));
        airports.Single(a => a.Id == "faa-ASE").Should().Match<HazardFeature>(a => a.Severity == "major" && a.Title == "ASE (Aspen): closed");

        FluentActions.Invoking(() => AirportStatusService.Parse("""<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><AIRPORT_STATUS_INFORMATION>&e;</AIRPORT_STATUS_INFORMATION>"""))
            .Should().Throw<System.Xml.XmlException>();
    }

    [Fact]
    public void Ovation_ShouldFillThePolarBands_FromTheDateLineEastward()
    {
        const string json = """
            {"Observation Time":"2026-11-06T12:52:00Z","Forecast Time":"2026-11-06T14:06:00Z","coordinates":[
              [0,90,7],[0,40,12],[359,65,18],[180,-60,4],[100,20,99],[10,-90,9],[45,70,0]]}
            """;

        var (north, south, peak, forecastFor) = SpaceWeatherService.ParseOvation(json);

        north.Should().HaveCount(AuroraForecast.Rows * AuroraForecast.Columns);
        peak.Should().Be(18, "the 99 at 20°N is outside the polar bands");
        forecastFor.Should().Be(new DateTimeOffset(2026, 11, 6, 14, 6, 0, TimeSpan.Zero));
        static int At(int row, int column) => row * AuroraForecast.Columns + column;
        north[At(0, 180)].Should().Be(7, "90°N at 0° is row 0, column 180");
        north[At(50, 180)].Should().Be(12, "40°N is the last row");
        north[At(25, 179)].Should().Be(18, "359°E is 1°W, column 179");
        south[At(20, 0)].Should().Be(4, "60°S at 180° is row 20, column 0");
        south[At(50, 190)].Should().Be(9);
        north.Count(b => b > 0).Should().Be(3);
    }

    [Fact]
    public void Kp_ShouldReadTheLatestValue_InEitherOfSwpcsShapes()
    {
        SpaceWeatherService.ParseKp("""[{"time_tag":"2026-11-06T06:00:00","Kp":1.67},{"time_tag":"2026-11-06T09:00:00","Kp":2.33}]""")
            .Should().Be((2.33, new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));
        SpaceWeatherService.ParseKp("""[["time_tag","Kp","a_running","station_count"],["2026-11-06 06:00:00.000","5.33","27","8"]]""").Kp.Should().Be(5.33);
        SpaceWeatherService.Describe(2.33).Should().Be("quiet");
        SpaceWeatherService.Describe(5.33).Should().Be("G1 minor storm");
        SpaceWeatherService.Describe(9).Should().Be("G5 extreme storm");
    }
}
