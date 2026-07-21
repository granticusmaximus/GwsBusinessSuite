# GWS Business Suite

GWS Business Suite is a multi-project application for business operations and content workflows.

## Repository Structure

- `src/GwsBusinessSuite.Domain`: Domain entities and core business rules
- `src/GwsBusinessSuite.Application`: Use cases, orchestration, service contracts, and DTOs
- `src/GwsBusinessSuite.Infrastructure`: Data access and external integration implementations
- `src/GwsBusinessSuite.Web`: Blazor web application
- `src/GwsBusinessSuite.App`: .NET MAUI client for macOS, Windows, iOS, and Android
- `src/GwsBusinessSuite.Linux`: Linux desktop companion using the hosted application
- `tests/GwsBusinessSuite.Tests`: Unit and service tests
- `docs`: Architecture and product documentation

## Architecture

This repository follows Clean Architecture boundaries:

- `Web -> Application`
- `Infrastructure -> Application + Domain`
- `Application -> Domain`
- `Domain -> (no external project dependencies)`

## Prerequisites

- .NET SDK 10.x
- .NET MAUI workload for native Apple, Windows, and Android clients
- Node.js 22+ for Linux desktop packaging

## Build And Test

From the repository root:

```bash
dotnet build GwsBusinessSuite.slnx
dotnet test GwsBusinessSuite.slnx
```

Native clients are kept in a separate solution so server-only development and CI do not require
the MAUI workloads:

```bash
dotnet build src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj -f net10.0-android
```

## Run The Blazor App

```bash
dotnet run --project src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj
```

## Live Show TURN Relay

Live Show supports coturn for viewers whose networks cannot establish a direct WebRTC
connection. The app generates short-lived TURN REST credentials; do not put static TURN
usernames or browser credentials in source control.

For the Docker deployment:

1. Point a public DNS record such as `turn.example.com` at the host. If the domain uses
   Cloudflare DNS, keep this record DNS-only; the normal HTTP proxy does not carry TURN.
2. Copy the TURN settings from `.env.example` into the deployment host's `.env`, replacing
   the sample host and generating a long random shared secret.
3. Allow inbound TCP/UDP 3478 and UDP 49160-49200 in the host/cloud firewall.
4. Run `docker compose -f docker-compose.yml -f docker-compose.turn.yml up -d --build`,
   or push to `main`; the deployment workflow includes the override automatically when
   the required TURN variables are present.

The Live Show studio displays `TURN relay configured` when the app has both relay URLs and the
shared secret. Without them it keeps the existing STUN/direct-connection fallback.

## Database Migrations

`ApplicationDbContext` (`src/GwsBusinessSuite.Infrastructure`) requires the
`--project`/`--startup-project` flags below every time, since the DbContext lives in
Infrastructure but the connection string/EF tooling entry point is the Web project.
The wrapper scripts in `scripts/` set those for you:

```bash
./scripts/add-migration.sh <MigrationName>   # dotnet ef migrations add
./scripts/remove-migration.sh                # dotnet ef migrations remove (last, unapplied one)
./scripts/update-database.sh                 # dotnet ef database update (optional - the app
                                              # applies pending migrations automatically on
                                              # startup via dbContext.Database.MigrateAsync())
```

Equivalent raw commands, if you need to pass additional `dotnet ef` flags not covered
by the scripts:

```bash
dotnet ef migrations add <MigrationName> \
  --project src/GwsBusinessSuite.Infrastructure --startup-project src/GwsBusinessSuite.Web
```

Always review the generated migration file(s) under
`src/GwsBusinessSuite.Infrastructure/Migrations/` before committing.

## Key Documents

- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `docs/SEO_ARTICLE_GENERATOR.md`
- `docs/ARTICLE_IMAGE_BRANDING.md`
