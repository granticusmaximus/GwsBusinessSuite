using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public static class GrantWatsonHomepageTemplate
{
    public const string SiteSlug = "grantwatson-dev";
    public const string MetaTitle = "Grant Watson | Full-Stack C# Developer for Web, Desktop, and Business Apps";
    public const string MetaDescription = "C#-heavy full-stack software development for ASP.NET, WPF, VB.NET, JavaScript, inventory systems, internal tools, dashboards, reporting, and business automation.";

    private const string RecentArticlesBlockHtml = """
        <section class="home-blog" id="recent-writing">
          <div class="home-blog-header">
            <div>
              <h2>Recent blog posts</h2>
              <p class="gws-paragraph">A few recent notes on software development, tooling, and building software that solves real business problems.</p>
            </div>
            <a href="/blog" class="view-all">View all articles</a>
          </div>
          <div class="home-blog-grid" data-home-blog-grid>
            <article class="article-card">
              <div class="article-card-body">
                <div class="article-card-title">Loading recent posts…</div>
                <p class="article-card-desc">Pulling in the latest writing from the blog.</p>
              </div>
            </article>
          </div>
          <div class="gws-button-wrap" style="margin-top:1.5rem">
            <a href="/blog" class="btn btn-ghost">See all blog articles</a>
          </div>
        </section>
        <script>
        (function () {
          var grid = document.querySelector('[data-home-blog-grid]');
          if (!grid) return;

          function esc(value) {
            return String(value || '')
              .replace(/&/g, '&amp;')
              .replace(/</g, '&lt;')
              .replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;')
              .replace(/'/g, '&#39;');
          }

          function renderEmpty(message) {
            grid.innerHTML = '<article class="article-card"><div class="article-card-body"><div class="article-card-title">' + esc(message) + '</div><p class="article-card-desc">Visit the blog for the full archive.</p><div class="article-card-meta"><span>grantwatson.dev</span></div></div></article>';
          }

          fetch('/api/blog')
            .then(function (response) {
              if (!response.ok) throw new Error('load_failed');
              return response.json();
            })
            .then(function (articles) {
              var items = Array.isArray(articles) ? articles.slice(0, 5) : [];
              if (items.length === 0) {
                renderEmpty('No articles published yet.');
                return;
              }

              grid.innerHTML = items.map(function (article) {
                var description = article.metaDescription || '';
                if (description.length > 125) {
                  description = description.slice(0, 125) + '…';
                }

                var dateLabel = '';
                if (article.publishedAt) {
                  var published = new Date(article.publishedAt);
                  if (!isNaN(published.getTime())) {
                    dateLabel = published.toLocaleDateString('en-US', { month: 'short', year: 'numeric' });
                  }
                }

                var imageHtml = article.heroImageUrl
                  ? '<img src="' + esc(article.heroImageUrl) + '" alt="' + esc(article.title) + '" class="article-card-img" loading="lazy" />'
                  : '<div class="article-card-img-placeholder">No image</div>';

                var metaBits = [];
                if (article.estimatedReadingTime) metaBits.push('<span>' + esc(article.estimatedReadingTime) + ' read</span>');
                if (article.primaryKeyword) metaBits.push('<span class="article-tag">' + esc(article.primaryKeyword) + '</span>');
                if (dateLabel) metaBits.push('<span>' + esc(dateLabel) + '</span>');

                return '<a href="/blog/' + esc(article.slug) + '" class="article-card">' +
                  imageHtml +
                  '<div class="article-card-body">' +
                    '<div class="article-card-title">' + esc(article.title) + '</div>' +
                    (description ? '<p class="article-card-desc">' + esc(description) + '</p>' : '') +
                    '<div class="article-card-meta">' + metaBits.join('') + '</div>' +
                  '</div>' +
                '</a>';
              }).join('');
            })
            .catch(function () {
              renderEmpty('Recent posts are temporarily unavailable.');
            });
        }());
        </script>
        """;

    public static string CreateBlocksJson() => CmsBuilderJson.Serialize(CreateLayout());

    public static bool ShouldApplyTemplate(CmsPage? page)
    {
        if (page is null || page.TrashedAt is not null)
        {
            return true;
        }

        var layout = CmsBuilderJson.ParseLayout(page.BlocksJson);
        if (layout is null || layout.Sections.Count == 0)
        {
            return true;
        }

        var widgets = layout.Sections
            .SelectMany(section => section.Columns)
            .SelectMany(column => column.Widgets)
            .ToList();

        if (widgets.Count == 0)
        {
            return true;
        }

        var legacyHero = widgets.FirstOrDefault(widget => widget.WidgetType == "hero");
        var legacyGithubButton = widgets.FirstOrDefault(widget =>
            widget.WidgetType == "button"
            && widget.Props.TryGetValue("href", out var href)
            && href.Contains("github.com/granticusmaximus", StringComparison.OrdinalIgnoreCase));

        return widgets.Count <= 3
            && legacyHero?.Props.TryGetValue("headline", out var headline) == true
            && string.Equals(headline, "Grant Watson", StringComparison.Ordinal)
            && legacyGithubButton is not null;
    }

    public static PageLayout CreateLayout() =>
        new()
        {
            Sections =
            [
                Section(
                    label: "Hero",
                    background: "transparent",
                    padding: "lg",
                    columnLayout: "full",
                    Column(
                        HeroWidget(
                            headline: "C#-heavy software development for the apps your business actually needs.",
                            subline: "I build ASP.NET, WPF, VB.NET, and JavaScript applications for internal teams, customer experiences, inventory workflows, and line-of-business operations. My recent business analyst experience at Unum also helps me translate requirements into software that works in the real world.",
                            cta1Label: "Start a Project",
                            cta1Href: "/contact",
                            cta2Label: "Read the Blog",
                            cta2Href: "/blog"),
                        ButtonWidget("View Resume", "/resume", "outline-secondary"))),

                Section(
                    label: "Intro",
                    background: "transparent",
                    padding: "md",
                    columnLayout: "full",
                    Column(
                        HeadingWidget("What I build", "h2"),
                        ParagraphWidget("From web applications and admin portals to desktop tools and operational software, I focus on software that helps teams work faster, serve customers better, and reduce manual friction."))),

                Section(
                    label: "Capabilities Row One",
                    background: "transparent",
                    padding: "sm",
                    columnLayout: "thirds",
                    Column(CardWidget("ASP.NET and API Development", "Build or extend ASP.NET Core, Blazor, Razor, and API-driven applications for internal or customer-facing use.")),
                    Column(CardWidget("WPF and Desktop Tools", "Create desktop applications and internal utilities for teams that need fast native workflows and strong keyboard-first experiences.")),
                    Column(CardWidget("VB.NET Maintenance and Modernization", "Stabilize legacy VB.NET systems, ship needed changes, and move the right pieces forward without unnecessary rewrites."))),

                Section(
                    label: "Capabilities Row Two",
                    background: "transparent",
                    padding: "sm",
                    columnLayout: "thirds",
                    Column(CardWidget("JavaScript Apps and UI Work", "Deliver dashboards, frontend interfaces, and interaction-heavy features with modern JavaScript stacks when the project calls for it.")),
                    Column(CardWidget("Inventory and Operations Software", "Build inventory management, reporting, workflow tracking, and line-of-business systems around how your team actually operates.")),
                    Column(CardWidget("Integrations and Automation", "Handle imports, exports, API integrations, scheduled jobs, and business process automation that ties systems together."))),

                Section(
                    label: "Services",
                    background: "accent",
                    padding: "lg",
                    columnLayout: "half-half",
                    Column(
                        HeadingWidget("Simple software work I can take on", "h2"),
                        ParagraphWidget("Not every engagement needs a full product build. I can also help with focused development tasks, maintenance work, and internal improvements that make an existing system more useful.")),
                    Column(
                        RichTextWidget("""
                            - Bug fixes and production issue triage
                            - New CRUD screens and internal admin tools
                            - Dashboard, reporting, and export workflows
                            - Database-backed portals and operational utilities
                            - API endpoints and third-party integrations
                            - Legacy .NET cleanup, refactors, and modernization
                            - Data migration and automation scripts
                            - Requirements-to-delivery translation with a business analyst lens
                            """))),

                Section(
                    label: "Business Context",
                    background: "transparent",
                    padding: "md",
                    columnLayout: "full",
                    Column(
                        CardWidget("Business-minded delivery", "My newer experience at Unum as a business analyst sharpens how I scope work, understand reporting and operational needs, and turn ambiguous requirements into software teams can actually use."))),

                Section(
                    label: "Recent Writing",
                    background: "transparent",
                    padding: "none",
                    columnLayout: "full",
                    Column(HtmlWidget(RecentArticlesBlockHtml))),

                Section(
                    label: "Contact CTA",
                    background: "dark",
                    padding: "lg",
                    columnLayout: "full",
                    Column(
                        HeadingWidget("Need something built, fixed, or modernized?", "h2", "center"),
                        ParagraphWidget("If you need a full-stack developer who can work across .NET, desktop, web, integrations, and business workflows, start with the contact page and tell me what you need.", "center"),
                        ButtonWidget("Go to Contact Page", "/contact", "primary", "center"))),
            ]
        };

    private static LayoutSection Section(string label, string background, string padding, string columnLayout, params LayoutColumn[] columns) =>
        new()
        {
            Label = label,
            Background = background,
            Padding = padding,
            ColumnLayout = columnLayout,
            Columns = columns.ToList()
        };

    private static LayoutColumn Column(params LayoutWidget[] widgets) =>
        new()
        {
            Widgets = widgets.ToList()
        };

    private static LayoutWidget HeroWidget(string headline, string subline, string cta1Label, string cta1Href, string cta2Label, string cta2Href) =>
        new()
        {
            WidgetType = "hero",
            Props = new Dictionary<string, string>
            {
                ["headline"] = headline,
                ["subline"] = subline,
                ["cta1Label"] = cta1Label,
                ["cta1Href"] = cta1Href,
                ["cta2Label"] = cta2Label,
                ["cta2Href"] = cta2Href,
                ["align"] = "left"
            }
        };

    private static LayoutWidget HeadingWidget(string text, string level, string align = "left") =>
        new()
        {
            WidgetType = "heading",
            Props = new Dictionary<string, string>
            {
                ["text"] = text,
                ["level"] = level,
                ["align"] = align
            }
        };

    private static LayoutWidget ParagraphWidget(string text, string align = "left") =>
        new()
        {
            WidgetType = "paragraph",
            Props = new Dictionary<string, string>
            {
                ["text"] = text,
                ["align"] = align
            }
        };

    private static LayoutWidget ButtonWidget(string label, string href, string variant, string align = "left") =>
        new()
        {
            WidgetType = "button",
            Props = new Dictionary<string, string>
            {
                ["label"] = label,
                ["href"] = href,
                ["variant"] = variant,
                ["align"] = align
            }
        };

    private static LayoutWidget CardWidget(string title, string body) =>
        new()
        {
            WidgetType = "card",
            Props = new Dictionary<string, string>
            {
                ["title"] = title,
                ["body"] = body
            }
        };

    private static LayoutWidget RichTextWidget(string content) =>
        new()
        {
            WidgetType = "richtext",
            Props = new Dictionary<string, string>
            {
                ["content"] = content
            }
        };

    private static LayoutWidget HtmlWidget(string content) =>
        new()
        {
            WidgetType = "html",
            Props = new Dictionary<string, string>
            {
                ["content"] = content
            }
        };
}
