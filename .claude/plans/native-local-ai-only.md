# Native macOS local-AI-only enforcement

## Goal

When SentinelGPT is used from the GWS Business Suite macOS application, inference runs only on
that Mac's Ollama models. A local failure is reported locally and must never fall back to the AI
droplet. The application also must not prevent the Mac from sleeping.

## Plan

1. Remove the native server-fallback client, endpoint, rate-limit policy, and UI attribution.
2. Make a failed native local turn end with a clear local-Ollama recovery message and retry action.
3. Remove the idle-system-sleep assertion, battery policy service/settings, and their obsolete tests.
4. Add contract tests that prevent either server fallback or sleep-prevention behavior returning.
5. Update the platform operations guide and run focused tests plus `./scripts/verify-release.sh`.

## Progress

- [x] Traced the native agent relay and found the explicit `/native/fallback-chat` droplet path.
- [x] Confirmed the native agent turn itself already uses `RelayedOllamaService` tied to the signed-in
      user's Mac.
- [x] Remove native droplet fallback.
- [x] Remove keep-awake behavior.
- [x] Add regression coverage.
- [x] Update documentation.
- [x] Run release verification. Feature tests, Release build and Mac Catalyst build pass; the full
      suite remains blocked by an unrelated, non-reproducing Overwatch browser timing failure
      (`WatchWall_ClosingThenClickingACamera_ShouldStillOpenANormalStreamPanel`).

## Working-tree boundary

`tests/GwsBusinessSuite.Tests/NavLoadingBrowserTests.cs` was already modified by the preceding CI
repair. Preserve it and include it in release verification, but do not fold it into this feature's
implementation narrative.
