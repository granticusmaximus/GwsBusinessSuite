using FluentAssertions;
using GwsBusinessSuite.Application.Automation;

namespace GwsBusinessSuite.Tests;

// Every message asserted against here is copied verbatim (or near-verbatim, varying only the
// node/field name interpolation) from a real throw site in AutomationNodeRegistry,
// AutomationHttpClient, CronSchedule, or WikiDatabasePropertyValidation - not an invented
// example of what an error "might" look like. If one of those throw sites' wording ever
// changes, this is the test that should catch the diagnosis silently falling back to Unknown.
public sealed class AutomationFailureDiagnosticsTests
{
    [Theory]
    [InlineData("wiki.appendBlock cannot write to this page - 'grant' (this workflow's owner) does not have Edit access to it.", "Permissions")]
    [InlineData("Add Database Row cannot verify database access right now.", "Permissions")]
    [InlineData("Sentinel: Find Pages could not determine the workflow's owner to check page access.", "Permissions")]
    [InlineData("'internal.example' cannot be resolved.", "Network destination blocked")]
    [InlineData("Workflow HTTP requests cannot target localhost.", "Network destination blocked")]
    [InlineData("An error occurred while sending the request.", "Network error")]
    [InlineData("The operation was canceled.", "Timeout")]
    [InlineData("HTTP response exceeded the 5 MB workflow safety limit.", "Safety limit")]
    [InlineData("Split Out would fan out into 6000 items, which exceeds the 5,000 item safety limit.", "Safety limit")]
    [InlineData("Cron expression 'not a cron expression' must have exactly 5 space-separated fields: minute hour day-of-month month day-of-week.", "Schedule")]
    [InlineData("SKU is required.", "Validation rule")]
    [InlineData("Budget must be at least 100.", "Validation rule")]
    [InlineData("SKU doesn't match the required format.", "Validation rule")]
    [InlineData("This row no longer exists.", "Missing reference")]
    [InlineData("The selected row template no longer exists in this database.", "Missing reference")]
    [InlineData("Execute Workflow would create a recursive sub-workflow cycle - workflow abc123 is already running earlier in this chain.", "Workflow structure")]
    [InlineData("Execute Workflow exceeded the maximum sub-workflow call depth of 10.", "Workflow structure")]
    [InlineData("Sentinel: Create Page requires a title.", "Node configuration")]
    [InlineData("CRM: Save Contact requires a fullName.", "Node configuration")]
    [InlineData("Split Out expected 'items' to be an array.", "Node configuration")]
    [InlineData("Ollama is not available to the automation engine.", "Feature unavailable")]
    [InlineData("Database writes are not available to the automation engine.", "Feature unavailable")]
    public void Diagnose_ShouldCategorizeRealAutomationEngineErrorMessages(string errorMessage, string expectedCategory)
    {
        var diagnosis = AutomationFailureDiagnostics.Diagnose(errorMessage);

        diagnosis.Category.Should().Be(expectedCategory);
        diagnosis.SuggestedFix.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Diagnose_ShouldFallBackToUnknown_ForAnUnrecognizedMessage()
    {
        var diagnosis = AutomationFailureDiagnostics.Diagnose("Something entirely bespoke went wrong in a way nothing here anticipates.");

        diagnosis.Category.Should().Be("Unknown");
        diagnosis.SuggestedFix.Should().Contain("Executions tab");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Diagnose_ShouldReturnUnknown_RatherThanThrow_ForABlankOrMissingMessage(string? errorMessage)
    {
        var diagnosis = AutomationFailureDiagnostics.Diagnose(errorMessage);

        diagnosis.Category.Should().Be("Unknown");
    }

    [Fact]
    public void Diagnose_ShouldBeCaseInsensitive()
    {
        var diagnosis = AutomationFailureDiagnostics.Diagnose("SKU IS REQUIRED.");

        diagnosis.Category.Should().Be("Validation rule");
    }
}
