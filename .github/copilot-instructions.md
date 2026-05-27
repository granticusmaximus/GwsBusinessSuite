# GWS Business Suite Engineering Rules

Apply these rules to every code change in this repository.

## Architecture

- Preserve Clean Architecture boundaries.
- Domain contains entities, value objects, and business rules only.
- Application contains use cases, orchestration, abstractions, DTOs, and validation.
- Infrastructure contains database access, HTTP clients, external integrations, and framework-specific implementations.
- Web contains Blazor UI composition only. Keep components thin and move business logic to Application services.
- Dependencies must flow inward only: Web -> Application, Infrastructure -> Application and Domain, Application -> Domain, Domain -> nothing.

## Clean Code

- Remove dead code, scaffold leftovers, and placeholder types when they are no longer needed.
- Prefer focused classes and methods with a single responsibility.
- Avoid god classes, hidden side effects, and mixed concerns.
- Use explicit, descriptive names for types, methods, and variables.
- Favor strongly typed models over anonymous or loosely shaped data once behavior stabilizes.
- Keep logging structured and actionable around external boundaries and failure points.

## Blazor

- Keep Razor components primarily responsible for state, rendering, and user interaction.
- Move business workflows, HTTP calls, persistence rules, and transformation logic into Application or Infrastructure.
- Surface loading, success, and failure states explicitly in the UI.

## Testing

- Every meaningful feature or business rule change must include or update automated tests.
- Add tests at the narrowest layer that validates the behavior.
- Prefer Application-layer tests for orchestration and business outcomes.
- Do not keep empty or placeholder tests.

## Change Workflow

- Start with the smallest owning abstraction for the behavior being changed.
- Fix root causes rather than layering on UI-only workarounds.
- Avoid unrelated refactors during feature work unless they unblock the task or remove clear scaffold noise.
- Run `dotnet build` and `dotnet test` after changes.
