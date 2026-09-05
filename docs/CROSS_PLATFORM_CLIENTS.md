# GWS Business Suite cross-platform clients

The hosted ASP.NET Core application and its database remain the source of truth. Browser and
MAUI clients authenticate against and navigate the same HTTPS deployment, so no client owns a
competing SQLite database.

The hosted Blazor interface is also the visual source of truth. Native wrappers do not add a
second toolbar or recreate pages: browser and MAUI render the same responsive admin and Sentinel
components. Only loading, offline, and package-level operating-system states are native, and
those states mirror the canonical web tokens in `wwwroot/app.css`. See
[`UI_DESIGN_SYSTEM.md`](UI_DESIGN_SYSTEM.md) for the cross-platform contract.

## Platform plan

| Platform | Client | Current phase |
| --- | --- | --- |
| macOS | .NET MAUI / Mac Catalyst | Online synchronized shell |
| Windows | .NET MAUI / WinUI 3 | Online synchronized shell |
| iOS | .NET MAUI | Online synchronized shell |
| Android | .NET MAUI | Online synchronized shell |
| Browser | Existing Blazor Server app | Canonical web client |

## Continuous-integration artifacts

The `Native Clients` GitHub Actions workflow runs for client changes pushed to `main` or a
`codex/**` feature branch, for matching pull requests, and by manual dispatch. Each successful
run retains downloadable build artifacts for 14 days:

| Artifact | Contents |
| --- | --- |
| `gws-android-qa` | APK and Android App Bundle built without a production signing key |
| `gws-macos-arm64-unsigned` | Zipped Mac Catalyst application bundle |
| `gws-ios-simulator-arm64` | Zipped iOS Simulator application bundle |
| `gws-windows-x64-unsigned` | Unpackaged Windows x64 application directory |

These are QA artifacts, not store-ready releases. Public distribution still requires platform
signing identities, Apple provisioning and notarization, Android/Windows signing keys, and the
corresponding release secrets.

## MAUI client

`src/GwsBusinessSuite.App` starts at the complete admin portal at
`https://admin.gwsapp.net/admin`. Sentinel and every other admin workspace remain available
through the shared admin navigation. Its WebView retains the normal authenticated browser
session, blocks in-app navigation to untrusted origins, opens external HTTPS links in the system
browser, and reports connectivity/navigation failures.
Camera and microphone access for Live Show is declared on every MAUI platform. Android's WebView
grants capture requests only when they originate from the configured GWS server and contain only
camera/microphone resources; other origins and permission types are denied.
Native file selection remains available for media uploads, article images, and data imports.
Authenticated exports and other downloads are accepted only from the configured GWS server:
Android saves them to Downloads, Windows uses the standard WebView2 download UI, and Apple clients
open the system save/share sheet after the transfer completes.
The server URL can be overridden for development by setting the MAUI preference named `BaseUrl`;
release builds reject non-HTTPS values.

Build examples from macOS:

```bash
dotnet workload install maui
dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -f net10.0-android
dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -f net10.0-maccatalyst
```

On a configured development Mac, launch the native Mac Catalyst client from any directory with:

```bash
open GWS
```

The shell shortcut delegates to `scripts/open-gws-mac.sh`. The script selects the Mac's
architecture, rebuilds the Debug app bundle when client source files have changed, and opens
the native `GWS Business Suite.app`. Other uses of the macOS `open` command are delegated
unchanged.

Apple builds must use the Xcode version required by the installed iOS/Mac Catalyst workload.
Windows packaging must run on a Windows build agent; signed iOS/macOS packages require an Apple
developer identity and provisioning profile.

The Windows CI job selects only its Windows target through the app-specific
`GwsClientTargetFramework` MSBuild property. Do not replace that with a command-line
`TargetFrameworks` override: reserved global properties propagate to the app's plain `net10.0`
project references and can leave their NuGet assets without a `net10.0` target. The equivalent
Windows restore/build sequence is:

```powershell
dotnet restore src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -p:GwsClientTargetFramework=net10.0-windows10.0.19041.0 -r win-x64
dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -f net10.0-windows10.0.19041.0 -c Release --no-restore -r win-x64 -p:GwsClientTargetFramework=net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=true
```

## Device login and the native SentinelGPT tab (macOS)

The native Mac app's toolbar has a lock-icon "Configure device login" button that saves a shared
secret (matching the server's `NATIVE_APP_DEVICE_SECRET`) via a `POST /auth/device-login` call -
this lets the app sign in without the browser's mandatory MFA challenge, since it authenticates
with a device secret the user provisions once instead. It is optional; without a secret
configured, the app's WebView falls back to the normal browser login (with MFA) exactly as before.

Separately, the Mac app has its own native SentinelGPT tab - not the WebView-rendered admin
Sentinel page - which by design runs inference against Ollama models installed locally on that
Mac (`127.0.0.1:11434`), not this server's own Ollama container. This keeps that conversation
entirely on-device. If the local model can't handle a turn (Ollama isn't running, the model isn't
installed, or it times out), and a device secret is already configured, the tab automatically
falls back to a plain, non-tool-calling completion from the server's own Ollama via
`POST /native/fallback-chat` - the same device-secret trust boundary as device-login, not a user
login. Any answer that came from this fallback is visibly labeled "via server" in the transcript,
since the whole point of the local-first design is that the exception stays visible rather than
silently blending in.

### Choosing a local model

