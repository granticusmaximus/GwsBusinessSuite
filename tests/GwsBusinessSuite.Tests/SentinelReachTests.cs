using System.Net;
using System.Text.Json.Nodes;
using System.Xml;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Infrastructure.Services.Reach;
using GwsBusinessSuite.Infrastructure.Services.SentinelAgent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

// SentinelGPT's reach (05-11-26): Agent-Reach's functions written here. Payloads are trimmed from
// the live APIs on 05-11-26.
public sealed class SentinelReachTests
{
    private static ReachService Reach(FakeWeb web, ReachOptions? options = null, IOllamaWebSearchService? pages = null)
    {
        var factory = new Factory(web);
        var settings = Options.Create(options ?? new ReachOptions());
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new ReachService(
        [
            new GitHubReach(factory, settings), new YouTubeReach(factory, settings), new HackerNewsReach(factory),
            new RedditReach(factory, settings, cache), new BlueskyReach(factory, settings, cache), new MastodonReach(factory),
            new PodcastReach(factory), new XReach(factory, settings)
        ], NullLogger<ReachService>.Instance, pages);
    }

    [Theory]
    [InlineData("https://github.com/Panniantong/Agent-Reach", "github")]
    [InlineData("https://github.com/koala73/worldmonitor/pull/12", "github")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=4", "youtube")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "youtube")]
    [InlineData("https://news.ycombinator.com/item?id=47582482", "hackernews")]
    [InlineData("https://www.reddit.com/r/selfhosted/comments/abc123/a_title/", "reddit")]
    [InlineData("https://bsky.app/profile/bsky.app/post/3mzlal2hpds2u", "bluesky")]
    [InlineData("https://mastodon.social/@mcc/117566360676548588", "mastodon")]
    [InlineData("https://podcasts.apple.com/us/podcast/hard-fork/id1528594034", "podcasts")]
    [InlineData("https://x.com/jack/status/20", "x")]
    [InlineData("https://twitter.com/jack/status/20", "x")]
    [InlineData("https://github.com/settings/tokens", null)]
    [InlineData("https://www.youtube.com/@somechannel", null)]
    [InlineData("https://news.ycombinator.com/news", null)]
    [InlineData("https://example.com/@someone/12", null)]
    [InlineData("https://www.bbc.co.uk/news", null)]
    public void Links_ShouldGoToTheRightChannel(string link, string? channel) =>
        Reach(new FakeWeb()).ChannelFor(new Uri(link))?.Id.Should().Be(channel, link);

    [Fact]
    public async Task GitHub_ShouldReadARepositoryWithItsReadme_AndSendTheTokenWhenSet()
    {
        var web = new FakeWeb()
            .Answer("https://api.github.com/repos/Panniantong/Agent-Reach/readme", "# Agent Reach\nGive your agent eyes.")
            .Answer("https://api.github.com/repos/Panniantong/Agent-Reach", """
                {"full_name":"Panniantong/Agent-Reach","html_url":"https://github.com/Panniantong/Agent-Reach","description":"Give your AI agent eyes","stargazers_count":109191,"forks_count":9000,"open_issues_count":40,"language":"Python","license":{"spdx_id":"MIT"},"pushed_at":"2026-11-05T11:07:58Z","created_at":"2026-01-02T00:00:00Z","topics":["agent","cli"],"owner":{"login":"Panniantong"}}
                """);
        var reach = Reach(web, new ReachOptions { GitHubToken = "ghp_secret" });

        var repo = await reach.ReadAsync("https://github.com/Panniantong/Agent-Reach", default);

        repo.Title.Should().Be("Panniantong/Agent-Reach");
        repo.Text.Should().StartWith("Give your AI agent eyes").And.Contain("README:\n# Agent Reach");
        repo.Facts.Should().Contain(new KeyValuePair<string, string>("stars", "109,191")).And.Contain(new KeyValuePair<string, string>("licence", "MIT"))
            .And.Contain(new KeyValuePair<string, string>("last pushed", "05-11-26")).And.Contain(new KeyValuePair<string, string>("topics", "agent, cli"));
        web.Requests.Should().OnlyContain(r => r.Authorization == "Bearer ghp_secret");
        web.Requests.Single(r => r.Url.EndsWith("/readme", StringComparison.Ordinal)).Accept.Should().Be("application/vnd.github.raw+json");
        reach.Channels.Single(c => c.Id == "github").Needs.Should().BeNull("the token is set");
    }

    [Fact]
    public async Task GitHub_ShouldReadAPullRequestWithItsComments()
    {
        var web = new FakeWeb()
            .Answer("https://api.github.com/repos/koala73/worldmonitor/issues/12/comments", """[{"user":{"login":"ada"},"body":"Looks good"},{"user":{"login":"vercel[bot]","type":"Bot"},"body":"Preview ready"},{"user":{"login":"bob"},"body":"Merged"}]""")
            .Answer("https://api.github.com/repos/koala73/worldmonitor/issues/12", """
                {"title":"Add GDACS","html_url":"https://github.com/koala73/worldmonitor/pull/12","state":"closed","comments":2,"body":"Adds the UN feed","user":{"login":"koala73"},"created_at":"2026-10-01T00:00:00Z","pull_request":{},"labels":[{"name":"data"}]}
                """);

        var pull = await Reach(web).ReadAsync("https://github.com/koala73/worldmonitor/pull/12", default);

        pull.Title.Should().Be("koala73/worldmonitor#12: Add GDACS");
        pull.Facts!["kind"].Should().Be("pull request");
        pull.Facts["labels"].Should().Be("data");
        pull.Comments!.Select(c => $"{c.Author}: {c.Text}").Should().Equal(["ada: Looks good", "bob: Merged"], "bots' comments are left out");
        web.Requests.Should().OnlyContain(r => r.Authorization == null, "no token is set");
    }

    [Fact]
    public async Task HackerNews_ShouldKeepEachReplyUnderTheCommentItAnswers_AsPlainText()
    {
        var web = new FakeWeb().Answer("https://hn.algolia.com/api/v1/items/47582482", """
            {"id":47582482,"title":"Ollama is now powered by MLX","author":"redundantly","created_at":"2026-03-31T03:40:45.000Z","points":648,"url":"https://ollama.com/blog/mlx","text":null,
             "children":[
               {"author":"babblingfish","text":"LLMs on device is the future. It&#x27;s more secure","children":[{"author":"reply1","text":"<p>Agreed</p>","children":[]}]},
               {"author":"deleted","text":null,"children":[]},
               {"author":"third","text":"Fast on M4","children":[]}]}
            """);

        var story = await Reach(web).ReadAsync("https://news.ycombinator.com/item?id=47582482", default);

        story.Title.Should().Be("Ollama is now powered by MLX");
        story.Facts!["links to"].Should().Be("https://ollama.com/blog/mlx");
        story.Comments!.Select(c => (c.Author, c.Text, c.Depth)).Should().Equal(
            ("babblingfish", "LLMs on device is the future. It's more secure", 0), ("reply1", "Agreed", 1), ("third", "Fast on M4", 0));
    }

    [Fact]
    public async Task YouTube_ShouldUseOEmbedWithoutAKey_AndTheDataApiWithOne()
    {
        var web = new FakeWeb()
            .Answer("https://www.youtube.com/oembed", """{"title":"Never Gonna Give You Up","author_name":"Rick Astley"}""")
            .Answer("https://www.googleapis.com/youtube/v3/videos", """
                {"items":[{"snippet":{"title":"Never Gonna Give You Up","channelTitle":"Rick Astley","publishedAt":"2009-10-25T06:57:33Z","description":"The official video"},"contentDetails":{"duration":"PT3M33S"},"statistics":{"viewCount":"1700000000","likeCount":"18000000"}}]}
                """);

        var plain = await Reach(web).ReadAsync("https://youtu.be/dQw4w9WgXcQ", default);
        plain.Author.Should().Be("Rick Astley");
        plain.Note.Should().Contain("Reach:YouTubeApiKey").And.Contain("No transcript");
        web.Requests.Should().ContainSingle().Which.Url.Should().Contain("oembed?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DdQw4w9WgXcQ");

        var full = await Reach(web, new ReachOptions { YouTubeApiKey = "yt-key" }).ReadAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ", default);
        full.Text.Should().Be("The official video");
        full.Facts.Should().Contain(new KeyValuePair<string, string>("length", "3 min 33 s")).And.Contain(new KeyValuePair<string, string>("views", "1,700,000,000"));
        web.Requests.Last().Url.Should().Contain("id=dQw4w9WgXcQ").And.Contain("key=yt-key");
        YouTubeReach.Length("PT1H2M3S").Should().Be("1 h 02 min");

        await Reach(web).Invoking(r => r.SearchAsync("youtube", "rick", null, default))
            .Should().ThrowAsync<ReachException>().WithMessage("Searching YouTube needs Reach:YouTubeApiKey*");
    }

    [Fact]
    public async Task Reddit_ShouldAskForNothingWithoutItsKeys_AndSignInAsAnAppWithThem()
    {
        var none = new FakeWeb();
        await Reach(none).Invoking(r => r.ReadAsync("https://www.reddit.com/r/selfhosted/comments/abc123/a_title/", default))
            .Should().ThrowAsync<ReachException>().WithMessage("Reddit only answers apps that sign in*Reach:RedditClientId*");
        none.Requests.Should().BeEmpty("Reddit refuses anonymous readers, so it isn't asked");

        var web = new FakeWeb()
            .Answer("https://www.reddit.com/api/v1/access_token", """{"access_token":"app-token","expires_in":86400}""")
            .Answer("https://oauth.reddit.com/comments/abc123", """
                [{"data":{"children":[{"kind":"t3","data":{"title":"My homelab","author":"ada","subreddit":"selfhosted","selftext":"Pictures inside","url":"https://www.reddit.com/r/selfhosted/comments/abc123/my_homelab/","permalink":"/r/selfhosted/comments/abc123/my_homelab/","score":321,"num_comments":2,"created_utc":1793800000.0}}]}},
                 {"data":{"children":[{"kind":"t1","data":{"author":"bob","body":"Nice rack","replies":{"data":{"children":[{"kind":"t1","data":{"author":"ada","body":"Thanks","replies":""}}]}}}},{"kind":"more","data":{}}]}}]
                """);
        var reach = Reach(web, new ReachOptions { RedditClientId = "id", RedditClientSecret = "secret" });

        var post = await reach.ReadAsync("https://redd.it/abc123", default);

        post.Title.Should().Be("My homelab");
        post.Url.Should().Be("https://www.reddit.com/r/selfhosted/comments/abc123/my_homelab/");
        post.Facts!["subreddit"].Should().Be("r/selfhosted");
        post.Facts.Should().NotContainKey("links to", "a text post links to itself");
        post.Comments!.Select(c => (c.Author, c.Text, c.Depth)).Should().Equal(("u/bob", "Nice rack", 0), ("u/ada", "Thanks", 1));
        web.Requests[0].Should().Match<Seen>(r => r.Method == "POST" && r.Authorization == "Basic aWQ6c2VjcmV0" && r.Body == "grant_type=client_credentials");
        web.Requests[1].Should().Match<Seen>(r => r.Authorization == "Bearer app-token" && r.UserAgent == "web:GwsBusinessSuite:1.0 (+https://www.gwsapp.net)");

        await reach.ReadAsync("https://www.reddit.com/r/selfhosted/comments/abc123/x/", default);
        web.Requests.Count(r => r.Url.Contains("access_token", StringComparison.Ordinal)).Should().Be(1, "the app token is reused until it expires");
    }

    [Fact]
    public async Task Bluesky_ShouldReadAThreadWithoutAnAccount_ButNeedAnAppPasswordToSearch()
    {
        var web = new FakeWeb().Answer("https://public.api.bsky.app/xrpc/app.bsky.feed.getPostThread", """
            {"thread":{"post":{"uri":"at://did:plc:b5ly/app.bsky.feed.post/3mzlal2hpds2u","author":{"handle":"whysharksmatter.bsky.social","displayName":"David"},"record":{"text":"Scientists of BlueSky, introduce yourselves","createdAt":"2026-11-05T10:00:00Z"},"likeCount":5,"repostCount":1,"replyCount":1},
             "replies":[{"post":{"author":{"handle":"marine.bsky.social"},"record":{"text":"Marine biologist here"}},"replies":[]}]}}
            """);
        var reach = Reach(web);

        var thread = await reach.ReadAsync("https://bsky.app/profile/whysharksmatter.bsky.social/post/3mzlal2hpds2u", default);

        thread.Text.Should().Be("Scientists of BlueSky, introduce yourselves");
        thread.Url.Should().Be("https://bsky.app/profile/whysharksmatter.bsky.social/post/3mzlal2hpds2u");
        thread.Comments!.Single().Should().Be(new ReachComment("@marine.bsky.social", "Marine biologist here"));
        web.Requests.Single().Url.Should().Contain("uri=at%3A%2F%2Fwhysharksmatter.bsky.social%2Fapp.bsky.feed.post%2F3mzlal2hpds2u");
        await reach.Invoking(r => r.SearchAsync("bluesky", "sharks", null, default))
            .Should().ThrowAsync<ReachException>().WithMessage("*Reach:BlueskyAppPassword*");
    }

    [Fact]
    public async Task Mastodon_ShouldReadAPostAndItsReplies_AndHandOtherSitesToTheWebReader()
    {
        var web = new FakeWeb()
            .Answer("https://mastodon.social/api/v1/statuses/117566360676548588/context", """{"descendants":[{"account":{"acct":"ada@hachyderm.io"},"content":"<p>Same here</p>"}]}""")
            .Answer("https://mastodon.social/api/v1/statuses/117566360676548588", """
                {"id":"117566360676548588","url":"https://mastodon.social/@mcc/117566360676548588","content":"<p>Hello <b>fediverse</b></p>","created_at":"2026-11-05T09:00:00Z","favourites_count":3,"reblogs_count":1,"replies_count":1,"account":{"acct":"mcc","display_name":"mcc"}}
                """);
        var pages = new FakePages();
        var reach = Reach(web, pages: pages);

        var post = await reach.ReadAsync("https://mastodon.social/@mcc/117566360676548588", default);
        post.Text.Should().Be("Hello fediverse");
        post.Comments!.Single().Should().Be(new ReachComment("@ada@hachyderm.io", "Same here"));
        web.Requests.Should().OnlyContain(r => r.Client == ReachHttp.Open, "the server is named by the link, so the guarded client is used");

        var other = await reach.ReadAsync("https://blog.example.org/@writer/1234567", default);
        other.Channel.Should().Be("Web", "it wasn't a Mastodon server after all");
        pages.Fetched.Should().Equal("https://blog.example.org/@writer/1234567");

        await Reach(new FakeWeb()).Invoking(r => r.ReadAsync("https://www.bbc.co.uk/news", default))
            .Should().ThrowAsync<ReachException>().WithMessage("*needs OllamaWeb:ApiKey*");
    }

    [Fact]
    public async Task Podcasts_ShouldReadAnEpisodesPublishedTranscript_FromTheShowsOwnFeed()
    {
        var web = new FakeWeb()
            .Answer("https://itunes.apple.com/lookup", """
                {"results":[
                  {"wrapperType":"track","kind":"podcast","collectionName":"Hard Fork","artistName":"The New York Times","feedUrl":"https://feeds.example.com/hardfork","collectionViewUrl":"https://podcasts.apple.com/us/podcast/hard-fork/id1528594034","primaryGenreName":"Technology","trackCount":300},
                  {"wrapperType":"podcastEpisode","trackId":1000700,"trackName":"AI Agents Everywhere","releaseDate":"2026-11-01T10:00:00Z","trackTimeMillis":3600000,"description":"This week, agents.","episodeUrl":"https://cdn.example.com/ep700.mp3","trackViewUrl":"https://podcasts.apple.com/us/podcast/ai-agents/id1528594034?i=1000700"}]}
                """)
            .Answer("https://feeds.example.com/hardfork", """
                <?xml version="1.0"?><rss xmlns:podcast="https://podcastindex.org/namespace/1.0"><channel><item><title>AI Agents Everywhere</title>
                <enclosure url="https://cdn.example.com/ep700.mp3" type="audio/mpeg"/>
                <podcast:transcript url="https://cdn.example.com/ep700.vtt" type="text/vtt"/></item></channel></rss>
                """)
            .Answer("https://cdn.example.com/ep700.vtt", "WEBVTT\n\n1\n00:00:00.000 --> 00:00:02.000\n<v Kevin>Hello and welcome.\n\n2\n00:00:02.000 --> 00:00:04.000\nHello and welcome.\n\n3\n00:00:04.000 --> 00:00:06.000\nToday, agents.\n");
        var reach = Reach(web);

        var episode = await reach.ReadAsync("https://podcasts.apple.com/us/podcast/ai-agents/id1528594034?i=1000700", default);
        episode.Title.Should().Be("Hard Fork: AI Agents Everywhere");
        episode.Text.Should().Be("Transcript:\nHello and welcome. Today, agents.");
        episode.Note.Should().BeNull();
        web.Requests.Where(r => !r.Url.StartsWith("https://itunes.apple.com", StringComparison.Ordinal)).Should().OnlyContain(r => r.Client == ReachHttp.Open);

        var show = await reach.ReadAsync("https://podcasts.apple.com/us/podcast/hard-fork/id1528594034", default);
        show.Text.Should().Contain("01-11-26: AI Agents Everywhere (60 min)");

        FluentActions.Invoking(() => PodcastReach.Xml("<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><rss>&x;</rss>"))
            .Should().Throw<XmlException>("feeds can't pull in other files");
    }

    [Fact]
    public async Task X_ShouldReadAPostsOwnTextFromTheEmbedServiceWithoutAKey()
    {
        var web = new FakeWeb().Answer("https://publish.x.com/oembed", """
            {"author_name":"jack","html":"<blockquote class=\"twitter-tweet\"><p lang=\"en\" dir=\"ltr\">just setting up my twttr</p>&mdash; jack (@jack) <a href=\"https://x.com/jack/status/20\">March 21, 2006</a></blockquote>"}
            """);

        var post = await Reach(web).ReadAsync("https://twitter.com/jack/status/20?s=20", default);

        post.Url.Should().Be("https://x.com/jack/status/20");
        post.Text.Should().StartWith("just setting up my twttr");
        post.Note.Should().Contain("Reach:XBearerToken");
        web.Requests.Single().Authorization.Should().BeNull();
    }

    [Fact]
    public async Task CheckAll_ShouldSayWhichChannelsWork_InPlainWords()
    {
        var web = new FakeWeb()
            .Answer("https://api.github.com/rate_limit", """{"resources":{"core":{"remaining":0}}}""")
            .Answer("https://hn.algolia.com/api/v1/search", """{"hits":[]}""")
            .Answer("https://www.youtube.com/oembed", """{"title":"Me at the zoo"}""")
            .Answer("https://itunes.apple.com/search", """{"results":[]}""", HttpStatusCode.Forbidden);

        var statuses = await Reach(web).CheckAllAsync(default);

        statuses.Should().OnlyContain(s => s.Checked);
        statuses.Single(s => s.Id == "github").Problem.Should().Contain("hourly allowance");
        statuses.Single(s => s.Id == "hackernews").Working.Should().BeTrue();
        statuses.Single(s => s.Id == "youtube").Should().Match<ReachStatus>(s => s.Working && s.Needs != null);
        statuses.Single(s => s.Id == "podcasts").Problem.Should().Contain("refused");
        statuses.Single(s => s.Id == "reddit").Problem.Should().StartWith("Not set up");
        statuses.Single(s => s.Id == "mastodon").Problem.Should().Contain("isn't on Mastodon", "the fake web has no answer for it");
    }

    [Fact]
    public async Task Tools_ShouldReachTheSameRolesAsWebSearch_AndPassAChannelsReasonOn()
    {
        var web = new FakeWeb().Answer("https://hn.algolia.com/api/v1/search", """
            {"hits":[{"objectID":"1","title":"Show HN: Overwatch","author":"grant","url":"https://grantwatson.dev","points":12,"num_comments":3,"created_at":"2026-11-05T10:00:00Z"}]}
            """);
        var tools = new ReachAgentTools(Reach(web));
        var author = new SentinelAgentUser("writer", [AppRoles.Author]);
        var context = new SentinelAgentToolContext(author, Guid.NewGuid());

        tools.Tools.Select(t => t.Name).Should().Equal("read_link", "search_channel", "list_channels");
        tools.Tools.Should().OnlyContain(t => t.Risk == SentinelAgentRisk.Read && author.MayUse(t));
        var nobody = new SentinelAgentUser("nobody", []);
        tools.Tools.Should().OnlyContain(t => !nobody.MayUse(t));

        var found = await tools.Tools.Single(t => t.Name == "search_channel")
            .RunAsync(new JsonObject { ["channel"] = "Hacker News", ["query"] = "overwatch" }, context, default);
        found.ForModel.Should().Contain("Show HN: Overwatch").And.Contain("https://news.ycombinator.com/item?id=1").And.Contain("12 points");

        await tools.Tools.Single(t => t.Name == "read_link").Invoking(t => t.RunAsync(new JsonObject { ["url"] = "https://www.reddit.com/r/a/comments/b/c" }, context, default))
            .Should().ThrowAsync<SentinelAgentToolException>().WithMessage("Reddit only answers apps that sign in*");
        var list = await tools.Tools.Single(t => t.Name == "list_channels").RunAsync(new JsonObject(), context, default);
        list.ForModel.Should().Contain("Reach:RedditClientId").And.Contain("be read until OllamaWeb:ApiKey is set");
    }

    // The open client is the app's own registration: links naming a private or reserved address
    // are refused when connecting, before anything is sent.
    [Theory]
    [InlineData("https://127.0.0.1/@a/1234567")]
    [InlineData("https://169.254.169.254/@a/1234567")]
    [InlineData("https://10.0.0.5/users/a/statuses/1234567")]
    public async Task LinksToPrivateAddresses_ShouldBeRefused(string link)
    {
        var folder = Path.Combine(Path.GetTempPath(), "gws-reach", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
            ["WorldNews:DatabasePath"] = Path.Combine(folder, "worldnews.db")
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var reach = scope.ServiceProvider.GetRequiredService<ReachService>();

        await reach.Invoking(r => r.ReadAsync(link, default)).Should().ThrowAsync<ReachException>().WithMessage("*private network*");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Captions_ShouldReadAsRunningText() =>
        ReachText.Captions("WEBVTT\r\n\r\nNOTE made by hand\r\n\r\n00:00.000 --> 00:01.000\r\n<i>One</i> &amp; two\r\n\r\n2\r\n00:01.000 --> 00:02.000\r\nthree")
            .Should().Be("One & two three");

    private sealed record Seen(string Client, string Method, string Url, string? Authorization, string? Accept, string? UserAgent, string? Body);

    private sealed class FakeWeb
    {
        private readonly List<(string Prefix, HttpStatusCode Status, string Body)> _answers = [];
        public List<Seen> Requests { get; } = [];

        public FakeWeb Answer(string prefix, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers.Add((prefix, status, body));
            return this;
        }

        public HttpResponseMessage Send(string client, HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(new Seen(client, request.Method.Method, url, request.Headers.Authorization?.ToString(),
                request.Headers.Accept.Count == 0 ? null : string.Join(",", request.Headers.Accept),
                request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            var match = _answers.Where(a => url.StartsWith(a.Prefix, StringComparison.Ordinal)).OrderByDescending(a => a.Prefix.Length).FirstOrDefault();
            return match.Prefix is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(match.Status) { Content = new StringContent(match.Body) };
        }
    }

    private sealed class Factory(FakeWeb web) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(web, name));

        private sealed class Handler(FakeWeb web, string client) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(web.Send(client, request));
        }
    }

    private sealed class FakePages : IOllamaWebSearchService
    {
        public List<string> Fetched { get; } = [];
        public bool IsConfigured => true;
        public Task<IReadOnlyList<OllamaWebSearchResult>> SearchAsync(string query, int? maxResults = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<OllamaWebSearchResult> FetchAsync(string url, CancellationToken ct = default)
        {
            Fetched.Add(url);
            return Task.FromResult(new OllamaWebSearchResult("A blog", url, "Page text"));
        }
    }
}
