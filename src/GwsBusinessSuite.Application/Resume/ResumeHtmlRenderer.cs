using System.Net;
using System.Text;

namespace GwsBusinessSuite.Application.Resume;

// Renders the public /resume page body - a hand-authored page (not a Canvas/CMS page)
// since it needs the "Download CV" link to /resume.pdf alongside the same content
// ResumePdfService renders into the PDF. Wrapped in PublicSiteHtmlRenderer.Layout by
// the /resume route in Program.cs, same as the blog list/post pages.
public static class ResumeHtmlRenderer
{
    public static string Body()
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            <main class="resume-page">
              <header class="resume-header">
                <div>
                  <h1>{{Html(ResumeContent.FullName)}}</h1>
                  <p class="resume-title">{{Html(ResumeContent.Title)}}</p>
                </div>
                <div class="gws-button-wrap">
                  <a href="/resume.pdf" class="btn btn-primary" download>Download CV</a>
                </div>
              </header>
              <div class="resume-contact-bar">
                <span><i class="bi bi-envelope" aria-hidden="true"></i> {{Html(ResumeContent.Email)}}</span>
                <span><i class="bi bi-telephone" aria-hidden="true"></i> {{Html(ResumeContent.Phone)}}</span>
                <span><i class="bi bi-geo-alt" aria-hidden="true"></i> {{Html(ResumeContent.Location)}}</span>
                <a href="{{Html(ResumeContent.LinkedInUrl)}}" target="_blank" rel="noopener noreferrer"><i class="bi bi-linkedin" aria-hidden="true"></i> LinkedIn</a>
                <a href="{{Html(ResumeContent.GitHubUrl)}}" target="_blank" rel="noopener noreferrer"><i class="bi bi-github" aria-hidden="true"></i> GitHub</a>
              </div>
              <section class="resume-summary">
                <p class="gws-paragraph">{{Html(ResumeContent.Summary)}}</p>
              </section>
              <section class="resume-section">
                <h2>Experience</h2>
                <div class="resume-timeline">
            """);

        foreach (var job in ResumeContent.Experience)
        {
            var techHtml = job.Technologies.Count == 0
                ? ""
                : $"""<div class="resume-entry-tech">{string.Concat(job.Technologies.Select(t => $"""<span class="article-tag">{Html(t)}</span>"""))}</div>""";

            sb.Append($$"""
                  <div class="resume-entry">
                    <div class="resume-entry-header">
                      <h3>{{Html(job.Title)}}</h3>
                      <span class="resume-entry-company">{{Html(job.Company)}}</span>
                      <span class="resume-entry-dates">{{Html(job.DateRange)}}</span>
                    </div>
                    <ul class="resume-entry-bullets">
                """);
            foreach (var bullet in job.Bullets)
            {
                sb.Append($"<li>{Html(bullet)}</li>");
            }
            sb.Append("</ul>");
            sb.Append(techHtml);
            sb.Append("</div>");
        }

        sb.Append($$"""
                </div>
              </section>
              <section class="resume-section">
                <h2>Education</h2>
                <div class="resume-entry">
                  <div class="resume-entry-header">
                    <h3>{{Html(ResumeContent.Education.Degree)}}</h3>
                    <span class="resume-entry-company">{{Html(ResumeContent.Education.School)}}</span>
                    <span class="resume-entry-dates">{{Html(ResumeContent.Education.DateLabel)}}</span>
                  </div>
                </div>
              </section>
              <section class="resume-section">
                <h2>Skills</h2>
                <div class="resume-skills-list">
            """);
        foreach (var skill in ResumeContent.Skills)
        {
            sb.Append($"""<span class="article-tag">{Html(skill)}</span>""");
        }
        sb.Append("""
                </div>
              </section>
            </main>
            """);

        return sb.ToString();
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
