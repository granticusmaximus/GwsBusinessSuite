# Architecture

GWS Business Suite uses a Clean Architecture-inspired structure:

- Domain: business entities and rules
- Application: service contracts and orchestration
- Infrastructure: SQLite, external APIs, deployment integrations
- Web: Blazor UI
- Tests: unit and integration tests

The SEO Article Generator lives in the Application layer as `SeoArticleGeneratorService`, persists drafts as `SeoArticleDraft`, and calls Ollama through `IOllamaService`. Approval, rejection, and revision requests are modeled as state transitions on the draft.

Dependency rule: Web depends on Application and Infrastructure; Infrastructure depends on Application and Domain; Application depends on Domain; Domain depends on nothing.

## Repository rules

- Remove unused scaffold artifacts early so they do not become accidental architecture.
- Keep Blazor components thin and push workflow logic into Application services.
- Add or update tests for every meaningful business behavior change.
- Treat external integrations, persistence, and framework concerns as Infrastructure responsibilities.
- Favor small, explicit abstractions over placeholder classes and catch-all services.
