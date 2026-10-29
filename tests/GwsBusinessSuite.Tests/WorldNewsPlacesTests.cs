using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class WorldNewsPlacesTests
{
    private static WorldNewsPlaces Places => WorldNewsPlaces.Instance;

    [Theory]
    [InlineData("Two High-Speed Trains Collide in Spain Near Madrid", "ES", "Madrid")]
    [InlineData("Spain's prime minister visits Toledo after the crash", "ES", "Toledo")]
    [InlineData("Tokyo hit by widespread power cut", "JP", "Tokyo")]
    [InlineData("Floods in Lagos, Nigeria leave thousands homeless", "NG", "Lagos")]
    public void Locate_ShouldPlaceAtACityWhenOneIsNamed(string headline, string country, string city)
    {
        var place = Places.Locate(headline);

        place.Should().NotBeNull();
        place!.CountryCode.Should().Be(country);
        place.City.Should().Be(city);
        place.Precision.Should().Be(WorldNewsPrecision.City);
    }

    [Theory]
    [InlineData("Japón: un terremoto sacude el norte del país", "JP")]
    [InlineData("日本で強い地震", "JP")]
    [InlineData("Wahl in Österreich: Regierung verliert Mehrheit", "AT")]
    [InlineData("Spanish-led rescue reaches the stranded hikers", "ES")]
    [InlineData("Cambodia stranded cruise passengers flown home", "KH")]
    public void Locate_ShouldReadEveryCountryInManyLanguages(string headline, string country)
    {
        Places.Locate(headline)!.CountryCode.Should().Be(country);
    }

    [Theory]
    [InlineData("New Mexico wildfire forces evacuations", "a US state, not Mexico")]
    [InlineData("Georgia runoff election results", "Georgia is a US state as often as a country")]
    [InlineData("Valencia floods: death toll rises", "Valencia is in Spain and in Venezuela")]
    [InlineData("Toledo council approves the budget", "a mid-sized city alone could be anywhere")]
    [InlineData("What the storm means for us", "case matters")]
    public void Locate_ShouldNotGuess(string headline, string because)
    {
        Places.Locate(headline).Should().BeNull(because);
    }

    [Fact]
    public void Locate_ShouldKeepCountryLevelWhenTheOnlyCityIsAnEverydayWord()
    {
        var place = Places.Locate("Wedding Season Returns to Germany");

        place!.CountryCode.Should().Be("DE");
        place.City.Should().BeNull("Wedding is a Berlin district but here it's just a word");
        place.Precision.Should().Be(WorldNewsPrecision.Country);
    }

    [Fact]
    public void Locate_ShouldSharpenToACityFromTheDetailText()
    {
        var place = Places.Locate("Trains collide in Spain", "The crash happened near Toledo, south of Madrid.");

        place!.City.Should().Be("Madrid", "the biggest city named in the country wins");
        place.Label.Should().Be("Madrid, Spain");
    }

    [Fact]
    public void FindCountryAndCity_ShouldResolveCorrections()
    {
        Places.FindCountry("España")!.Code.Should().Be("ES");
        Places.FindCountry("es")!.Name.Should().Be("Spain");
        Places.FindCity("ES", "toledo")!.Name.Should().Be("Toledo");
        Places.FindCity("US", "toledo")!.CountryCode.Should().Be("US");
        Places.FindCity("ES", "Narnia").Should().BeNull();
        Places.Countries.Should().HaveCountGreaterThan(200).And.OnlyHaveUniqueItems(c => c.Code);
    }

    [Theory]
    [InlineData(40.42, -3.70, "ES")]
    [InlineData(48.85, 2.35, "FR")]
    [InlineData(38.90, -77.03, "US")]
    [InlineData(-33.87, 151.21, "AU")]
    [InlineData(0.0, -30.0, null)]
    public void CountryAt_ShouldFindTheCountryAPointIsIn(double lat, double lon, string? code)
    {
        WorldNewsPlaces.CountryAt(lat, lon).Should().Be(code);
    }
}
