# GWS Business Suite

GWS Business Suite is a multi-project application for business operations and content workflows.

## Repository Structure

- `src/GwsBusinessSuite.Domain`: Domain entities and core business rules
- `src/GwsBusinessSuite.Application`: Use cases, orchestration, service contracts, and DTOs
- `src/GwsBusinessSuite.Infrastructure`: Data access and external integration implementations
- `src/GwsBusinessSuite.Web`: Blazor web application
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

## Build And Test

From the repository root:

```bash
dotnet build GwsBusinessSuite.slnx
dotnet test GwsBusinessSuite.slnx
```

## Run The Blazor App

```bash
dotnet run --project src/GwsBusinessSuite.Web/GwsBusinessSuite.Web.csproj
```

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
