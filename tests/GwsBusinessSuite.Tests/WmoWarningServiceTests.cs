using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Overwatch WORLD WARNINGS (19-11-26): WMO's public index of current CAP alerts, placed by issuing country.
public sealed class WmoWarningServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 30, 0, TimeSpan.Zero);

    // Shaped like severeweather.wmo.int/v2/json/wmo_all.json (19-11-26).
    private const string Index = """
        {"itemCount": 7, "lastUpdated": "2026-11-19 15:34:03", "items": [
          {"id": "a1", "event": "TORMENTAS FUERTES", "headline": "AVISO NARANJA", "sent": "2026-11-19 14:44:00", "expires": "2026-11-19 18:44:00",
           "areaDesc": "SAN LUIS: CNEL PRINGLES - GRAL PEDERNERA.", "s": 3, "u": 4, "c": 4, "capURL": "ar-smn-es/2026/11/19/14/48/26-333-303.xml"},
          {"id": "a2", "event": "VIENTO", "headline": "AVISO AMARILLO", "sent": "2026-11-19 12:00:00", "expires": "2026-11-20 06:00:00",
           "areaDesc": "MENDOZA", "s": 2, "capURL": "ar-smn-es/2026/11/19/12/00/00-1.xml"},
          {"id": "a3", "event": "OLD", "headline": "expired", "sent": "2026-11-14 10:00:00", "expires": "2026-11-14 16:00:00",
           "areaDesc": "SALTA", "s": 4, "capURL": "ar-smn-es/2026/11/14/10/00/00-2.xml"},
          {"id": "b1", "event": "Tempestade", "headline": "Aviso de Tempestade", "sent": "2026-11-19 15:32:16", "expires": "2026-11-23 02:59:00",
           "areaDesc": "Centro Goiano", "s": 2, "url": "br-inmet-pt/2026/11/19/12/33/x.xml"},
          {"id": "r1", "event": "awareness_type=1, awareness_level=4", "headline": "Cod rosu de vant", "expires": "",
           "areaDesc": "Brasov", "s": 4, "capURL": "ro-meteoroma/2026/11/19/x.xml"},
          {"id": "u1", "event": "Winter Storm Warning", "headline": "NWS", "expires": "2026-11-20 06:00:00",
           "areaDesc": "Duluth", "s": 3, "capURL": "us-noaa-nws-en/2026/11/19/x.xml"},
          {"id": "x1", "event": "Unknown place", "headline": "?", "expires": "2026-11-20 06:00:00",
           "areaDesc": "?", "s": 2, "capURL": "zz-nowhere/2026/11/19/x.xml"}
        ]}
        """;

    [Fact]
    public void Parse_ShouldGiveOnePinPerCountry_AtItsWorstCurrentWarning()
    {
        var pins = WmoWarningService.Parse(Index, Now, WorldNewsPlaces.Instance);

        pins.Select(p => p.Id).Should().BeEquivalentTo(["wmo-ar", "wmo-br", "wmo-ro"],
            "the US is drawn by the NWS layer, and a code with no country is skipped");
        var argentina = pins.Single(p => p.Id == "wmo-ar");
        argentina.Title.Should().Be("Argentina: 2 weather warnings (1 severe or extreme)", "the expired warning is gone");
        argentina.Severity.Should().Be("major");
        argentina.Kind.Should().Be("weather-warning");
        argentina.Detail.Should().StartWith("Severe: TORMENTAS FUERTES · SAN LUIS: CNEL PRINGLES - GRAL PEDERNERA")
            .And.Contain("Moderate: VIENTO · MENDOZA")
            .And.Contain("Placed at the country's centre");
        argentina.Url.Should().Be(WmoWarningService.SiteUrl);

        pins.Single(p => p.Id == "wmo-br").Severity.Should().Be("moderate", "an alert without capURL is placed from its url");
        var romania = pins.Single(p => p.Id == "wmo-ro");
        romania.Detail.Should().StartWith("Extreme: Cod rosu de vant", "a coded event name falls back to the headline");
        romania.Detail.Should().NotContain("until", "a warning with no expiry shows none");
        pins[0].Severity.Should().Be("major", "the most serious countries come first");
    }

    [Fact]
    public void Parse_ShouldReturnNothing_ForAnIndexWithoutItems()
    {
        WmoWarningService.Parse("""{"itemCount":0}""", Now, WorldNewsPlaces.Instance).Should().BeEmpty();
    }
}
