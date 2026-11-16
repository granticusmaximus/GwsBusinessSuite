# macOS local AI battery policy

## Goal

Keep the Mac available as the web app's fast Ollama host while running on battery, without
permanently disabling display sleep or exhausting a low/overheated battery.

## Design

- The native app owns a macOS idle-system-sleep assertion while its policy allows local AI.
- The display may still sleep; closing the lid remains an operating-system boundary.
- Balanced allows battery operation at 20% or above and releases at serious/critical thermal state.
- Always allows battery operation at any charge, but still releases at unsafe thermal state.
- Never mode releases the assertion while on battery; AC operation remains available.
- Unknown battery telemetry is treated as available so desktop Macs and Catalyst telemetry gaps do
  not disable local AI.
- When the assertion is released and the Mac sleeps, the server's existing five-second health
  monitor routes work to the AI droplet.

## Progress

- [x] Trace the Tailscale/Ollama route and existing Mac background activity.
- [x] Implement policy decisions and tests.
- [x] Add the native sleep assertion and battery/thermal monitor.
- [x] Add the setting and status to SentinelGPT's native settings panel.
- [x] Update the Mac user guide and run release verification (`PARTIAL` only because deployed targets were not supplied).
