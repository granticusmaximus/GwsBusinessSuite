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
The server URL can be overridden for development by setting the MAUI preference named `BaseUrl`;
release builds reject non-HTTPS values.

Build examples from macOS:

```bash
dotnet workload install maui
dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -f net10.0-android
dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -f net10.0-maccatalyst
```

Apple builds must use the Xcode version required by the installed iOS/Mac Catalyst workload.
Windows packaging must run on a Windows build agent; signed iOS/macOS packages require an Apple
developer identity and provisioning profile.

## Migration beyond the online shell

The shell phase delivers installable clients and synchronized data immediately. Reusing Razor UI
locally is a separate migration: move portable components into a Razor Class Library, expose
authenticated HTTP/SignalR endpoints for server-only services, and replace component-level EF
access with API-backed client services. Offline editing would additionally require a local outbox,
versioned records, deterministic conflict resolution, and replay/idempotency contracts.
