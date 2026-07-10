namespace GwsBusinessSuite.Application.Resume;

public sealed record ResumeExperience(
    string Title,
    string Company,
    string DateRange,
    IReadOnlyList<string> Bullets,
    IReadOnlyList<string> Technologies);

public sealed record ResumeEducation(string Degree, string School, string DateLabel);

// Hardcoded like GrantWatsonHomepageTemplate - this is Grant's own resume content, not
// admin-editable through the CMS, so it lives here as a single source of truth for both
// the /resume HTML page and the /resume.pdf download.
public static class ResumeContent
{
    public const string FullName = "Grant Watson";
    public const string Title = "Software Engineer & Business Analyst";
    public const string Email = "grant@gwsapp.net";
    public const string Phone = "478-733-5230";
    public const string Location = "Kathleen, GA";
    public const string LinkedInUrl = "https://www.linkedin.com/in/grantwatsonfullstack/";
    public const string GitHubUrl = "https://github.com/granticusmaximus";

    public const string Summary =
        "Full-stack software developer with experience since 2017 across federal, tribal, and " +
        "private-sector teams, building ASP.NET, VB.NET, and JavaScript applications. Current " +
        "business-analysis work at Unum adds a strong requirements-to-delivery lens on top of " +
        "hands-on engineering.";

    public static readonly IReadOnlyList<ResumeExperience> Experience =
    [
        new(
            "Business Developer II",
            "Unum",
            "Jul 2025 – Present",
            [
                "Analyze business rules and regulatory requirements governing print distribution processes",
                "Use internal tooling to route print distributions to the correct parcel handlers",
                "Keep distribution decisions compliant with applicable regulations",
            ],
            []),
        new(
            "Full Stack Developer",
            "Cherokee Nation",
            "Feb 2023 – Apr 2025",
            [
                "Maintained a USGS acquisition application, including Entity Framework model updates",
                "Built monthly SSRS reports used internally for audits",
                "Delivered new RESTful APIs from updated client requirements",
                "Managed Agile sprints and CI/CD pipeline development",
                "Provided ongoing user support",
            ],
            ["ASP.NET", "GitLab", "Azure", "Jira", "JavaScript", "SQL"]),
        new(
            "Software Developer",
            "RMCI",
            "Feb 2021 – Feb 2022",
            [
                "Maintained VB.NET applications for USAStaffing.gov and USAHiring.gov",
                "Ran Agile sprint planning and quality assurance testing",
                "Completed peer reviews on Git branch merges for production updates",
            ],
            ["VB.NET", "Knockout.js", "AWS", "TFS", "JavaScript", "SQL"]),
        new(
            "Full Stack Developer",
            "Rocky Mountain Arsenal",
            "Oct 2020 – Jan 2021",
            [
                "Migrated legacy ASP and Java projects to ASP.NET 5 with Blazor",
                "Gathered requirements directly from wildlife refuge staff",
                "Developed handset software for use with limited internet access",
                "Built a CRUD application for office maintenance tracking",
            ],
            []),
        new(
            "Full Stack Developer",
            "Department of Commerce",
            "May 2019 – May 2020",
            [
                "Collaborated with T-Mobile and Sprint on a 5G crowdsourcing application for first responders",
                "Used LLMs for text analytics, generation, and data visualization within the application",
            ],
            []),
        new(
            "Full Stack Developer",
            "Department of Defense",
            "Nov 2017 – Apr 2019",
            [
                "Worked across three Air Force contracts",
                "Developed a Learning Management System platform for Air Force Reserves",
                "Built a monthly status report application with React, ASP.NET, and SQL",
                "Collaborated with DBAs on SQL entities and Redis cache configuration",
            ],
            ["React", "ASP.NET", "SQL", "Redis"]),
    ];

    public static readonly ResumeEducation Education =
        new("B.S. Information Technology", "Middle Georgia State University", "May 2018");

    public static readonly IReadOnlyList<string> Skills =
    [
        "C#", "ASP.NET", "Blazor", "VB.NET", "Classic ASP", "JavaScript", "React", "Knockout.js",
        "SQL", "Python", "SSIS/SSRS", "Azure DevOps", "TFS", "GitHub", "GitLab", "Jira",
    ];
}
