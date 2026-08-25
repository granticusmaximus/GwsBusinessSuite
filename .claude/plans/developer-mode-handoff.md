# Handoff: Developer Mode for the native SentinelGPT tab

## Status: shipped 2026-08-25 (commits `240f43e`, `146908f`, `6755037`)

Full plan lived at `~/.claude/plans/crystalline-spinning-babbage.md` (outside this repo, may not
be available to every session/agent - this file is the durable, in-repo record).

The Mac app's SentinelGPT tab can now read, search, and (with a per-edit approval dialog) modify
files in a folder the user picks, using the same `WorkspaceTools` engine SentinelCLI's terminal
tool already used. No process spawning or REPL piping - `WorkspaceTools` moved into a new shared
`GwsBusinessSuite.SentinelAgentKit` project and the native app's existing `OllamaToolCallingAgent`
drives it directly via `IOllamaToolExecutor`.

**Key finding**: the Mac Catalyst App Sandbox denies `Process.Start` outright (confirmed
empirically - `git`/`dotnet` both throw `Win32Exception: Operation not permitted` even with a
correct, fully-resolved `PATH`). `WorkspaceTools` therefore has a new `allowRunCommand` flag
(default `true`, unaffected for SentinelCLI); the native app constructs it with
`allowRunCommand: false`, so `run_command` is unavailable from this tab specifically. Don't
re-investigate this if it comes up again - it's a hard sandbox limitation, not a bug to fix.

**Descoped for v1, not bugs**: the chosen workspace folder does not persist across app relaunches
(no security-scoped bookmark support yet - must re-pick each launch).

**Verified**: `./scripts/verify-release.sh` passed locally. CI: "Deploy to DigitalOcean" and the
Apple/Android legs of "Native Clients" passed. The Windows leg of "Native Clients" failed, but on
a **pre-existing, unrelated** bug (`NETSDK1005` - the job's `-p:TargetFrameworks=...` override
bleeds into plain-`net10.0` library ProjectReferences during restore) confirmed present before
this work started; not fixed here since it needs a CI workflow YAML change, out of scope for this
feature.

**Not yet done**: a real interactive click-through smoke test (toggle, pick a folder, streaming
read, an approval-gated edit, folder-switching/history scoping) - needs a human at the keyboard,
no agent has visual access to this native app.

**Codex follow-up implemented locally 2026-08-25 (awaiting Grant's publish):** the terminal tool
is now officially SentinelCLI: project `src/SentinelCLI`, namespace `GwsBusinessSuite.SentinelCLI`,
binary/command `sentinelcli`, installer `scripts/install-sentinelcli.sh`, and guide
`docs/SENTINELCLI_USER_GUIDE.md`. The installer copies forward old CLI sessions and skills without
deleting them. The main solution and test project references were renamed in place, keeping the
console project and its CLI-specific tests inside the normal release gate.

**Still queued, unrelated to this feature** (see the Claude session's own memory if picking this
up as Claude - `project_sentinelgpt_consolidation_2026_08_24`):
- jsMind.Blazor as a standalone "Mind Maps" tool.
- blazordevelopertools.com as a dev-only NuGet package.
