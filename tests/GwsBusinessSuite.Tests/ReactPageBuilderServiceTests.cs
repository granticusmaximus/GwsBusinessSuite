using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace GwsBusinessSuite.Tests;

public sealed class ReactPageBuilderServiceTests
{
    [Fact]
    public async Task GetPublishStatusAsync_ShouldReturnStatusWithoutThrowing()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var service = fixture.CreateService();
            await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "README.md"), "outside-react");

            await service.SaveAsync(new ReactPageSaveRequest
            {
                PageKey = "About",
                Elements =
                [
                    new VisualBuilderElement { ElementType = "heading", Text = $"Changed {Guid.NewGuid():N}" }
                ]
            });

            var status = await service.GetPublishStatusAsync();

            Assert.False(string.IsNullOrWhiteSpace(status.CurrentBranch));
            Assert.NotNull(status.ChangedFiles);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ListReactPagesAsync_ShouldDiscoverRoutesAndFiles()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var service = fixture.CreateService();

            var pages = await service.ListReactPagesAsync();

            Assert.Contains(pages, x => x.PageKey == "Home" && x.RoutePath == "/");
            Assert.Contains(pages, x => x.PageKey == "About" && x.RoutePath == "/about");
            Assert.DoesNotContain(pages, x => x.PageKey == "AdminRedirect");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldWriteManagedReactPage_WhenAutoGitDisabled()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var service = fixture.CreateService();

            var result = await service.SaveAsync(new ReactPageSaveRequest
            {
                PageKey = "About",
                Elements =
                [
                    new VisualBuilderElement { ElementType = "heading", Text = "About Heading" },
                    new VisualBuilderElement { ElementType = "paragraph", Text = "About body" }
                ]
            });

            Assert.True(result.SavedToFile);
            Assert.False(result.GitAutoPushAttempted);

            var aboutFile = await File.ReadAllTextAsync(Path.Combine(fixture.PagesPath, "About.jsx"));
            Assert.Contains("GWS_VISUAL_BUILDER_JSON_START", aboutFile);
            Assert.Contains("About Heading", aboutFile);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_ShouldReturnPublishSummary_WhenRemoteIsNotConfigured()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var service = fixture.CreateService(new CmsBuilderOptions
            {
                ReactAppRelativePath = "apps/public-site",
                AutoGitPushOnSave = false,
                GitCommitPrefix = "cms-builder",
                GitPublishRemoteName = string.Empty,
                GitPublishBranch = "main",
                RequireGithubTokenForPush = true,
                GithubTokenEnvironmentVariable = "GWS_GITHUB_TOKEN"
            });

            await service.SaveAsync(new ReactPageSaveRequest
            {
                PageKey = "About",
                Elements =
                [
                    new VisualBuilderElement { ElementType = "paragraph", Text = $"Publish Me {Guid.NewGuid():N}" }
                ]
            });

            var result = await service.PublishAsync(new ReactPublishRequest
            {
                CommitMessage = "cms-builder publish about"
            });

            Assert.False(string.IsNullOrWhiteSpace(result.Summary));
            Assert.NotNull(result.PublishedFiles);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task LoadEditorStateAsync_ShouldDiscoverCoLocatedCssFile()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            // Write a co-located CSS file for the Home page
            var homeCssPath = Path.Combine(fixture.PagesPath, "Home.css");
            await File.WriteAllTextAsync(homeCssPath, ".home { color: red; }");

            var service = fixture.CreateService();
            var state = await service.LoadEditorStateAsync("Home");

            Assert.NotNull(state);
            Assert.Contains(state.UiFiles, f => f.FileName == "Home.css" && f.FileType == "css" && !f.IsThemeFile);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task LoadEditorStateAsync_ShouldDiscoverGlobalThemeFiles()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            // Write a global index.css theme file
            var srcDir = Path.Combine(fixture.RootPath, "apps", "public-site", "src");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "index.css"), ":root { --color-primary: #2563eb; }");

            var service = fixture.CreateService();
            var state = await service.LoadEditorStateAsync("Home");

            Assert.NotNull(state);
            Assert.Contains(state.UiFiles, f => f.FileName == "index.css" && f.IsThemeFile);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task LoadEditorStateAsync_ShouldDiscoverImportedCssFromJsxImport()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            // Write an imported CSS file and update the JSX to import it
            var homeCssPath = Path.Combine(fixture.PagesPath, "Home.module.css");
            await File.WriteAllTextAsync(homeCssPath, ".hero { font-size: 2rem; }");

            var homeJsx = "import styles from './Home.module.css';\nconst Home = () => <div className={styles.hero}>Home</div>; export default Home;";
            await File.WriteAllTextAsync(Path.Combine(fixture.PagesPath, "Home.jsx"), homeJsx);

            var service = fixture.CreateService();
            var state = await service.LoadEditorStateAsync("Home");

            Assert.NotNull(state);
            Assert.Contains(state.UiFiles, f => f.FileName == "Home.module.css" && f.FileType == "css");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadFileContentAsync_ShouldReturnFileContent()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var cssPath = Path.Combine(fixture.PagesPath, "Home.css");
            const string expectedContent = ".home { color: blue; }";
            await File.WriteAllTextAsync(cssPath, expectedContent);

            var service = fixture.CreateService();
            var content = await service.ReadFileContentAsync(cssPath);

            Assert.Equal(expectedContent, content);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadFileContentAsync_ShouldReturnEmptyString_WhenFileDoesNotExist()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var service = fixture.CreateService();
            var content = await service.ReadFileContentAsync("/nonexistent/path/file.css");

            Assert.Equal(string.Empty, content);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task SaveFileContentAsync_ShouldWriteContentToFile()
    {
        var fixture = await TestFixture.CreateAsync();
        try
        {
            var cssPath = Path.Combine(fixture.PagesPath, "Home.css");
            const string newContent = ".home { background: #f0f4ff; }";

            var service = fixture.CreateService();
            await service.SaveFileContentAsync(cssPath, newContent);

            var written = await File.ReadAllTextAsync(cssPath);
            Assert.Equal(newContent, written);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed class TestFixture : IDisposable
    {
        public string RootPath { get; }
        public string WebContentRoot { get; }
        public string PagesPath { get; }

        private TestFixture(string rootPath)
        {
            RootPath = rootPath;
            WebContentRoot = Path.Combine(rootPath, "src", "GwsBusinessSuite.Web");
            PagesPath = Path.Combine(rootPath, "apps", "public-site", "src", "pages");
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "gws-react-page-builder-tests", Guid.NewGuid().ToString("N"));
            var fixture = new TestFixture(rootPath);

            Directory.CreateDirectory(fixture.WebContentRoot);
            Directory.CreateDirectory(fixture.PagesPath);
            Directory.CreateDirectory(Path.Combine(rootPath, "apps", "public-site", "src"));

            var appFile = Path.Combine(rootPath, "apps", "public-site", "src", "App.jsx");
            await File.WriteAllTextAsync(appFile,
                "import Home from './pages/Home';\nimport About from './pages/About';\nimport AdminRedirect from './pages/AdminRedirect';\n" +
                "function App(){ return (<Routes><Route path=\"/\" element={<Home />} /><Route path=\"/about\" element={<About />} /><Route path=\"/admin/*\" element={<AdminRedirect />} /></Routes>); }\nexport default App;");

            await File.WriteAllTextAsync(Path.Combine(fixture.PagesPath, "Home.jsx"), "const Home = () => <div>Home</div>; export default Home;");
            await File.WriteAllTextAsync(Path.Combine(fixture.PagesPath, "About.jsx"), "const About = () => <div>About</div>; export default About;");
            await File.WriteAllTextAsync(Path.Combine(fixture.PagesPath, "AdminRedirect.jsx"), "const AdminRedirect = () => null; export default AdminRedirect;");

            await RunGitAsync(fixture.RootPath, "init");
            await RunGitAsync(fixture.RootPath, "config user.email \"tests@example.com\"");
            await RunGitAsync(fixture.RootPath, "config user.name \"Tests\"");
            await RunGitAsync(fixture.RootPath, "add .");
            await RunGitAsync(fixture.RootPath, "commit -m \"initial\"");
            await RunGitAsync(fixture.RootPath, "branch -M main");

            return fixture;
        }

        public ReactPageBuilderService CreateService(CmsBuilderOptions? cmsBuilderOptions = null)
        {
            var hostEnvironment = new FakeHostEnvironment
            {
                ContentRootPath = WebContentRoot,
                ApplicationName = "Tests",
                EnvironmentName = Environments.Development,
                ContentRootFileProvider = new NullFileProvider()
            };

            var options = Options.Create(cmsBuilderOptions ?? new CmsBuilderOptions
            {
                ReactAppRelativePath = "apps/public-site",
                AutoGitPushOnSave = false,
                GitCommitPrefix = "cms-builder",
                GitPublishRemoteName = "origin",
                GitPublishBranch = "main",
                RequireGithubTokenForPush = true,
                GithubTokenEnvironmentVariable = "GWS_GITHUB_TOKEN"
            });

            return new ReactPageBuilderService(hostEnvironment, options);
        }

        private static async Task RunGitAsync(string workingDirectory, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {arguments} failed. Output: {stdOut} {stdErr}");
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }
}
