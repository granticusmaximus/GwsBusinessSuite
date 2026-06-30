using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class DomainEntityTests
{
    [Fact]
    public void SeoArticleDraft_Should_Default_To_Draft_Status()
    {
        var draft = new SeoArticleDraft
        {
            Topic = "Blazor Architecture",
            TargetAudience = "Mid-level .NET developers"
        };

        draft.Status.Should().Be("Draft");
        draft.ArticleMarkdown.Should().BeEmpty();
        draft.Id.Should().NotBeEmpty();
    }
}
