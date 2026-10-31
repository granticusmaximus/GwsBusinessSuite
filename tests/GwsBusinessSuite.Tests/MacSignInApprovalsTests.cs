using FluentAssertions;
using GwsBusinessSuite.Application.NativeApp;

namespace GwsBusinessSuite.Tests;

// "Approve with your Mac" (31-10-26): a browser signs in with the fingerprint on the person's Mac,
// by picking the browser's number there. Only the browser that asked can use the approval, once.
public sealed class MacSignInApprovalsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void PickingTheBrowsersNumber_ShouldSignThatBrowserIn_Once()
    {
        var approvals = new MacSignInApprovals(new Clock());
        var started = approvals.Start("grant", "Chrome on Windows", "203.0.113.5");

        var request = approvals.WaitingFor("GRANT").Should().NotBeNull().And.Subject.As<MacSignInRequest>();
        request.Id.Should().Be(started.Id);
        request.Numbers.Should().HaveCount(3).And.OnlyHaveUniqueItems().And.Contain(started.Number);
        request.Browser.Should().Be("Chrome on Windows");
        approvals.State(started.Id, started.Secret).Should().Be(MacSignInState.Waiting);
        approvals.Redeem(started.Id, started.Secret).Should().BeNull("not approved yet");

        approvals.Approve(started.Id, "grant", started.Number).Should().BeNull();

        approvals.State(started.Id, "someone-else's-guess").Should().Be(MacSignInState.Expired, "only the browser that asked can see it");
        approvals.State(started.Id, started.Secret).Should().Be(MacSignInState.Approved);
        approvals.Redeem(started.Id, started.Secret).Should().Be("grant");
        approvals.Redeem(started.Id, started.Secret).Should().BeNull("an approval signs in once");
        approvals.WaitingFor("grant").Should().BeNull();
    }

    [Fact]
    public void TheWrongNumber_SomeoneElsesRequest_OrAnOldOne_ShouldSignNoOneIn()
    {
        var clock = new Clock();
        var approvals = new MacSignInApprovals(clock);
        var started = approvals.Start("grant", "Firefox on Linux", null);
        var wrong = approvals.WaitingFor("grant")!.Numbers.First(n => n != started.Number);

        approvals.Approve(started.Id, "sam", started.Number).Should().NotBeNull("it's grant's request, not sam's");
        approvals.Approve(started.Id, "grant", wrong).Should().Contain("isn't the number");
        approvals.State(started.Id, started.Secret).Should().Be(MacSignInState.Declined, "a wrong pick ends the request");
        approvals.Approve(started.Id, "grant", started.Number).Should().NotBeNull();
        approvals.Redeem(started.Id, started.Secret).Should().BeNull();

        var late = approvals.Start("grant", "Safari on a Mac", null);
        clock.Now += MacSignInApprovals.Lifetime + TimeSpan.FromSeconds(1);
        approvals.WaitingFor("grant").Should().BeNull("two minutes and it's gone");
        approvals.Approve(late.Id, "grant", late.Number).Should().NotBeNull();
        approvals.State(late.Id, late.Secret).Should().Be(MacSignInState.Expired);
    }

    [Fact]
    public void ANewerRequest_ShouldReplaceTheOneWaiting_AndTheMacCanDecline()
    {
        var approvals = new MacSignInApprovals(new Clock());
        var first = approvals.Start("grant", "Chrome on Windows", null);
        var second = approvals.Start("grant", "Chrome on Windows", null);

        approvals.State(first.Id, first.Secret).Should().Be(MacSignInState.Declined);
        approvals.WaitingFor("grant")!.Id.Should().Be(second.Id);

        approvals.Decline(second.Id, "grant");
        approvals.State(second.Id, second.Secret).Should().Be(MacSignInState.Declined);
        approvals.WaitingFor("grant").Should().BeNull();
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36", "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15", "Safari on a Mac")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0", "Edge on Windows")]
    [InlineData(null, "A browser")]
    public void DescribeBrowser_ShouldNameTheBrowserAndSystem(string? userAgent, string expected) =>
        MacSignInApprovals.DescribeBrowser(userAgent).Should().Be(expected);
}
