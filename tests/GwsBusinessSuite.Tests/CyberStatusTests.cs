using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// Unit 4 (06-11-26): cyber stories are their own World News kind, with globalhackmap's honest status.
public sealed class CyberStatusTests
{
    [Theory]
    [InlineData("Hospital chain confirms ransomware attack disrupted surgeries", CyberStatus.Confirmed)]
    [InlineData("Retailer discloses breach in 8-K filing", CyberStatus.Confirmed)]
    [InlineData("Ransomware gang claims attack on city council and threatens to leak files", CyberStatus.Claimed)]
    [InlineData("Hackers claim to have stolen data of 2 million customers", CyberStatus.Claimed)]
    [InlineData("Airline takes systems offline as a precaution after suspicious activity", CyberStatus.Precautionary)]
    [InlineData("Bank denies it was hacked after group posts data on leak site", CyberStatus.Disputed)]
    [InlineData("Cyberattack hits port operator in Rotterdam", CyberStatus.Reported)]
    public void Status_ShouldFollowWhatTheHeadlinesSay(string headline, string status) =>
        CyberStatus.Of([headline]).Should().Be(status);

    [Fact]
    public void Status_ShouldWeighADenialAboveAConfirmation_AndAConfirmationAboveAClaim()
    {
        CyberStatus.Of(["Gang claims hack of insurer", "Insurer confirms breach", null, ""]).Should().Be(CyberStatus.Confirmed);
        CyberStatus.Of(["Gang claims hack of insurer", "Insurer confirms outage but denies data was stolen"]).Should().Be(CyberStatus.Disputed);
        CyberStatus.Meaning(CyberStatus.Claimed).Should().Be("asserted by the attackers, unverified");
        CyberStatus.Label("anything else").Should().Be("Reported");
    }

    [Fact]
    public void CyberStories_ShouldBeTheirOwnOperationalKind()
    {
        SituationKinds.FromWords("Ransomware attack shuts down hospital systems").Should().Be(SituationKinds.Cyber);
        SituationKinds.FromWords("Hackers attack the national bank").Should().Be(SituationKinds.Cyber, "a tie with conflict goes to cyber");
        SituationKinds.FromWords("Missile attack hits the capital").Should().Be(SituationKinds.Conflict);
        SituationKinds.Operational.Should().Contain(SituationKinds.Cyber);
        SituationKinds.Label(SituationKinds.Cyber).Should().Be("Cyber attacks & breaches");
        SituationKinds.Examples[SituationKinds.Cyber].Should().NotBeEmpty();
        SituationKinds.Examples.Sum(pair => pair.Value.Length).Should().BeLessThanOrEqualTo(64, "the Mac embeds every example in one batch of 64");
    }
}
