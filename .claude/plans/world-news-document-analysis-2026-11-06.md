# World News: a document that answers the question (06-11-26) - DONE

## Grant's ask

"When I find a story to research on Overwatch from World News intelligence, I want to be able to
click on Read Document, I want to see the following information:
- What the hypothesis is being stated from the report
- A summary of what all outlets that are reporting on the matter
- Who those outlets are
- Compare and contrast what is missing from each outlet
- Compare and contrast what all are saying is accurate
- Then a list of the links of outlets and their stories"

## Why the first version fell short (checked on production, read-only, 06-11-26)

- 702 events in the last 24 h had 3+ outlets; only 70 had any summary.
  - The Mac wrote 54 in that day, at about 5 per 15-minute round while its app was open.
  - Only 10 summaries had the comparison.
- Summaries came from headlines and feed descriptions only. Article text was never read.

## What it does now

- **On demand.** Read Document writes SentinelGPT's analysis there and then (`EventAnalyzer`,
  `WorldNewsService.AnalyzeAsync`).
  - It writes again once coverage grows by a quarter. "Analyse again" does it any time.
  - One run per event, shared by everyone waiting on it.
  - It runs where shared work on public data runs (MacRelayScope): with ServerAi None, that's
    the Admin Mac.
- **Article text** (`WorldNewsArticleReader`):
  - Up to 14 articles, one per outlet, languages taking turns.
  - Only outlets' own links. Google News links need a browser to follow: 28k of the day's links
    were those.
  - Fetched through the guarded client (no private addresses) and RefusedHostPause.
  - SmartReader text via WebPageReader, the opening ~2,400 characters.
  - Kept in `article_texts`; failures are retried after 24 h.
- **Document order:**
  1. The claim
  2. What the outlets report
  3. Who is reporting it (every outlet named)
  4. What each outlet leaves out or adds
  5. What they agree on (+ the no-AI figures)
  6. Sources, with links
- **"Leaves out" is computed, not asked for.** Live check on gemma4/sentinelgpt: asked twice
  for omissions (even with a fixed "leaves out ...; adds ..." form and an example), it only ever
  wrote what each outlet adds. So:
  - AGREED lines name the outlets stating each fact: `- fact [A, B, C]`.
  - `EventAnalyzer.Compare` lists, per outlet, the agreed facts it isn't named for.
  - DIFFERENCES gives "adds".
  - Outlet names are matched to the event's own outlets; unknown ones are dropped from facts,
    and kept as general points in differences.
- **Overwatch:** READ DOCUMENT in the World News panel opens `/admin/world-news?event=<latest
  event>&read=1` (`WorldNewsMapSignal.EventId`). The `read` parameter is text, because a bool
  can't parse "1" and took the page down (500).
- **No Mac connected:** the window says so plainly, with Try again. The outlets, figures and
  sources don't need a model.

## Checked

- Lula re-election event (79 reports, 31 direct links), local sentinelgpt:
  - 14 articles read in about 4 s; the analysis in 60-75 s.
  - All six sections filled; the PDF has them.
- ServerAi None with no Mac: the message and Try again.
- 100 World News and PDF tests.
