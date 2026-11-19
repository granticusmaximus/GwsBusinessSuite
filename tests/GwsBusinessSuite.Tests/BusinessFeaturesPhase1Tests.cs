using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

// 19-11-26, five business features, phase 1: weekly restore drills, loose ends, and checks before
// a page goes live.
public sealed class BusinessFeaturesPhase1Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RestoreDrill_ShouldRunWeekly_AgainAfterAFailure_AndBeKeptOnDisk()
    {
        RestoreDrill.IsDue(null, Now).Should().BeTrue("no drill yet");
        RestoreDrill.IsDue(new(Now.AddDays(-2), "a", true, "ok"), Now).Should().BeFalse();
        RestoreDrill.IsDue(new(Now.AddDays(-8), "a", true, "ok"), Now).Should().BeTrue();
        RestoreDrill.IsDue(new(Now.AddHours(-1), "a", false, "bad key"), Now).Should().BeTrue("a failed drill is tried again with the next backup");

        var folder = Path.Combine(Path.GetTempPath(), $"drill-{Guid.NewGuid():N}");
        try
        {
            RestoreDrill.Read(folder).Should().BeNull();
            RestoreDrill.Write(folder, new RestoreDrillRecord(Now, "gws-backup-x.gwsbackup", true, "12 files"));
            RestoreDrill.Read(folder).Should().Be(new RestoreDrillRecord(Now, "gws-backup-x.gwsbackup", true, "12 files"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void LooseEnds_ShouldListWhatSlipped_AndNothingThatDidnt()
    {
        var acme = new Contact { FullName = "Ann Lee", Company = "Acme", FollowUpDate = Now.AddDays(-2) };
        var calm = new Contact { FullName = "Bo Ray", FollowUpDate = Now.AddDays(3) };
        var gone = new Contact { FullName = "Old", FollowUpDate = Now.AddDays(-9), TrashedAt = Now.AddDays(-1) };
        var quiet = new Deal { ContactId = acme.Id, Title = "Retainer", Stage = DealStages.ProposalSent, ValueUsd = 5000, CreatedAt = Now.AddDays(-60) };
        var lively = new Deal { ContactId = calm.Id, Title = "Site", Stage = DealStages.Lead, CreatedAt = Now.AddDays(-60) };
        var won = new Deal { ContactId = acme.Id, Title = "Won one", Stage = DealStages.Won, CreatedAt = Now.AddDays(-90) };
        var activity = new ContactActivity { ContactId = calm.Id, Note = "Called", CreatedAt = Now.AddDays(-5) };
        var overdue = new Invoice { ContactId = acme.Id, Title = "INV-7", Status = InvoiceStatuses.Sent, DueDate = Now.AddDays(-10),
            LineItems = [new InvoiceLineItem { Description = "Work", Quantity = 2, UnitPriceUsd = 600 }] };
        var paid = new Invoice { ContactId = acme.Id, Title = "INV-6", Status = InvoiceStatuses.Paid, DueDate = Now.AddDays(-30), PaidAt = Now.AddDays(-29) };
        var silent = new SupportTicket { ContactId = calm.Id, Subject = "Login broken", Status = SupportTicketStatuses.Open, CreatedAt = Now.AddDays(-4) };
        var fresh = new SupportTicket { ContactId = calm.Id, Subject = "Question", Status = SupportTicketStatuses.Open, CreatedAt = Now.AddHours(-3) };

        var ends = LooseEnds.Build([acme, calm, gone], [quiet, lively, won], [activity], [overdue, paid], [silent, fresh], Now);

        ends.Select(e => e.Kind).Should().Equal(LooseEndKinds.FollowUp, LooseEndKinds.Invoice, LooseEndKinds.Ticket, LooseEndKinds.QuietDeal);
        ends[0].Title.Should().Be("Ann Lee (Acme)");
        ends[1].Detail.Should().Contain("$1,200").And.Contain("10 days ago").And.Contain("no reminder");
        ends[2].Title.Should().StartWith("Login broken");
        ends[3].Title.Should().StartWith("Retainer").And.NotContain("Site", "a deal with recent CRM activity isn't quiet");
    }

    [Fact]
    public void PrePublishReview_ShouldCatchMistakes_AndPassAGoodPage()
    {
        const string good = """<h1>Hello</h1><h2>Part</h2><h3>Detail</h3><img src="a.jpg" alt="A view"><img src="b.jpg" alt=""><a href="/about">About us</a>""";
        PrePublishReview.Review(good, "Hello", "A short description.").Should().BeEmpty();

        const string bad = """<h2>No main</h2><h4>Jump</h4><img src="a.jpg"><a href="#">Click</a><a href="/x"><i class="bi bi-x"></i></a>""";
        var issues = PrePublishReview.Review(bad, "", null).Select(i => i.Check).ToList();
        issues.Should().Contain(["Title", "Description", "Images", "Headings", "Links"]);
        PrePublishReview.Review(bad, "", null).Should().Contain(i => i.Message.Contains("jumps from H2 to H4"))
            .And.Contain(i => i.Message.Contains("no text a screen reader"));
        PrePublishReview.Review(good, new string('t', 70), "ok").Should().ContainSingle(i => i.Check == "Title");
    }
}
