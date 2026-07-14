using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Resume;

namespace GwsBusinessSuite.Tests;

public sealed class ResumeHtmlRendererTests
{
    [Fact]
    public void Body_ShouldIncludeCoreContactAndSummaryFields()
    {
        var html = ResumeHtmlRenderer.Body();

        html.Should().Contain(ResumeContent.FullName);
        // Body() HTML-encodes every value (see ArticleMarkdownRenderer's equivalent
        // convention) - the resume title contains "&", so it round-trips as "&amp;".
        html.Should().Contain(WebUtility.HtmlEncode(ResumeContent.Title));
        html.Should().Contain(ResumeContent.Email);
        html.Should().Contain(ResumeContent.Phone);
        html.Should().Contain(ResumeContent.Location);
        html.Should().Contain(ResumeContent.Summary);
    }

    [Fact]
    public void Body_ShouldIncludeEveryExperienceEntry_TitleCompanyAndBullets()
    {
        var html = ResumeHtmlRenderer.Body();

        foreach (var job in ResumeContent.Experience)
        {
            html.Should().Contain(job.Title);
            html.Should().Contain(job.Company);
            html.Should().Contain(job.DateRange);
            foreach (var bullet in job.Bullets)
            {
                html.Should().Contain(bullet);
            }
        }
    }

    [Fact]
    public void Body_ShouldIncludeEducationAndSkills()
    {
        var html = ResumeHtmlRenderer.Body();

        html.Should().Contain(ResumeContent.Education.Degree);
        html.Should().Contain(ResumeContent.Education.School);
        foreach (var skill in ResumeContent.Skills)
        {
            html.Should().Contain(skill);
        }
    }

    [Fact]
    public void Body_ShouldLinkToDownloadablePdf()
    {
        var html = ResumeHtmlRenderer.Body();

        html.Should().Contain("href=\"/resume.pdf\"");
    }

    [Fact]
    public void Body_ShouldBeStable_AndProduceTheSameOutputOnRepeatedCalls()
    {
        // Body() is pure/static with no external state, so it should be safely cacheable
        // and callable from multiple requests without any shared mutable state leaking
        // between them.
        var first = ResumeHtmlRenderer.Body();
        var second = ResumeHtmlRenderer.Body();

        first.Should().Be(second);
    }
}
