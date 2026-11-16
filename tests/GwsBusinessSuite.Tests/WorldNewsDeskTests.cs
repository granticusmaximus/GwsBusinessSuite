using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News newsroom N5-N15 (14-11-26): beats, notes with mentions, the source book, two-person
// sign-off, live updates, holds, corrections, dossiers, the handover note, metrics and the morgue.
public sealed class WorldNewsDeskTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wn-desk-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private WorldNewsStore Store() => new(Path.Combine(_folder, "worldnews.db"));

    [Fact]
    public void Beats_ShouldMatchRegionsAndTopics_AndRefuseUnknownOnes()
    {
        var africa = new Beat("b1", "ana", BeatRules.Region, WorldRegions.Africa, Now);
        var cyber = new Beat("b2", "ana", BeatRules.Topic, SituationKinds.Cyber, Now);

        Assert.True(BeatRules.Matches(africa, ["Kenya", "France"], "politics"));
        Assert.False(BeatRules.Matches(africa, ["France"], "politics"));
        Assert.True(BeatRules.Matches(cyber, ["France"], SituationKinds.Cyber));
        Assert.Null(BeatRules.Problem(BeatRules.Region, WorldRegions.Africa));
        Assert.NotNull(BeatRules.Problem(BeatRules.Region, "Narnia"));
        Assert.NotNull(BeatRules.Problem("colour", "red"));
    }

    [Fact]
    public void Beats_ShouldBeKeptPerPerson_WithoutDuplicates()
    {
        var store = Store();

        Assert.NotNull(store.AddBeat("Ana", BeatRules.Region, "Europe", Now));
        Assert.Null(store.AddBeat("ana", BeatRules.Region, "europe", Now));
        var beat = Assert.Single(store.Beats("ana"));
        store.RemoveBeat("ana", beat.Id);
        Assert.Empty(store.Beats("ana"));
    }

    [Theory]
    [InlineData("colour", "red")]
    [InlineData(BeatRules.Region, "Narnia")]
    [InlineData(BeatRules.Topic, "made-up")]
    public void Beats_ShouldRejectInvalidSubscriptions(string kind, string value)
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.AddBeat("ana", kind, value, Now));
        Assert.Empty(store.Beats("ana"));
    }

    [Fact]
    public void BeatQueue_ShouldMatchOnlyMyBeats_OrderByCoverage_AndRespectTheLimit()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        var now = WorldNewsTestStore.Now;
        test.SeedEvent("s1", "e1", "Airport shuts after ransomware attack", ["A", "B", "C"], now.AddHours(-3));
        test.SeedEvent("s2", "e2", "Company confirms data breach", ["A", "B"], now.AddHours(-2));
        test.SeedEvent("s3", "e3", "Parliament passes budget", ["A", "B", "C", "D"], now.AddHours(-2));
        store.SaveKinds([("s1", SituationKinds.Cyber), ("s2", SituationKinds.Cyber),
            ("s3", SituationKinds.Politics)]);
        store.AddBeat("ana", BeatRules.Topic, SituationKinds.Cyber, now);
        store.AddBeat("ben", BeatRules.Topic, SituationKinds.Politics, now);
        var since = now.AddHours(-24);

        Assert.Equal(["e1", "e2"], store.BeatQueue("Ana", since).Select(story => story.Id));
        Assert.Equal("e1", Assert.Single(store.BeatQueue("ana", since, limit: 1)).Id);
        Assert.Equal("e3", Assert.Single(store.BeatQueue("ben", since)).Id);
        Assert.Empty(store.BeatQueue("cat", since));
    }

    [Fact]
    public void BeatAlerts_ShouldRetryAfterCorroboration_AndSendOnlyOnce()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        test.SeedEvent("s1", "e1", "Quake hits Tokyo", ["A", "B"], Now);
        store.AddBeat("ana", BeatRules.Region, WorldRegions.Europe, Now);

        Assert.Empty(store.CreateBeatAlerts(Now, Now.AddDays(-1), mayAlert: _ => false));
        var alert = Assert.Single(store.CreateBeatAlerts(Now.AddMinutes(5), Now.AddDays(-1), mayAlert: _ => true));
        Assert.Equal(("ana", "s1"), (alert.Username, alert.SituationId));
        Assert.Empty(store.CreateBeatAlerts(Now.AddMinutes(10), Now.AddDays(-1), mayAlert: _ => true));
    }

    [Fact]
    public void BeatAlerts_ShouldWaitForAHoldToBeReleased()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        test.SeedEvent("s1", "e1", "Quake hits Tokyo", ["A", "B"], Now);
        store.AddBeat("ana", BeatRules.Region, WorldRegions.Europe, Now);
        store.SetHold(new StoryHold("e1", Now.AddHours(1), "Await the briefing", "ana", Now));

        Assert.Empty(store.CreateBeatAlerts(Now, Now.AddDays(-1)));
        store.ReleaseHold("e1");
        Assert.Single(store.CreateBeatAlerts(Now.AddMinutes(5), Now.AddDays(-1)));
    }

    [Fact]
    public void RemovingAnotherPersonsBeat_ShouldNotResetTheirAlertHistory()
    {
        using var test = new WorldNewsTestStore();
        var store = test.Store;
        test.SeedEvent("s1", "e1", "Quake hits Tokyo", ["A", "B"], Now);
        var beat = store.AddBeat("ana", BeatRules.Region, WorldRegions.Europe, Now)!;
        Assert.Single(store.CreateBeatAlerts(Now, Now.AddDays(-1)));

        store.RemoveBeat("ben", beat.Id);

        Assert.Single(store.Beats("ana"));
        Assert.Empty(store.CreateBeatAlerts(Now.AddMinutes(5), Now.AddDays(-1)));
        store.RemoveBeat("ANA", beat.Id);
        Assert.Empty(store.Beats("ana"));
    }

    [Fact]
    public void DeskNotes_ShouldFindMentions_AndAlertOnlyRealOtherUsers()
    {
        Assert.Equal(["ben", "cat.d"], DeskNotes.Mentions("Checked with @ben and @cat.d; mail me@example.com, not @ben again"));
        var store = Store();

        var (note, alerts) = store.AddDeskNote("e1", "ana", "Ping @ben and @ana and @nobody", Now, knownUser: name => name is "ben" or "ana");

        Assert.Equal("Ping @ben and @ana and @nobody", note.Text);
        var alert = Assert.Single(alerts);
        Assert.Equal("ben", alert.Username);
        Assert.Contains("ana mentioned you", alert.Message);
        Assert.Single(store.DeskNotes("e1"));
    }

    [Fact]
    public void SourceBook_ShouldBePrivateToItsOwner_AndTrackAccuracy()
    {
        var store = Store();
        var contact = store.SaveSourceContact(new SourceContact("", "ana", "J. Doe", "Port authority", "+1 555", 4, "Night shift", Now));

        Assert.Single(store.SourceContacts("ana"));
        Assert.Empty(store.SourceContacts("ben"));
        Assert.True(store.RecordSourceUse("ana", contact.Id, "e1", "held", "Confirmed the closure", Now));
        Assert.True(store.RecordSourceUse("ana", contact.Id, "e2", "wrong", "Gave the wrong day", Now));
        Assert.False(store.RecordSourceUse("ben", contact.Id, "e3", "held", "", Now), "another person can't use my contact");
        Assert.Equal(0.5, SourceBook.Accuracy(store.SourceUses("ana")));
        Assert.Null(SourceBook.Accuracy([new SourceUse(contact.Id, "e9", "unknown", "", Now)]));
        Assert.NotNull(SourceBook.Problem("", 3));
        Assert.NotNull(SourceBook.Problem("X", 6));
    }

    [Fact]
    public void SignOffs_ShouldNeedTwoDifferentPeople_AndChainLikeTheLedger()
    {
        var store = Store();
        Assert.NotNull(SignOffs.Problem([], SignOffs.Verdict, "ana", checklistComplete: false));
        Assert.Null(SignOffs.Problem([], SignOffs.Verdict, "ana", checklistComplete: true));

        var first = store.AddSignOff("e1", SignOffs.Verdict, "ana", "Reported", Now);
        Assert.NotNull(SignOffs.Problem(store.SignOffs("e1"), SignOffs.Verdict, "Ana", true));
        Assert.NotNull(SignOffs.Problem(store.SignOffs("e1"), SignOffs.Publish, "ben", true));
        var second = store.AddSignOff("e1", SignOffs.Verdict, "ben", "Reported", Now.AddMinutes(5));

        Assert.True(SignOffs.IsComplete(store.SignOffs("e1"), SignOffs.Verdict));
        Assert.Null(SignOffs.Problem(store.SignOffs("e1"), SignOffs.Publish, "ana", true));
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.Null(SignOffs.FirstBroken(store.SignOffs()));
        var tampered = store.SignOffs().Select(s => s.Id == first.Id ? s with { Username = "mallory" } : s).ToList();
        Assert.Equal(first.Id, SignOffs.FirstBroken(tampered));
    }

    [Fact]
    public void LiveUpdates_ShouldKeepTheOriginal_WhenCorrected()
    {
        var store = Store();
        var update = store.AddLiveUpdate("e1", "ana", "Bridge closed in both directions", LiveUpdates.Unconfirmed, Now);
        store.CorrectLiveUpdate(update.Id, "Bridge closed northbound only", LiveUpdates.Confirmed, Now.AddMinutes(10));

        var shown = Assert.Single(store.LiveUpdates("e1"));
        Assert.Equal("Bridge closed in both directions", shown.Text);
        Assert.Equal("Bridge closed northbound only", shown.CorrectedText);
        Assert.Equal(LiveUpdates.Confirmed, shown.Status);
        Assert.Equal(("e1", 1), (store.LiveStories()[0].EventId, store.LiveStories()[0].Updates));
        Assert.NotNull(LiveUpdates.Problem("", LiveUpdates.Confirmed));
        Assert.NotNull(LiveUpdates.Problem("x", "maybe"));
    }

    [Fact]
    public void Holds_ShouldStopAlertsForTheWholeSituation_UntilRelease()
    {
        var store = Store();
        var hold = new StoryHold("e1", Now.AddHours(2), "Embargoed until the briefing", "ana", Now);
        store.SetHold(hold);

        Assert.True(Holds.IsHeld(store.Hold("e1"), Now));
        Assert.False(Holds.IsHeld(store.Hold("e1"), Now.AddHours(3)));
        Assert.Contains("Embargoed", Holds.Describe(hold, at => at.ToString("HH:mm")));
        Assert.NotNull(Holds.Problem(Now.AddHours(-1), "late", Now));
        store.ReleaseHold("e1");
        Assert.Null(store.Hold("e1"));
    }

    [Fact]
    public void Dossier_ShouldPinOnceByUrl_AndRefuseUnknownKinds()
    {
        var store = Store();
        var pinned = store.PinToDossier("e1", Dossiers.Report, "BBC: Bridge collapses", "https://bbc.example/a", "First report", "ana", Now);
        var again = store.PinToDossier("e1", Dossiers.Report, "BBC again", "https://bbc.example/a", "", "ben", Now);

        Assert.NotNull(pinned);
        Assert.Equal(pinned!.Id, again!.Id);
        Assert.Null(store.PinToDossier("e1", "video", "x", null, "", "ana", Now));
        Assert.Single(store.Dossier("e1"));
        store.UnpinFromDossier(pinned.Id);
        Assert.Empty(store.Dossier("e1"));
    }

    [Fact]
    public void Metrics_ShouldMeasureTimeToVerdict_AndWhetherVerdictsHeld()
    {
        var store = Store();
        store.SetBudgetItem(new BudgetItem("e1", "Bridge", "ana", "watching", "Verify", Now, "ana"));
        store.SetBudgetItem(new BudgetItem("e1", "Bridge", "ana", "verifying", "Call the council", Now.AddHours(1), "ana"));
        store.SetBudgetItem(new BudgetItem("e1", "Bridge", "ana", "ready", "Publish", Now.AddHours(3), "ana"));
        store.AddSignOff("e1", SignOffs.Verdict, "ana", "Reported", Now.AddHours(3));
        store.AddSignOff("e1", SignOffs.Verdict, "ben", "Reported", Now.AddHours(3));
        store.AppendLabelSnapshots([("e1", "Confirmed", "3 independent sources.", 3, 5)], "v", Now.AddHours(4));

        var metrics = store.DeskMetrics(Now.AddHours(5));

        Assert.Equal(TimeSpan.FromHours(3), metrics.MedianTimeToVerdict);
        Assert.Equal(1, metrics.VerdictsSigned);
        Assert.Equal(1, metrics.VerdictsHeld);
        Assert.Equal(100, metrics.VerdictsHeldPercent);
        Assert.Equal(("ana", 1), metrics.OpenPerPerson[0]);
        Assert.True(DeskMetricsRules.Held("Reported", "Confirmed"));
        Assert.False(DeskMetricsRules.Held("Confirmed", "Disputed"));
        Assert.False(DeskMetricsRules.Held("Confirmed", "SingleSource"));
    }

    [Fact]
    public void Handover_ShouldSayWhatMoved_WhatIsUnconfirmed_AndWhatIsOpen()
    {
        var budget = new[]
        {
            new BudgetItem("e1", "Bridge collapse", "ana", "verifying", "Call the council", Now.AddMinutes(-30), "ana"),
            new BudgetItem("e2", "Port strike", "ben", "watching", "Wait", Now.AddHours(-20), "ben"),
        };
        var labels = new[] { new LabelSnapshot(1, "e1", "SingleSource", "One outlet so far.", 1, 1, "v", Now.AddMinutes(-40), "", "h") };
        var holds = new Dictionary<string, StoryHold> { ["e2"] = new("e2", Now.AddHours(1), "Embargo", "ben", Now) };

        var md = ShiftHandover.Markdown(Now.AddHours(-8), Now, budget, holds, labels, [], [new NewsTip("t", null, null, "Saw smoke near the port", null, Now.AddHours(-1), "new", null, null)],
            [], [], id => id == "e1" ? "Bridge collapse" : null, at => at.ToString("HH:mm"));

        Assert.Contains("**Bridge collapse**: verifying, ana (moved", md);
        Assert.Contains("On hold until", md);
        Assert.Contains("## Still on one source", md);
        Assert.Contains("## New tips", md);
        Assert.DoesNotContain("## Open challenges", md);
    }

    [Fact]
    public void Morgue_ShouldSearchTheDesksRecords_AndOnlyMyContacts()
    {
        var store = Store();
        store.AddDeskNote("e1", "ana", "Harbour master says the crane fell at 06:10", Now);
        store.SaveSourceContact(new SourceContact("", "ana", "Harbour master", "Port of X", "", 4, "", Now));
        store.SaveSourceContact(new SourceContact("", "ben", "Harbour pilot", "Port of X", "", 3, "", Now));
        store.AddCorrection("e1", "Crane", "The crane fell at 06:40, not 06:10.", "ana", Now);

        var hits = store.SearchDesk("harbour", "ana");

        Assert.Contains(hits, h => h.Kind == "note");
        Assert.Contains(hits, h => h.Kind == "contact" && h.Title == "Harbour master");
        Assert.DoesNotContain(hits, h => h.Kind == "contact" && h.Title == "Harbour pilot");
        Assert.Single(store.SearchDesk("06:40", "ana"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
