# The AI droplet as SentinelGPT's one source (13-11-26)

Grant (13-11-26): World News said "SentinelGPT writes this on your Mac, and no Mac is connected".
The web app must not depend on the Mac app; SentinelGPT's source of truth is the separate Ollama
droplet (`gws-ai`, set up with Codex on 08-11-26, private address in the ops guide, firewall
admits only the web droplet).

Cause: production ran `ServerAi__Mode: None` (no `SERVER_AI_MODE` in the web droplet's `.env`), so
`IOllamaService` was `MacRelayOllamaService` and nothing pointed at the AI droplet.

## Pieces

| # | Piece | State |
| --- | --- | --- |
| 1 | `ServerAiOptions.SingleSource` / `ServerModelOnly` (needs a server model; ignored in None) | done |
| 2 | `BrowserLocalOllamaService.For(js, serverAi, server?)`: probe off; local-only panels delegate to the server model | done |
| 3 | All 11 panels make their local client through `For` (Kanban x2, BI, Sentinel cleanup get the server model) | done |
| 4 | SentinelGPT side panel skips the Mac relay turn | done |
| 5 | World News: no summary/embed/kind jobs for the Mac; server never stands aside for a stale Mac | done |
| 6 | `docker-compose.yml` `ServerAi__SingleSource: true`; `.env.example` | done |
| 7 | Web droplet `.env`: `SERVER_AI_MODE=Full`, `ContentStudio__BaseUrl=http://<private-ip>:11434` + `docker compose up -d` | Grant (Claude's production access is blocked) |
| 8 | Docs (Platform Ops guide), AGENTS.md | done |

Known limit: Overwatch "Ask the cameras" needs a vision model; the AI droplet has none, so it says so.
The Mac app's relays keep polling but get no work; its native camera watcher still uses the Mac's Ollama.

## Progress

- 13-11-26: pieces 1-6 and 8 built with tests (ServerModelOnlyTests, WorldNewsEmbedRelayTests). Piece 7 waits for Grant.
