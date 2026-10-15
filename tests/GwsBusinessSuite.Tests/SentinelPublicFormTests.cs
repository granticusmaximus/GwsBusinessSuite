using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel item 32: public forms from a database.
public sealed class SentinelPublicFormTests
{
    private static async Task<ApplicationDbContext> DbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed record Fixture(
        ApplicationDbContext Db, WikiDatabaseService Databases, SentinelAccessService Access, SentinelPublicFormService Forms,
        WikiDatabase Database, WikiDatabaseView View,
        WikiDatabaseProperty Name, WikiDatabaseProperty Kind, WikiDatabaseProperty Budget, WikiDatabaseProperty Rush,
        WikiDatabaseProperty Deadline, WikiDatabaseProperty Topics, WikiDatabaseProperty Owner, WikiDatabaseProperty Internal);

    // A "Requests" database: Name (title), Kind (select: project/question), Budget (number, only
    // asked for projects), Rush (checkbox, only for projects), Deadline (date, only when Rush is
    // ticked), Topics (multi-select), Owner (person - never public), Internal notes (not asked).
    private static async Task<Fixture> FixtureAsync()
    {
        var db = await DbAsync();
        var databases = new WikiDatabaseService(db);
        var access = new SentinelAccessService(db);
        var database = await databases.CreateDatabaseAsync("Requests", null, "grant");
        var name = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        async Task<WikiDatabaseProperty> Add(string propertyName, string type, IReadOnlyList<WikiDatabasePropertyOption>? options = null) =>
            await databases.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = propertyName, Type = type, Options = options ?? [] }, "grant");
        var kind = await Add("Kind", WikiDatabasePropertyTypes.Select, [new("project", "Project", "blue"), new("question", "Question", "gray")]);
        var budget = await Add("Budget", WikiDatabasePropertyTypes.Number);
        var rush = await Add("Rush", WikiDatabasePropertyTypes.Checkbox);
        var deadline = await Add("Deadline", WikiDatabasePropertyTypes.Date);
        var topics = await Add("Topics", WikiDatabasePropertyTypes.MultiSelect, [new("web", "Web", "blue"), new("seo", "SEO", "green")]);
        var owner = await Add("Owner", WikiDatabasePropertyTypes.Person);
        var internalNotes = await Add("Internal notes", WikiDatabasePropertyTypes.Text);
        var settings = new WikiDatabaseFormSettings(
            Title: "Start a request", ThankYouMessage: "Thanks!",
            HiddenPropertyIds: [internalNotes.Id.ToString()],
            RequiredPropertyIds: [name.Id.ToString(), budget.Id.ToString()],
            Conditions:
            [
                new(budget.Id.ToString(), kind.Id.ToString(), WikiDatabaseFormOperators.Is, "project"),
                new(rush.Id.ToString(), kind.Id.ToString(), WikiDatabaseFormOperators.Is, "project"),
                new(deadline.Id.ToString(), rush.Id.ToString(), WikiDatabaseFormOperators.IsChecked)
            ]);
        var view = await databases.SaveViewAsync(database.Id, null, "Intake", WikiDatabaseViewTypes.Form,
            WikiDatabaseViewConfig.Empty with { FormSettings = settings }, "grant");
        var reloaded = (await databases.GetDatabaseAsync(database.Id))!;
        return new Fixture(db, databases, access, new SentinelPublicFormService(access, databases), reloaded, view,
            name, kind, budget, rush, deadline, topics, owner, internalNotes);
    }

    private static Dictionary<string, IReadOnlyList<string>> Answers(params (WikiDatabaseProperty Property, string[] Values)[] answers) =>
        answers.ToDictionary(answer => answer.Property.Id.ToString(), answer => (IReadOnlyList<string>)answer.Values);

    private static WikiDatabaseFormSettings Settings(Fixture f) => WikiDatabaseViewConfigJson.Parse(f.View.ConfigJson).FormSettings!;

    [Fact]
    public async Task Questions_ShouldBeTheAnswerableAskedProperties_TitleFirst()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        SentinelPublicForm.Questions(f.Database, Settings(f)).Select(p => p.Name)
            .Should().Equal("Name", "Kind", "Budget", "Rush", "Deadline", "Topics");
    }

    [Fact]
    public async Task Conditions_ShouldDecideWhichAnswersCount_AndWhichAreRequired()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        var settings = Settings(f);

        // A question: Budget is required but not asked, so it isn't required; a stray Budget,
        // Deadline and Owner answer are all dropped.
        var (values, errors) = SentinelPublicForm.BuildRow(f.Database, settings, Answers(
            (f.Name, ["  How do I log in?  "]), (f.Kind, ["question"]), (f.Budget, ["5000"]), (f.Deadline, ["2026-12-01"]), (f.Owner, ["grant"])));
        errors.Should().BeEmpty();
        WikiPropertyValues.GetText(values, f.Name.Id).Should().Be("How do I log in?");
        values.ContainsKey(f.Budget.Id.ToString()).Should().BeFalse();
        values.ContainsKey(f.Deadline.Id.ToString()).Should().BeFalse();
        values.ContainsKey(f.Owner.Id.ToString()).Should().BeFalse();

        // A project: Budget is now asked and required; Deadline only once Rush is ticked.
        SentinelPublicForm.BuildRow(f.Database, settings, Answers((f.Name, ["Site"]), (f.Kind, ["project"]))).Errors
            .Should().Equal("\"Budget\" is required.");
        (values, errors) = SentinelPublicForm.BuildRow(f.Database, settings, Answers(
            (f.Name, ["Site"]), (f.Kind, ["project"]), (f.Budget, ["2500.50"]), (f.Rush, ["true"]), (f.Deadline, ["2026-12-01"]), (f.Topics, ["web", "seo", "web"])));
        errors.Should().BeEmpty();
        WikiPropertyValues.GetNumber(values, f.Budget.Id).Should().Be(2500.50m);
        WikiPropertyValues.GetCheckbox(values, f.Rush.Id).Should().BeTrue();
        WikiPropertyValues.GetDate(values, f.Deadline.Id).Should().Be(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        WikiPropertyValues.GetMultiSelect(values, f.Topics.Id).Should().Equal("web", "seo");
    }

    [Fact]
    public async Task BuildRow_ShouldRejectBadValues()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        var (_, errors) = SentinelPublicForm.BuildRow(f.Database, Settings(f), Answers(
            (f.Name, [new string('x', SentinelPublicForm.MaxAnswerLength + 1)]), (f.Kind, ["project"]), (f.Budget, ["lots"]),
            (f.Rush, ["true"]), (f.Deadline, ["next week"]), (f.Topics, ["web", "<script>"])));
        errors.Should().Equal("\"Name\" is too long.", "\"Budget\" must be a number.", "\"Deadline\" must be a date.",
            "\"Topics\" has a choice that isn't on the list.");
        SentinelPublicForm.BuildRow(f.Database, Settings(f), Answers((f.Name, ["x"]), (f.Kind, ["urgent"]))).Errors
            .Should().Equal("\"Kind\" has a choice that isn't on the list.");
    }

    [Fact]
    public void IsShown_ShouldTreatAConditionLoopAsHidden()
    {
        var database = new WikiDatabase { Id = Guid.NewGuid(), Title = "Loop", CreatedBy = "u" };
        WikiDatabaseProperty Text(string name) => new() { Id = Guid.NewGuid(), WikiDatabaseId = database.Id, Name = name, Type = WikiDatabasePropertyTypes.Text, CreatedBy = "u" };
        var a = Text("A");
        var b = Text("B");
        database.Properties = [a, b];
        var settings = new WikiDatabaseFormSettings(Conditions:
        [
            new(a.Id.ToString(), b.Id.ToString(), WikiDatabaseFormOperators.IsNotEmpty),
            new(b.Id.ToString(), a.Id.ToString(), WikiDatabaseFormOperators.IsNotEmpty)
        ]);
        var questions = SentinelPublicForm.Questions(database, settings);
        var answers = Answers((a, ["x"]), (b, ["y"]));
        SentinelPublicForm.IsShown(a, questions, settings, answers).Should().BeFalse();
        SentinelPublicForm.IsShown(b, questions, settings, answers).Should().BeFalse();
    }

    [Fact]
    public async Task AnonymousSubmission_ShouldCreateARow_OnlyThroughAFormLink()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        var share = await f.Access.CreatePublicShareAsync(f.Database.Id, true, null, false, null, "grant", formViewId: f.View.Id);

        var form = (await f.Forms.GetAsync(share.PublicToken!))!;
        form.Title.Should().Be("Start a request");
        form.Settings.ThankYouMessage.Should().Be("Thanks!");

        var result = await f.Forms.SubmitAsync(share.PublicToken!, Answers((f.Name, ["Need a website"]), (f.Kind, ["project"]), (f.Budget, ["4000"])));
        result.Saved.Should().BeTrue();
        var row = await f.Db.WikiDatabaseRows.AsNoTracking().SingleAsync(item => item.WikiDatabaseId == f.Database.Id);
        row.CreatedBy.Should().Be(SentinelPublicForm.SubmittedBy);
        WikiPropertyValues.GetNumber(WikiPropertyValues.ParseObject(row.PropertyValuesJson), f.Budget.Id).Should().Be(4000m);
        (await f.Access.GetAccessAsync(f.Database.Id, true)).Shares.Single().ViewCount.Should().Be(1, "it counts responses");

        (await f.Forms.SubmitAsync(share.PublicToken!, Answers((f.Kind, ["question"])))).Errors.Should().Equal("\"Name\" is required.");

        // A plain database link isn't a form, and a revoked form link stops working.
        var databaseShare = await f.Access.CreatePublicShareAsync(f.Database.Id, true, null, false, null, "grant");
        (await f.Forms.GetAsync(databaseShare.PublicToken!)).Should().BeNull();
        await f.Access.RevokePublicShareAsync(share.Id, "grant");
        (await f.Forms.SubmitAsync(share.PublicToken!, Answers((f.Name, ["Again"])))).Errors.Should().Equal("This form is no longer available.");
        (await f.Db.WikiDatabaseRows.CountAsync(item => item.WikiDatabaseId == f.Database.Id)).Should().Be(1);
    }

    [Fact]
    public async Task PropertyValidation_ShouldStillApply_EvenToQuestionsTheFormDoesNotAsk()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        await f.Databases.SavePropertyAsync(f.Database.Id, new WikiDatabasePropertyEditor
        {
            Id = f.Internal.Id, Name = f.Internal.Name, Type = f.Internal.Type, IsRequired = true
        }, "grant");
        var share = await f.Access.CreatePublicShareAsync(f.Database.Id, true, null, false, null, "grant", formViewId: f.View.Id);

        var result = await f.Forms.SubmitAsync(share.PublicToken!, Answers((f.Name, ["Hi"]), (f.Kind, ["question"])));

        result.Saved.Should().BeFalse();
        result.Errors.Single().Should().Contain("Internal notes");
    }

    [Fact]
    public async Task FormLink_ShouldOnlyBelongToADatabase()
    {
        var f = await FixtureAsync();
        await using var _ = f.Db;
        var act = () => f.Access.CreatePublicShareAsync(Guid.NewGuid(), false, null, false, null, "grant", formViewId: f.View.Id);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
