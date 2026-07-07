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

## Key Documents

- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `docs/SEO_ARTICLE_GENERATOR.md`
- `docs/ARTICLE_IMAGE_BRANDING.md`
