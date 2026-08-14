using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class WikiDatabaseAiFieldTests
{
    [Fact]
    public async Task SavePropertyAsync_ShouldRoundTripThePromptTemplateAndModel()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");

        var property = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField,
            AiPromptTemplate = "Summarize [Notes] in one sentence.",
            AiModel = "llama3.1"
        }, "u");

        var config = WikiDatabasePropertyConfig.Parse(property);
        config.AiPromptTemplate.Should().Be("Summarize [Notes] in one sentence.");
        config.AiModel.Should().Be("llama3.1");
    }

    [Fact]
    public async Task SaveInlineCellAsync_ShouldRejectDirectWritesToAnAiField()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var aiProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField,
            AiPromptTemplate = "x",
            AiModel = "llama3.1"
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var act = () => service.SaveInlineCellAsync(database.Id, row.Id, aiProperty.Id, "manual value", "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateAiFieldValueAsync_ShouldResolveReferencedPropertiesAndPersistTheResult()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService { Response = "A concise generated summary." };
        var service = new WikiDatabaseService(db, ollama: ollama);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var notesProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Notes", Type = WikiDatabasePropertyTypes.Text }, "u");
        var summaryProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField,
            AiPromptTemplate = "Summarize [Notes] for the task titled [Name].",
            AiModel = "llama3.1"
        }, "u");

        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, "Ship the feature");
        WikiPropertyValues.SetText(values, notesProperty.Id, "Needs a final review before release.");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { Values = values.ToDictionary(kv => kv.Key, kv => kv.Value) }, "u");

        var snapshot = await service.GenerateAiFieldValueAsync(database.Id, row.Id, summaryProperty.Id, "u");

        ollama.LastUserPrompt.Should().Contain("Needs a final review before release.").And.Contain("Ship the feature");
        ollama.LastModel.Should().Be("llama3.1");
        var reloadedRow = snapshot.Rows.Single(r => r.Id == row.Id);
        var reloadedCell = reloadedRow.Cells.Single(cell => cell.PropertyId == summaryProperty.Id);
        reloadedCell.Value.Should().Be("A concise generated summary.");
    }

    [Fact]
    public async Task GenerateAiFieldValueAsync_ValueShouldSurviveAFollowUpRowEdit()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService { Response = "Generated value" };
        var service = new WikiDatabaseService(db, ollama: ollama);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var summaryProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField,
            AiPromptTemplate = "Say hi.",
            AiModel = "llama3.1"
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");
        await service.GenerateAiFieldValueAsync(database.Id, row.Id, summaryProperty.Id, "u");

        var editValues = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(editValues, titleProperty.Id, "Renamed task");
        await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            Id = row.Id,
            Values = editValues.ToDictionary(kv => kv.Key, kv => kv.Value)
        }, "u");

        var reloaded = await service.GetDatabaseAsync(database.Id);
        var reloadedValues = WikiPropertyValues.ParseObject(reloaded!.Rows.Single(r => r.Id == row.Id).PropertyValuesJson);
        WikiPropertyValues.GetText(reloadedValues, summaryProperty.Id).Should().Be("Generated value");
    }

    [Fact]
    public async Task GenerateAiFieldValueAsync_ShouldThrow_WhenOllamaIsUnavailable()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db); // no ollama supplied
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var summaryProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField,
            AiPromptTemplate = "x",
            AiModel = "llama3.1"
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var act = () => service.GenerateAiFieldValueAsync(database.Id, row.Id, summaryProperty.Id, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateAiFieldValueAsync_ShouldThrow_WhenNoPromptIsConfigured()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService { Response = "x" };
        var service = new WikiDatabaseService(db, ollama: ollama);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var summaryProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField
        }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var act = () => service.GenerateAiFieldValueAsync(database.Id, row.Id, summaryProperty.Id, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateAiFieldValueAsync_ShouldThrow_ForANonAiFieldProperty()
    {
        await using var db = await CreateDbAsync();
        var ollama = new FakeOllamaService { Response = "x" };
        var service = new WikiDatabaseService(db, ollama: ollama);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var textProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Notes", Type = WikiDatabasePropertyTypes.Text }, "u");
        var row = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "u");

        var act = () => service.GenerateAiFieldValueAsync(database.Id, row.Id, textProperty.Id, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RenamingAReferencedProperty_ShouldUpdateTheAiPromptTemplate()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "u");
        var notesProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor { Name = "Notes", Type = WikiDatabasePropertyTypes.Text }, "u");
        var summaryProperty = await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Summary",
            Type = WikiDatabasePropertyTypes.AiField,
            AiPromptTemplate = "Summarize [Notes] briefly.",
            AiModel = "llama3.1"
        }, "u");

        await service.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Id = notesProperty.Id,
            Name = "Details",
            Type = WikiDatabasePropertyTypes.Text
        }, "u");

        var reloaded = await service.GetDatabaseAsync(database.Id);
        var reloadedSummary = reloaded!.Properties.Single(p => p.Id == summaryProperty.Id);
        WikiDatabasePropertyConfig.Parse(reloadedSummary).AiPromptTemplate.Should().Be("Summarize [Details] briefly.");
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeOllamaService : IOllamaService
    {
        public string Response { get; set; } = string.Empty;
        public string? LastModel { get; private set; }
        public string? LastUserPrompt { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastModel = model;
            LastUserPrompt = userPrompt;
            return Task.FromResult(Response);
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
