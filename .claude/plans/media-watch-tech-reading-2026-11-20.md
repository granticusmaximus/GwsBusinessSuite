# Media Watch: a reading desk for software development (20-11-26)

Grant: "Build 1-7 and test everything visually." One phase, all seven:

1. Software development source pack: one click makes (or tops up) a Technical topic with key-less
   feeds (InfoQ, .NET Blog, GitHub Engineering, Martin Fowler, Simon Willison, Andrew Lock, Pragmatic
   Engineer, Cloudflare, Netflix) and Lobsters; fixes the dead GitHub curated feed.
2. Rank by community signal and depth: Hacker News points (Algolia, also looked up by URL for other
   sources, bounded) and Lobsters scores stored on NewsItem.CommunityScore; reading minutes from the
   text already extracted for hot takes (NewsItem.ReadingMinutes). "Best reads" sort (TechReadingRank).
3. Reading queue: saved articles carry minutes and FinishedAt; filter by time (5/15/30), sort by
   length, mark finished (also from the reader), finished hidden by default.
4. Stack filters: tag chips (.NET/Blazor, AI and local models, Security, Architecture, Apple/MAUI,
   Web) and muted keywords/sites in Media Watch settings (ReadingFilters).
5. "Why it matters to me": reader button, local model gives three takeaways + one line for GWS.
6. Full-text clips with notes: clipping saves the whole readable article; the reader has notes and
   "add selection as highlight"; clipping an already-clipped article appends the notes.
7. Weekly reading digest: Monday, a personal Sentinel page per reader (SentinelPersonalPages) with
   the top unread deep reads of the week; "Build now" on the page.

## Progress
- [x] Domain + migration (NewsItem.CommunityScore/ReadingMinutes; SavedNewsArticle.ReadingMinutes/
      FinishedAt; NewsWatchSettings.MutedKeywords/MutedSources)
- [x] Application: TechReadingPack, TechReadingRank, ReadingFilters, reader takeaways/markdown
- [x] Infrastructure: sources, scores, minutes, queue, clips, digest
- [x] Web: pack button, sort, chips, mutes, queue, reader actions
- [x] Tests + visual check (local app on a DB copy, real feeds, local Ollama, Playwright):
      pack refresh (30 new, scores + minutes), best reads, stack chip, reader, takeaways, highlight +
      note + clip (full article + quote block in Sentinel), read later / finished, queue filters,
      mutes, weekly page in Sentinel, Copy button. Fixed on the way: reader didn't redraw after
      clipping; first weekly run built pages midweek; takeaways' "For GWS" line as a bullet; queue
      title; placeholder entity text.
