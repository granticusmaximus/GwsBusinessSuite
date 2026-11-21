using FluentAssertions;
using GwsBusinessSuite.Application.Mail;

namespace GwsBusinessSuite.Tests;

// GWS's own inbox sorting (21-11-26): rules first, sure only when the winner is clear.
public sealed class MailSorterTests
{
    private static MailSortResult Sort(string from, string subject, params (string Name, string Value)[] headers) =>
        MailSorter.Classify(new MailSortInput(from, string.Empty, subject,
            headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public void APersonWriting_ShouldBePrimary_AndSure()
    {
        var result = Sort("sam.jones@example.com", "Lunch on Friday?");
        result.Should().Be(new MailSortResult("inbox:primary", true, "a person writing, no list or automated headers"));
        Sort("sam.jones@example.com", "Re: Lunch on Friday?").TabKey.Should().Be("inbox:primary");
    }

    [Fact]
    public void ASocialNetwork_ShouldBeSocial()
    {
        var result = Sort("notification@facebookmail.com", "Sam commented on your photo",
            ("List-Unsubscribe", "<https://facebook.com/unsub>"), ("Precedence", "bulk"));
        result.TabKey.Should().Be("inbox:social");
        result.Sure.Should().BeTrue();
        Sort("messages-noreply@linkedin.com", "You have 3 new messages", ("List-Unsubscribe", "<x>")).TabKey.Should().Be("inbox:social");
    }

    [Fact]
    public void ADiscussionList_ShouldBeForums()
    {
        var result = Sort("someone@gmail.com", "[dotnet-dev] Question about Span<T>",
            ("List-Id", "<dotnet-dev.googlegroups.com>"), ("List-Post", "<mailto:dotnet-dev@googlegroups.com>"), ("Precedence", "list"),
            ("List-Unsubscribe", "<mailto:unsub@googlegroups.com>"));
        result.TabKey.Should().Be("inbox:forums");
        result.Sure.Should().BeTrue();
    }

    [Fact]
    public void AShippingNotice_ShouldBeUpdates()
    {
        var result = Sort("shipment-tracking@amazon.com", "Your order has shipped", ("Auto-Submitted", "auto-generated"));
        result.TabKey.Should().Be("inbox:updates");
        result.Sure.Should().BeTrue();
        Sort("no-reply@accounts.google.com", "Security alert: new sign-in on Mac").TabKey.Should().Be("inbox:updates");
        Sort("billing@utility.example", "Your bill is ready").TabKey.Should().Be("inbox:updates");
    }

    [Fact]
    public void AMarketingCampaign_ShouldBePromotions()
    {
        var result = Sort("news@store.example", "Black Friday: 40% off everything",
            ("List-Unsubscribe", "<https://store.example/u>"), ("X-Mailchimp-Campaign", "abc"), ("Precedence", "bulk"));
        result.TabKey.Should().Be("inbox:promotions");
        result.Sure.Should().BeTrue();
        result.Reason.Should().Contain("sale or offer wording");
    }

    [Fact]
    public void AMixedOrWeakSignal_ShouldBeUnclear_ForTheAiToDecide()
    {
        // A newsletter with an unsubscribe link and nothing else: Promotions is only a guess.
        var newsletter = Sort("writer@substack.example", "This week in Georgia politics", ("List-Unsubscribe", "<https://x/u>"));
        newsletter.Sure.Should().BeFalse();
        // A store's order confirmation sent through a marketing platform: Updates and Promotions both score.
        var mixed = Sort("orders@store.example", "Order confirmation - save 10% on your next order",
            ("List-Unsubscribe", "<x>"), ("Feedback-ID", "1:2:3"));
        mixed.Sure.Should().BeFalse();
    }
}