Ollama gates chat and tool-calling per model and rejects the whole request rather than degrading
(`"embeddinggemma" does not support chat`, `gemma3:12b does not support tools`). The tab's model
picker therefore lists only chat-capable models, and annotates each with its parameter size and
`no tools` where applicable. A model without tool support still works for conversation - the tab
simply offers it no tools and tells it so - but it cannot search the wiki or read an attached
folder. Thinking is disabled (`think: false`) for models that support it, because it costs real
latency for no accuracy gain on ordinary messages; Deep analysis remains the deliberate opt-in
for depth.

The `sentinelgpt` profile is based on `gemma4` as of 2026-09-04, replacing `llama3.2`. Measured
on an M3 Pro over 7 prompts with this tab's own tool set, the 3.2B llama3.2 base scored 2/7 -
it called `search_wiki` for "are you running locally?", invented a filename to `read_file` for a
plain C# question, and answered "17 * 23" with the literal text `{"name":"multiply",...}`. The
8B gemma4 base scored 7/7 on the same prompts. Rationale and figures are in
`ollama/SentinelGPT.Modelfile` itself.

### Model library and CLI parity

App Sandbox denies process execution outright, so the tab cannot shell out to `ollama` or to
`sentinelcli`. Instead, the capabilities that make sense in a GUI are available in-process
against Ollama's loopback HTTP API:

- **Model library** (down-arrow in the toolbar) browses curated free models with sizes and
  tool-support flags, installs any of them (or any name from ollama.com/library) via
  `POST /api/pull`, and rebuilds the whole canonical set plus the `sentinelgpt` profile via
  `POST /api/create`. Ollama removed the whole-file `modelfile` field, so `OllamaModelfileParser`
  splits `ollama/SentinelGPT.Modelfile` into the structured fields the API now wants - the same
  version-controlled text still drives both `ollama create -f` in the CLI and the API here.
- **Personas** (`/agent` in the CLI) shape tone and priorities through an extra system-prompt
  paragraph, and are advisory rather than enforcing.
- **Skills** (`/skills` in the CLI) apply one markdown file's instructions to a single message.
  The CLI's `~/.config/sentinelcli/skills` is unreachable from the sandbox, so the tab reads its
  own app container plus `.sentinel/skills` inside the attached project folder - which lets a
  repository carry its own skills alongside its code.

### GWS business data

Beyond the wiki, the tab can read live business data over the same `sentinel:read` developer-API
key: `search_crm` (contacts and deals), `get_pipeline` (deal counts and value by stage),
`search_cms_pages` (public site pages and publish status), and `get_system_health` (unread
container alerts). Every one of these is read-only by construction - there is no `sentinel:write`
scope for any of them to pair with, and no mutating tool exists in `NativeToolExecutor`. The
tools are offered only when a grounding key is configured, so an unconfigured Mac degrades to
plain local chat rather than to a model describing lookups it cannot perform.

## Sentinel menu-bar companion (macOS)

`src/GwsBusinessSuite.SentinelMenuBar` is a small, separate `net10.0-macos` app (not part of the
`GwsBusinessSuite.App` MAUI shell) - a persistent `NSStatusItem` with no Dock icon or window
(`LSUIElement` in `Info.plist`). It holds no credentials and owns no data: every menu action
opens the system browser at the already-authenticated hosted app, the same source of truth every
other client uses. "Open Sentinel" and "Open Dashboard" are plain deep links; "Refresh Workspace
Data" opens Sentinel with `?syncNow=1`, which `Wiki.razor` recognizes to trigger the same manual
Notion sync the in-app "Sync now" button calls - no separate authenticated API was added for the
native client. The status-bar glyph is a placeholder SF Symbol pending a dedicated Sentinel logo.

Build from macOS (requires the `macos` workload: `dotnet workload install macos`):

```bash
dotnet build src/GwsBusinessSuite.SentinelMenuBar/GwsBusinessSuite.SentinelMenuBar.csproj
```

As of 2026-08-01 this has not been build-verified on the development Mac: the installed Xcode
(27.0) is newer than every currently-published `.NET for macOS`/`.NET for MacCatalyst` SDK pack,
each of which requires an exact Xcode version (26.0 or 26.6). This also already blocks the
existing MAUI Mac Catalyst target, so it's a toolchain/environment gap, not specific to this
project - confirm both build once a matching Xcode is installed.

## SentinelCLI (macOS)

`src/SentinelCLI` is the local terminal companion for software work. It is a
self-contained .NET console application rather than a WebView: it talks only to the loopback
Ollama API and receives bounded tools for listing, reading, searching, editing, and validating
files beneath a user-selected workspace root. The default coding model is `qwen2.5-coder`; the
same canonical model manifest and SentinelGPT Modelfile used by the production Ollama container
are embedded in the CLI for local model synchronization.

Install it for the current macOS user with:

```bash
./scripts/install-sentinelcli.sh --sync-models
```

The executable is linked at `~/.local/bin/sentinelcli`. See
[`SENTINELCLI_USER_GUIDE.md`](SENTINELCLI_USER_GUIDE.md) for repository selection,
confirmation behavior, safety boundaries, and troubleshooting.

## Migration beyond the online shell

The shell phase delivers installable clients and synchronized data immediately. Reusing Razor UI
locally is a separate migration: move portable components into a Razor Class Library, expose
authenticated HTTP/SignalR endpoints for server-only services, and replace component-level EF
access with API-backed client services. Offline editing would additionally require a local outbox,
versioned records, deterministic conflict resolution, and replay/idempotency contracts.
