# Media Watch web crawler + mail-in on the server account (26-10-26)

Grant (26-10-26): "some way to scrape the web for search terms in the Media Watch kind of like
how Google crawls the web for terms", then "I want to tell you what I want and I want you to
figure a way to make it happen" (no hand-back steps). Same day, the SMTP ask ("use those settings
to connect the email") was taken as: connect Support email-to-ticket and Sentinel email-in, which
each needed a mailbox typed into their panels, to the droplet's existing .env SMTP login.

## Web crawler (done 26-10-26)

- `Infrastructure/WebCrawl/`: `RobotsTxt` (RFC 9309: own group else `*`, longest match, Allow wins
  ties, `*`/`$`, Crawl-delay, Sitemap), `WebPageReader` (AngleSharp + SmartReader text, title,
  published date from meta / `<time>` / JSON-LD, canonical, robots meta, links normalised without
  tracking parameters; sitemap index / urlset (news dates) / RSS / Atom, DTDs refused),
  `WebCrawlStore` (own SQLite file `WebCrawl:DatabasePath`, FTS5 porter index, frontier with hubs
  re-fetched on an interval, per-site waits, counters, topic marks, pruning), `WebCrawler` +
  `WebCrawlBackgroundService`.
- Seeds: World News outlets + curated tech blogs + every topic's trusted feeds (feeds every 2 h,
  home pages and robots sitemaps every 6 h). Discovery: same-site links to depth 3, other sites'
  home pages (40 new sites a day; social networks and shorteners skipped). Feeds' and sitemaps'
  pages are queued wherever they're hosted.
- Politeness/safety: robots.txt (4xx = all allowed, 5xx/unreachable = site put off an hour), at
  least 10 s per site, User-Agent `GwsBusinessSuite-MediaWatch/1.0 (+gwsapp.net)`, 4,000 fetches a
  day paced across the day, 2 MB cap, ports 80/443 only, every connection through
  `AutomationDestinationValidator.ConnectAsync` (no private/metadata addresses, redirects and DNS
  rebinding included).
- Topic matching every 5 min: keywords (comma-separated) as quoted FTS phrases OR'd; hits become
  `NewsItem`s in the topic's feed, Source `Web · host`, once per URL; a topic's first look goes back
  two days. `WebSearchQuery` quotes every user word/phrase so no input reaches FTS5 syntax.
- UI: Media Watch sidebar **Web search** (`MediaWatchWebSearch.razor`): search, crawler status,
  sites (add/remove). Media Watch card/list styles became `::deep` so the child view is themed.
- Config: on in production (`/app/data/webcrawl.db`, 14 days), off in Development.
- Tests: `WebCrawlTests` (7, incl. an end-to-end crawl of a fake site: robots Disallow never
  requested, per-site waits, sitemap + link discovery, canonical address, topic match once).
- Live check on a throwaway instance (real web): ~3 min -> 172 fetches, 42 pages, 104 sites, no
  failed rounds; searches returned Dawn / Japan Times / BBC / DW pages.

## Mail-in on the server account (done 26-10-26)

- `ServerMailAccount`: when the shared Smtp login is Google (smtp.gmail.com), it's lent to IMAP
  (imap.gmail.com:993, authserv mx.google.com) on a plus-address per feature: `you+support@`,
  `you+sentinel@`. Polling searches Gmail with `deliveredto:` and re-checks To/Cc/Delivered-To;
  anything else in the inbox is never read or marked.
- Support and Sentinel services: a mailbox saved in the panel (with password) still wins; with none
  saved the server account is used and is ON unless turned off (`SetServerAccountEnabledAsync`,
  a settings row with no password and Enabled=false). Reply-To for ticket mail = the plus-address.
- Panels show the address and Turn off / Turn on; Test connection / Check now work on it.
- Tests: 3 new in `SupportEmailInTests`. Panel checked on a throwaway instance (fake account).
- Not verifiable from here: the droplet's real .env values (no SSH access); if its Smtp login isn't
  Google, the panels simply stay as they were.

## Next candidates

- Brave Search API (needs a key) for whole-web discovery of each topic's terms.
- Crawl stats per topic (hits per day) in Trends.
