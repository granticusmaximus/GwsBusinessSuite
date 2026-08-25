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

## Claude's outstanding items, handed off 2026-08-25 for Codex to pick up

Everything below is diagnosed/scoped but not started (or, for the CI bug, diagnosed but
deliberately not fixed). None of it overlaps the SentinelCLI rename above.

1. **Windows MAUI CI bug — fixed locally 2026-08-25, awaiting Grant's publish/CI run**: the
   app now accepts an app-specific `GwsClientTargetFramework` property and the workflow uses it
   instead of globally overriding the reserved `TargetFrameworks` property. The custom property
   safely flows through the graph because the plain `net10.0` libraries do not consume it. An
   isolated Windows graph restore on macOS passed: the app assets contain
   `net10.0-windows10.0.19041`, while OllamaKit and SentinelAgentKit each retain `net10.0`. A
   follow-up cross-build compiled both libraries and reached Windows' `XamlCompiler.exe`, where
   macOS correctly cannot execute a Windows binary; the original `NETSDK1005` is gone. The
   workflow path filters now also include both shared client libraries so their standalone
   changes cannot skip Native Clients CI. Original diagnosis: the "Native Clients" GitHub Actions
   workflow's Windows MAUI job had failed with `NETSDK1005: Assets file '...\obj\project.assets.json'
   doesn't have a target for 'net10.0'` on every plain-`net10.0` library the App project
   references (`GwsBusinessSuite.OllamaKit`, `GwsBusinessSuite.SentinelAgentKit`). Root cause: the
   job passes `-p:TargetFrameworks=net10.0-windows10.0.19041.0 -r win-x64` as global MSBuild
   properties to `dotnet restore`/`dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj`,
   and global properties propagate down ProjectReferences, so libraries that only declare
   `<TargetFramework>net10.0</TargetFramework>` get their restore silently redirected to a TFM
   they don't have. Confirmed pre-existing (present on commit `249b792`, before any of this
   session's Developer Mode work). Doesn't affect "Deploy to DigitalOcean" or local
   `verify-release.sh` (neither builds the Windows target). Needs the CI workflow YAML changed -
   e.g. give each referenced library its own restore step first, or isolate the global property
   so it doesn't propagate to ProjectReferences. Full original write-up:
   `project_windows_maui_ci_target_frameworks_bug` in the Claude session's memory.
2. **jsMind.Blazor as a standalone "Mind Maps" tool** (github.com/StefH/jsMind.Blazor) - explicit
   "I want this" request, not evaluated for integration approach yet.
3. **blazordevelopertools.com as a dev-only NuGet package** - explicit request; likely a small
   addition (package ref + dev-environment-only registration in `Program.cs`), not yet started.
4. **OSINT sidecar latency investigation** - the user asked why `vendor/osiris-intel` (the vendored
   OSINT sidecar) "seems way too laggy and slow." Unconfirmed hypothesis going in: likely
   unpaginated/uncached Wikidata SPARQL calls in `vendor/osiris-intel/intel/server.js` - never
   actually opened that file to confirm. Needs: read `server.js`, confirm or refute, propose a
   caching/pagination fix if confirmed.
5. **5 similar open-source apps research** - same request as #4: compile a list of 5 open-source
   apps in the same realm as the Osiris OSINT sidecar, at least 2 of them public-CCTV-related and
   C#-related/potentially portable into this app. Not yet researched.
6. **Developer Mode live smoke test** - not really delegable to another coding agent unless it has
   real visual/UI access to the running Mac app; flagging for completeness. Checklist: toggle
   Developer Mode with no folder chosen (confirm the guard message), pick a real folder, ask it to
   read a file (confirm streaming), trigger an edit (confirm the approval dialog, decline leaves
   the file unchanged, approve writes it), switch folders and confirm the history sidebar doesn't
   mix workspaces or ordinary chats, quit and relaunch to confirm the folder needs re-picking
   (expected, not a bug).
