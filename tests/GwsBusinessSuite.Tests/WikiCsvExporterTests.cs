using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class WikiCsvExporterTests
{
    [Fact]
    public void ExportDatabase_ShouldWriteAHeaderRowAndOneRowPerDatabaseRow()
    {
        var titleProperty = new WikiDatabaseProperty { Name = "Name", Type = WikiDatabasePropertyTypes.Title, SortOrder = 0 };
        var statusProperty = new WikiDatabaseProperty { Name = "Status", Type = WikiDatabasePropertyTypes.Text, SortOrder = 1 };
        var database = new WikiDatabase
        {
            Title = "Tasks",
            Properties = [statusProperty, titleProperty], // deliberately out of SortOrder to prove ordering is applied
            Rows =
            [
                new WikiDatabaseRow
                {
                    SortOrder = 0,
                    PropertyValuesJson = $$"""{"{{titleProperty.Id}}":"Ship it","{{statusProperty.Id}}":"Done"}"""
                }
            ]
        };

        var csv = WikiCsvExporter.ExportDatabase(database);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().Be("\"Name\",\"Status\"");
        lines[1].Should().Be("\"Ship it\",\"Done\"");
    }

    [Fact]
    public void ExportDatabase_ShouldQuoteEmbeddedCommasAndQuotesAndDefeatFormulaInjection()
    {
        var titleProperty = new WikiDatabaseProperty { Name = "Name", Type = WikiDatabasePropertyTypes.Title, SortOrder = 0 };
        var database = new WikiDatabase
        {
            Title = "Rows",
            Properties = [titleProperty],
            Rows =
            [
                new WikiDatabaseRow { SortOrder = 0, PropertyValuesJson = $$"""{"{{titleProperty.Id}}":"a, \"quoted\" value"}""" },
                new WikiDatabaseRow { SortOrder = 1, PropertyValuesJson = $$"""{"{{titleProperty.Id}}":"=SUM(A1:A2)"}""" }
            ]
        };

        var csv = WikiCsvExporter.ExportDatabase(database);

        csv.Should().Contain("\"a, \"\"quoted\"\" value\"");
        csv.Should().Contain("\"'=SUM(A1:A2)\"");
    }
}
