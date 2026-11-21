# Multi-Droplet SSH Docker & Image Management (`2026-11-21`)

## Goal
Extend `/admin/docker-health` (`DockerHealth.razor`) so Grant can inspect active containers, all cached Docker images, Docker disk usage, and AI droplet runtime health (`ollama` / `searxng`) across every active DigitalOcean droplet, and execute safe, audit-logged SSH actions directly from each droplet card.

## Architecture & Scope

### 1. Application Layer (`GwsBusinessSuite.Application/DigitalOcean/`)
- **`IDropletRemoteDockerService`** & **`DropletRemoteDockerModels.cs`**:
  - `InspectDropletAsync(ActiveDropletView droplet, CancellationToken ct)` — runs read-only inspection on a single active droplet (`docker image ls`, `docker system df`, `ollama list` + `systemctl is-active ollama gws-ollama-watchdog.timer`, and `cd /opt/gwssuite && docker compose ps --format json` when present).
  - `TailContainerLogsAsync(ActiveDropletView droplet, string containerName, int tailLines = 100, CancellationToken ct)` — runs `docker logs --tail <N> --timestamps <container>`.
  - `PullImageAsync(ActiveDropletView droplet, string image, string performedBy, CancellationToken ct)` — runs `docker image pull <image>` and records a `DockerActionLog`.
  - `RestartContainerAsync(ActiveDropletView droplet, string containerName, string performedBy, CancellationToken ct)` — runs `docker restart <container>` and records a `DockerActionLog`.
  - `PruneDanglingImagesAsync(ActiveDropletView droplet, string performedBy, CancellationToken ct)` — runs `docker image prune -f` and records a `DockerActionLog`.
  - `PruneStaleImagesAsync(ActiveDropletView droplet, string performedBy, CancellationToken ct)` — runs `docker image prune -a -f --filter "until=168h"` and records a `DockerActionLog`.
  - `RestartOllamaAsync(ActiveDropletView droplet, string performedBy, CancellationToken ct)` — runs `systemctl restart ollama` (on droplets running `ollama`) and records a `DockerActionLog`.
- **Input Validation:**
  - `DropletDockerIdentifier.IsValid(string? value)` enforces `^[a-zA-Z0-9._:/-]+$` (max 256 chars) so no shell metacharacters can ever reach SSH commands.

### 2. Infrastructure Layer (`GwsBusinessSuite.Infrastructure/Services/DropletRemoteDockerService.cs`)
- Reuses `DigitalOceanSettings` (encrypted `SshPrivateKey`, `SshPrivateKeyPassphrase`, `SshUsername`, `SshPort`, and `SshHostKeyFingerprint` for the primary droplet) via `Renci.SshNet.SshClient`.
- Pure, unit-tested static parsers:
  - `ParseDockerImagesOutput(string output)` (`docker image ls --format '{{.Repository}}\t{{.Tag}}\t{{.ID}}\t{{.Size}}\t{{.CreatedAt}}'`)
  - `ParseDockerSystemDfOutput(string output)` (`docker system df --format '{{.Type}}\t{{.TotalCount}}\t{{.Active}}\t{{.Size}}\t{{.Reclaimable}}'`)
  - `ParseOllamaListOutput(string output)` (`ollama list` tabular output)
- Every mutating SSH action logs a `DockerActionLog` row (`ContainerName = $"{droplet.Name}:{target}"`, `Action`, `Command`, `Succeeded`, `ResultSummary`, `PerformedBy`).
- Registered in `DependencyInjection.cs` as `services.AddScoped<IDropletRemoteDockerService, DropletRemoteDockerService>()`.

### 3. Web UI (`GwsBusinessSuite.Web/Components/Pages/BusinessSuite/DockerHealth.razor`)
- Each droplet card in **Active Droplets & Images** gets:
  - **Inspect Images & Disk** button (or auto-loaded details drawer) showing cached Docker images (`docker image ls`), disk usage & reclaimable space (`docker system df`), and Ollama models + systemd status on the AI droplet.
  - **Read-only inline log viewer** (`Tail Logs` per running container).
  - **Confirm-gated action buttons** (`Pull`, `Restart`, `Prune Dangling`, `Prune Stale (>7d)`, and `Restart Ollama` when Ollama is detected), all using `ConfirmModal` (never `window.confirm`).

### 4. Tests (`tests/GwsBusinessSuite.Tests/DropletRemoteDockerServiceTests.cs`)
- Unit tests for `DropletDockerIdentifier.IsValid`, `ParseDockerImagesOutput`, `ParseDockerSystemDfOutput`, `ParseOllamaListOutput`, and `DropletRemoteDockerService` failure/validation paths (missing SSH key, invalid identifier rejection, audit log recording).

## Progress
- [x] Implementation plan documented in `.claude/plans/droplet-ssh-docker-images-2026-11-21.md`
- [x] Application & Infrastructure `IDropletRemoteDockerService` implementation (`IDropletRemoteDockerService.cs`, `DropletRemoteDockerService.cs`, `DependencyInjection.cs`)
- [x] `DockerHealth.razor` UI wiring & Confirm modals (`Inspect Images & Disk`, `Logs`, `Pull`, `Restart`, `Prune Dangling`, `Prune Stale (>7d)`, `Restart Ollama`)
- [x] Unit tests & build verification (`DropletRemoteDockerServiceTests`, `DigitalOceanServiceTests`, `RazorTemplateGuardTests` passed with exit code 0)
