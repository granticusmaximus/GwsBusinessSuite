# World News outage and charge-free search (19-11-26)

Grant asked that World News keep checking for new intelligence, and that nothing use a paid Ollama
service ("build a service that does what that was intending and then have Ollama do its magic").

## Found in production (read-only SSH, `/app/data/logs/*.clef`)

- **Every World News read failed** (98 `World news read failed` from 05:02 to 13:20 UTC on
  19-11-26 in the three newest log files, roughly one per 5-minute read). `FeedSchedule.AfterFailure`
  computed `5 min * 2^(failures-2)` before applying the 1-hour cap. Once one feed had failed ~45
  times in a row, `TimeSpan.FromMinutes` overflowed. The exception escaped the feed loop
  (`Parallel.ForEachAsync`) and aborted the whole read, so no new stories came in.
- **Active discovery searched a paid API.** `.env` has `OLLAMA_WEB_API_KEY`, which made
  `OllamaWebSearchService` use ollama.com's billed web search. It answered 402 Payment Required to
  every search (42 in about 3 hours). `SEARXNG_URL` is set too, and SearXNG answers from the web
  container: a live `time_range=day` search returned BBC, Al Jazeera, CNN, Reuters and AP, with no
  unresponsive engines.

## Fixes

- `FeedSchedule.AfterFailure` caps the exponent before making the `TimeSpan`.
- `WorldNewsPipeline` catches any exception from one feed as that feed's failure (cancellation
  excepted).
- `OllamaWeb:AllowPaidSearch` (default false; compose `OLLAMA_WEB_ALLOW_PAID_SEARCH`). Without it,
  the key is ignored: search is SearXNG, pages are fetched by the server's own guarded client, and
  the gws-ai model does the summarizing and grouping. This existing key-less path is what Grant
  described, so no parallel service was built. If paid search is ever allowed, a 401/402/403
  hands searches and fetches to SearXNG for 6 hours (429: 15 minutes), keyed by API key.
- `WorldNewsService.IsBehind`: the World News header turns amber and says "Not updating", with the
  last good read and the reason, when a read failed or none finished in 3 intervals.

## Progress

- [x] Root causes found in production logs.
- [x] Fixes and tests (`WorldNewsCrawlerTests`: 5,000-failure backoff, a broken feed doesn't stop
      the others, `IsBehind`; `OllamaWebSearchServiceTests`: never paid unless allowed, refusal
      hand-over, no key sent to SearXNG).
- [x] `docs/INTELLIGENCE_USER_GUIDE.md` (Sources section), `.env.example`, `docker-compose.yml`.
- [ ] After deploy: confirm `World news read failed` stops and reads finish every ~5 minutes;
      discovery runs on SearXNG with no 402s.
- [ ] Optional, Grant: delete `OLLAMA_WEB_API_KEY` from the droplet `.env`. It is harmless now.
