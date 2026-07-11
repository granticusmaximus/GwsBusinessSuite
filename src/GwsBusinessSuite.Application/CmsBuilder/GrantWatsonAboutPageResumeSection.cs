using GwsBusinessSuite.Application.Resume;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Merges the resume/CV content (see ResumeContent/ResumeHtmlRenderer) into the "about"
// CmsPage as a Canvas section, instead of the resume living on its own /resume page.
// Idempotent like GrantWatsonHomepageTemplate: only appends the section if the about
// page doesn't already have one, so it never clobbers edits made through the admin
// CMS builder UI on subsequent app restarts.
public static class GrantWatsonAboutPageResumeSection
{
    public const string SectionLabel = "Resume";

    public static bool HasResumeSection(PageLayout? layout) =>
        layout is not null && layout.Sections.Any(section => section.Label == SectionLabel);

    public static LayoutSection Build() =>
        new()
        {
            Label = SectionLabel,
            Background = "transparent",
            Padding = "none",
            ColumnLayout = "full",
            Columns =
            [
                new LayoutColumn
                {
                    Widgets =
                    [
                        new LayoutWidget
                        {
                            WidgetType = "html",
                            Props = new Dictionary<string, string>
                            {
                                ["content"] = ResumeHtmlRenderer.Body()
                            }
                        }
                    ]
                }
            ]
        };
}
