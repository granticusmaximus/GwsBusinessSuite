# Oqtane Module/Plugin Architecture

## How are third-party Oqtane modules built? (scaffolding tools, required interfaces/base classes)

### Takeaway
Oqtane provides an official `dotnet new` template (`Oqtane.Application.Template`) for the host app itself, and a separate in-admin "Module Creator" / CLI scaffolding path for generating new module projects; modules are Blazor component libraries that implement `IModule` (a thin marker/metadata interface) and derive UI components from the abstract `ModuleBase` class (which implements `IModuleControl`). Documentation on the exact scaffolding CLI commands for modules specifically (as opposed to the host app) is thin/unclear in official docs and had to be pieced together from a third-party developer walkthrough.

### Cited Findings
- The Oqtane **host application** is scaffolded via: `dotnet new install Oqtane.Application.Template` then `dotnet new oqtane-app -o MyCompany.MyProject` — [GitHub Discussion #5511](https://github.com/oqtane/oqtane.framework/discussions/5511)
- The official module-development doc page states prerequisites are "a working knowledge of ASP.NET Core" and client-side web tech, and recommends "Visual Studio or comparable IDE, plus the Oqtane CLI for scaffolding new module projects" — [Module Development Basics](https://docs.oqtane.org/dev/modules/module-development.html) (page is otherwise high-level only; it lists topics — module architecture, DI, lifecycle events, data access, UI components — without giving implementation specifics, and points to a YouTube "Oqtane Module Development Series" for depth)
- `IModule` is a minimal interface, defined (per GitHub source) as:
  ```csharp
  namespace Oqtane.Modules
  {
      public interface IModule
      {
          ModuleDefinition ModuleDefinition { get; }
      }
  }
  ```
  — [Oqtane.Client/Modules/IModule.cs](https://github.com/oqtane/oqtane.framework/blob/dev/Oqtane.Client/Modules/IModule.cs), corroborated by [IModule API docs](https://docs.oqtane.org/api/Oqtane.Modules.IModule.html)
- `ModuleDefinition` (the object `IModule.ModuleDefinition` returns) carries the module's metadata/manifest fields, including at minimum: `Name`, `Description`, `Version`, `ServerManagerType`, `ReleaseVersions`, `Dependencies`, `SettingsType` — [WebSearch synthesis citing Oqtane docs/discussions on module versioning](https://github.com/oqtane/oqtane.framework/discussions/2059)
- `ModuleBase` is `public abstract class ModuleBase : ComponentBase, IModuleControl` — it extends Blazor's `ComponentBase` and implements `IModuleControl`. It exposes: injected `ILogService LoggingService`, `IJSRuntime JSRuntime`, `SiteState SiteState`, `IHttpContext HttpContext` (static/prerender only); cascading parameters `PageState PageState` and `Models.Module ModuleState`; properties `SecurityAccessLevel` (defaults to View), `Title`, `Actions`, `RenderMode`, `Resources` (scripts/stylesheets); navigation helpers `NavigateUrl()`, `EditUrl()`, `FileUrl()`, `ImageUrl()`; lifecycle/UI methods `OnAfterRenderAsync()`, `ShouldRender()`, `AddModuleMessage()`, `ClearModuleMessage()`, `ShowProgressIndicator()`, `HideProgressIndicator()`, `ReplaceTokens()`, `SetModuleTitle()`, `SetPageTitle()`, and an async `Log()` method — [ModuleBase.cs on GitHub](https://github.com/oqtane/oqtane.framework/blob/dev/Oqtane.Client/Modules/ModuleBase.cs), [ModuleBase API docs](https://docs.oqtane.org/api/Oqtane.Modules.ModuleBase.html)
- A third-party developer walkthrough (Ryan Jagdfeld) describes generating a module project via the Admin Dashboard's Module Management → "Create Module," which prompts for owner name, module name, description, and a template type, and generates a 5-project solution **one directory level up from** the Oqtane framework folder: an **Oqtane.Server**-referencing test host, a **Client** project (browser-side Razor components), a **Server** project (backend/API logic), a **Shared** project (models shared client/server, using `[Table("TableName")]` attributes and an `IAuditable` interface), and a **Package** project whose job is to build the distributable NuGet package — [Build a Module Extension for Oqtane](https://ryanjagdfeld.com/!/7/build-a-module-extension-for-oqtane)
- That same walkthrough notes the Server project's migration files use `EntityBuilder` classes to define table schema, PKs, and FK relationships in a database-provider-agnostic way — [same source](https://ryanjagdfeld.com/!/7/build-a-module-extension-for-oqtane)
- Related interface names surfaced in search but not independently confirmed by primary source in this pass: `IModuleControl`, `IModuleContentRenderer`, `ServerManagerType` (an install/uninstall hook type referenced from `ModuleDefinition`) — flagged as needing direct source verification (see Gaps).

### Inferences
- The "Module Creator" is effectively a code generator invoked from inside the running Oqtane admin UI (Module Management → Create Module) rather than a standalone `dotnet new` template specifically for modules — this is a different mechanism from the `oqtane-app` host template. This distinction (host scaffolding via CLI vs. module scaffolding via an in-app wizard) is inferred from combining the two source sets above, not stated in one single source.
- `ModuleBase`'s design (inheriting `ComponentBase`) confirms modules are ordinary Razor/Blazor components at the UI layer — there is no separate rendering engine; a "module" is a Blazor component that happens to receive `PageState`/`ModuleState` via cascading parameters and gets slotted into a page's layout by the Oqtane page-rendering pipeline.

### Gaps
- Could not verify from primary source in this pass whether there is a real, separate "Oqtane CLI" (e.g. a `dotnet tool`) distinct from the `oqtane-app` template specifically for scaffolding new *modules*, versus that being done only through the in-admin Module Creator wizard. The docs page vaguely references "the Oqtane CLI for scaffolding new module projects" without naming the actual command.
- Full member list of `IModuleControl`, `IModuleContentRenderer`, `IPortable`, `ISearchable`, `IInstallable` and other optional module-capability interfaces referenced in scattered community discussions were not independently pulled from source in this research pass.

---

## How are modules packaged for distribution, and what's inside a package?

### Takeaway
Oqtane modules are packaged and distributed as standard **NuGet packages (`.nupkg`)**, produced by a dedicated "Package" project in the module's generated solution; the package bundles the compiled assemblies plus Razor/static assets and any SQL/EF migration artifacts, and is uploaded through the admin UI or pulled from the Oqtane Marketplace / NuGet.

### Cited Findings
- "Oqtane modules are distributed as NuGet packages," and to make one discoverable via Oqtane's "Available Modules" list you publish it to NuGet.org tagged `Oqtane` — [WebSearch synthesis of Module Management docs](https://docs.oqtane.org/manuals/system/module-management.html)
- The Module Management admin page's "Upload" tab accepts "a NuGet package as a new module to your site... the module package file with the extension `*.nupkg`, which must be a valid NuGet package for Oqtane" — [Module Management](https://docs.oqtane.org/manuals/system/module-management.html)
- The Module Deployment doc confirms the same `.nupkg` upload flow: upload via Module Management in the Administration Dashboard, click Install, receive confirmation — but does not enumerate the package's internal file layout — [Module Deployment](https://docs.oqtane.org/dev/modules/module-deployment.html)
- Actual example packages exist on NuGet.org, e.g. `Oqtane.Server` (framework core), `Oqtane.Framework`, `Oqtane.Shared`, and a real third-party/example module package `Oqtane.Survey` — [NuGet Gallery listings](https://www.nuget.org/packages/Oqtane.Server)
- Per the Jagdfeld walkthrough, "Building in Release mode generates a `.nupkg` file containing all necessary extension files," and that package "is what you will use to install your extension in other Oqtane instances" — [Build a Module Extension for Oqtane](https://ryanjagdfeld.com/!/7/build-a-module-extension-for-oqtane)
- Migration/schema artifacts (EntityBuilder-based EF Code First migration classes) live in the module's Server project under a `/Migrations` (and `/EntityBuilders`) folder and travel inside the compiled Server assembly rather than as loose SQL files, in the current (v2.1+) approach — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)
- Older/1.x-era approach (per a different search synthesis) had modules ship raw versioned SQL scripts (e.g. an "Uninstall.sql" referenced from `ModuleDefinitionController.cs`) that the framework would execute up to the target version on install — this predates or coexists with the EF-based approach depending on version (see contradiction noted below) — [WebSearch synthesis referencing ModuleDefinitionController.cs and GitHub Discussion #1744](https://github.com/oqtane/oqtane.framework/discussions/1744)

### Inferences
- The package almost certainly also carries compiled Client-project assemblies (Razor component binaries) and Shared-project model assemblies, since the Client/Server/Shared split is fundamental to the generated module solution — but this was not directly confirmed from an actual unzipped `.nupkg` inspection or a documented manifest list in this pass.

### Gaps
- Could not directly enumerate a real module's `.nupkg` contents (file tree) from a primary source — no doc page or blog post in the sources fetched actually lists the package's internal folder/file structure (e.g., `lib/`, `content/`, `tools/`, whether there's an Oqtane-specific manifest file inside the nupkg beyond the standard NuGet `.nuspec`).
- Whether static assets (CSS/JS/images) are packaged inside the nupkg's `content`/`wwwroot`-style folder per NuGet convention, or handled through a separate Oqtane resource-registration mechanism (`ModuleBase.Resources`), is not confirmed with a direct source citation.

---

## How are modules installed at runtime into an already-running instance — is it truly dynamic, or does it need a restart?

### Takeaway
Assembly loading is genuinely dynamic in the .NET sense (Oqtane uses `System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath` to load module DLLs from the bin folder into the running process, rather than relying on default AppDomain-at-startup loading), but in practice **installing a new module still requires — and the docs explicitly instruct — a full Restart of the Oqtane application** before the new module is usable; this is not a cosmetic recommendation but a stated hard requirement in multiple official/semi-official sources. The claim that Oqtane installs modules into a live site "without a redeploy" is best understood as "without a new build/deploy pipeline run or IIS-level redeploy," not "without any process-level reload."

### Cited Findings
- Oqtane's own blog post "Assembly Loading in Blazor and .NET Core" explains that .NET Core does **not** automatically load assemblies dropped into `/bin`; Oqtane must manually call `AssemblyLoadContext.LoadFromAssemblyPath()` server-side to bring a module's compiled assembly into the running process, with dependent assemblies in the same folder resolved automatically, after which `assembly.GetTypes()` (reflection) is used to discover module types — [Assembly Loading in Blazor and .NET Core](https://www.oqtane.org/blog/!/11/assembly-loading-in-blazor-and--net-core)
- On the Blazor **client** side (WebAssembly-hosted scenario), Oqtane instead downloads the module assembly as a byte array over HttpClient and loads it via `Assembly.Load()`; the same post explicitly flags a limitation: "if the assembly you are loading has any dependencies which do not yet exist on the client then the load operation will fail," requiring a custom dependency-resolution/download-ordering mechanism — [same source](https://www.oqtane.org/blog/!/11/assembly-loading-in-blazor-and--net-core)
- Despite the above being real dynamic in-process assembly loading, the official Module Management documentation itself states that after creating/installing a module, "you will be asked to compile the module project and then restart to start working with your module" — [Module Management](https://docs.oqtane.org/manuals/system/module-management.html)
- The "How To Restart Oqtane" doc confirms restart is a normal, expected operational step, "the most common [reason] being after installing/updating an extension"; for production/IIS it's triggered via `/admin/system` → Restart, and a manual fallback is explicitly given: "the simplest method to do this is to open the `web.config` and save it again. This will restart Oqtane" (the classic ASP.NET/IIS technique of touching `web.config` to force an app-pool worker-process recycle) — [How To Restart Oqtane](https://docs.oqtane.org/manuals/how-to/restart/index.html)
- In Visual Studio/dev hosting, the same doc notes restart just stops/starts the debug process ("it will only shut down Oqtane, since Visual Studio will terminate the process") — [same source](https://docs.oqtane.org/manuals/how-to/restart/index.html)
- A GitHub discussion thread states plainly: "You do not load assemblies dynamically and there is no way how to load them without restart. If you do not have assemblies in bin before app start they will not be loaded." — [GitHub Discussion #1194, "Debugging of the External Module, Hot Reload simulation"](https://github.com/oqtane/oqtane.framework/discussions/1194)
- That same discussion documents a **developer-only workaround** to avoid manual restarts during local development: add a Watch Include directive for `*.dll` to the `Oqtane.Server` project file and run under `dotnet watch run`, so `dotnet watch`'s file-system watcher triggers an automatic process reload when a new external-module DLL is copied into the server's output directory — this is `dotnet watch`'s generic hot-reload/file-watch feature repurposed for modules, not an Oqtane-native hot-swap mechanism, and it is explicitly a dev-time simulation, not how production installs behave — [GitHub Discussion #1194](https://github.com/oqtane/oqtane.framework/discussions/1194)
- A related Oqtane blog post title, "Blazor Server and Hot Reload Challenges," independently signals that hot reload of Blazor Server code (which modules are built from) is a known pain point the framework has had to work around rather than solved cleanly — [Blog | Blazor Server and Hot Reload Challenges](https://www.oqtane.org/blog/!/45/blazor-server-and-hot-reload-challenges) (title/existence confirmed via search; full content not fetched in this pass)
- EF-based module database migrations (see next section) are stated to "run automatically during framework startup" — meaning schema changes for a newly-installed module's tables are applied at the next app start/restart, not the instant the package is uploaded — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)

### Inferences
- Putting these findings together: Oqtane's "signature" claim of installing modules into a live app is accurate at the level of *packaging and delivery* (you don't need to rebuild/redeploy the whole solution from source, push new code to a repo, or run a CI/CD pipeline — you literally upload a `.nupkg` through a web UI while the site is up) and at the level of *assembly-loading mechanism* (it does use real `AssemblyLoadContext` dynamic loading rather than something requiring a fresh compile of the host). But it is **not** hot in the stronger sense of "the new module becomes usable with zero interruption to the running process" — a Restart (an in-process app restart at minimum, or an IIS worker-process recycle via the web.config-touch technique) is the explicitly documented, required final step, and multiple community/maintainer statements confirm there is no supported way to skip it. The "no redeploy" framing in marketing material should be read as "no redeploy pipeline / no server access needed," not "no process restart."
- The distinction between the app-level "Restart" command (`/admin/system`) and a true OS/IIS-level app-pool recycle is blurred in the docs — in IIS hosting the two may be nearly equivalent (touching web.config causes IIS to recycle the worker process), while under Visual Studio/Kestrel, "Restart" from the admin UI vs. stopping the debugger appear to be handled differently, but the exact internal implementation (e.g., whether `/admin/system` Restart calls `IHostApplicationLifetime.StopApplication()` and relies on a supervising process like IIS/systemd/dotnet-watch to bring it back up, versus doing something more surgical) was not found documented in the sources fetched.

### Gaps
- Could not find primary-source (code-level) confirmation of exactly what the `/admin/system` "Restart" button does internally (e.g., which service/method it calls) — the docs describe user-facing behavior only, not the implementation.
- Could not confirm whether there is any narrower class of "hot" changes in Oqtane that genuinely need no restart at all (e.g., theme/CSS changes, settings changes, content edits) as distinct from module *code* installation — the research scope here was module installation specifically, and restart does appear required for that case across every source found.

---

## How does module registration work (manifest/descriptor, DB table, reflection discovery, DI)?

### Takeaway
Registration is a combination of reflection-based discovery of `IModule`-implementing types at startup/load time plus persistence of module metadata in a database table (`ModuleDefinitions`); there is no separate standalone manifest file format independent of the `ModuleDefinition` object exposed through code.

### Cited Findings
- The `IModule` interface's sole job is to expose a `ModuleDefinition` object, which functions as the in-code manifest: it carries `Name`, `Description`, `Version`, `ServerManagerType`, `ReleaseVersions`, `Dependencies`, `SettingsType` — [WebSearch synthesis of Oqtane docs, cross-referenced with GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)
- After assemblies are loaded into the process via `AssemblyLoadContext`, Oqtane uses reflection (`assembly.GetTypes()`) to find and instantiate module types — this is the discovery mechanism, run against whatever is present in `/bin` at process start — [Assembly Loading in Blazor and .NET Core](https://www.oqtane.org/blog/!/11/assembly-loading-in-blazor-and--net-core)
- The installed module's version is compared against the module's declared `Version`/`ReleaseVersions` by consulting a **`ModuleDefinitions` database table**, which stores the currently-installed version so the framework knows which migrations (if any) still need to run — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)
- An older reference (found via search synthesis, not independently fetched) to `ModuleDefinitionController.cs` containing "db uninstall" logic that executes a script literally named `Uninstall.sql` suggests the controller responsible for module lifecycle (install/uninstall/upgrade) is server-side and DB-table-driven — [WebSearch synthesis referencing ModuleDefinitionController.cs](https://github.com/oqtane/oqtane.framework/discussions/1744)

### Inferences
- There does not appear to be a separate declarative manifest file (like a `module.json` or XML descriptor) shipped in the package that Oqtane parses independently of the compiled code — the "manifest" is effectively the `ModuleDefinition` object returned by the module's `IModule` implementation, discovered via reflection once the assembly is loaded. This is inferred from the consistent absence of any manifest-file discussion across all sources and the explicit description of `ModuleDefinition`-via-reflection as the mechanism.

### Gaps
- Could not confirm from primary source whether module registration also involves explicit DI container registration (e.g., a module registering its own scoped services in `ConfigureServices`) as a discovery/self-registration convention, versus Oqtane's core framework doing all service registration generically based on interface discovery. This is a plausible design (common in plugin frameworks) but unverified here.
- Did not obtain the actual `ModuleDefinitions` table schema (columns) from source.

---

## Versioning, upgrades, marketplace, and sandboxing/security model

### Takeaway
There is a real, named "Oqtane Marketplace" (a separate site, oqtane.net, run by "Oqtane Labs Inc") distinguishing open-source and commercial modules, reachable both from within the admin Module Management UI and standalone; versioning is handled via the module's own `Version`/`ReleaseVersions` metadata and NuGet package versions. No sandboxing, code-signing, or trust-tier security model for third-party module code was found documented anywhere in official sources — third-party modules run fully in-process with the same trust level as core code.

### Cited Findings
- The Module Management admin page has a "Browse Marketplace" option letting admins filter modules as "Open Source" or "Commercial," with search and sort — [Module Management](https://docs.oqtane.org/manuals/system/module-management.html)
- A standalone Oqtane Marketplace site exists at oqtane.net, described as operated by "Oqtane Labs Inc," based in Jupiter, Florida, serving as a hub for the Oqtane Framework community; the homepage content fetched did not itself detail listing mechanics, vetting, or update workflows — [Home - Oqtane Marketplace](https://www.oqtane.net/)
- Discoverability into Oqtane's in-admin "Available Modules" list is achieved simply by publishing a package to NuGet.org tagged `Oqtane` — there is no stated separate submission/review/approval process for that path — [Module Management docs synthesis](https://docs.oqtane.org/manuals/system/module-management.html)
- No source found in this research pass (official docs, GitHub discussions, or blog posts) describes any sandboxing, code-signing, permission/capability model, or runtime isolation for third-party module assemblies — modules load via `AssemblyLoadContext.LoadFromAssemblyPath` into the same process and (implicitly) the same trust/security context as the host application and all other modules, with full access to the same AppDomain/process memory space and any APIs the host exposes. This absence was confirmed by a targeted search that returned no Oqtane-specific results on the topic — [WebSearch: "Oqtane marketplace security sandboxing third party module trust"](https://github.com/oqtane/oqtane.framework/discussions/732) (only tangential/off-topic results returned; see Gaps)
- Module version fields (`Version`, `ReleaseVersions`) drive whether pending EF migrations for that module need to run on the next startup, functioning as the upgrade mechanism — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)

### Inferences
- Given that module code loads directly into the same .NET process as the Oqtane host with no described isolation boundary, a malicious or buggy third-party module could, in principle, access/corrupt any in-process data, other modules' data, or host resources — this is an architectural inference from the confirmed loading mechanism (in-process `AssemblyLoadContext`, not out-of-process plugin hosts, not WASM sandboxing, not `AppDomain`-based isolation which .NET Core removed) rather than a claim found explicitly stated by Oqtane maintainers.
- The "Open Source vs. Commercial" marketplace filter is a licensing/monetization distinction, not a security/trust-tier distinction — no evidence found that "Commercial" implies any additional vetting.

### Gaps
- Could not find any Oqtane maintainer statement (blog, docs, GitHub discussion) explicitly addressing the security implications of running untrusted third-party code in-process, or any mitigation the project recommends (e.g., "only install modules from trusted sources"). This may be a genuine documentation gap in the project rather than a search-tooling failure — it is worth flagging in the report as notably absent for a framework whose core value proposition is installing third-party code into a live site.
- Could not determine whether the Oqtane Marketplace applies any automated or manual review to submitted packages before listing them.
- A GitHub Discussion titled "Commercial Ecosystem" (#732) surfaced in search but was not fetched/read in this pass; it may contain maintainer commentary relevant to the marketplace's business/trust model and would be worth a follow-up fetch if more budget were available.

---

## How do modules interact with the database (EF Core migrations vs. SQL scripts)?

### Takeaway
Current Oqtane versions (v2.1+) use **EF Core Code-First migrations** for module schema, with a database-provider-agnostic `EntityBuilder` helper API rather than hand-written SQL, replacing an earlier (1.x-era, or optional) raw-SQL-script approach; one source describes a third-party component called **DbUp** for script-based schema execution, which appears to conflict with the EF-migration description and is flagged below as an unresolved discrepancy likely explained by version differences.

### Cited Findings
- "In the 2.1 release Oqtane was enhanced to support multiple database platforms - and this required a transition to using Migrations, with support for modules as well. However, it is not required for modules, and if you are only planning on using a specific database platform such as SQL Server then you are still able to use the script-based approach which was utilized in the 1.x release." — [WebSearch synthesis of Oqtane docs/discussions](https://docs.oqtane.org/guides/migrations/index.html)
- Per GitHub Discussion #2059: module `/Migrations` folders contain migration classes named with version-encoded identifiers (e.g. `01000100_AddColumn.cs`) decorated with attributes like `[Migration("Module.01.00.01.00")]`; migrations call into `EntityBuilder` helper methods (e.g. `moduleEntityBuilder.AddStringColumn("YourColumn", 50, true)`) rather than raw SQL; execution happens automatically at framework startup, comparing the module's installed version (tracked via the `ModuleDefinitions` table and version fields on `IModule`) against declared `ReleaseVersions`, and an EF `__EFMigrationsHistory` table tracks what has run per (tenant) database — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)
- A separate WebSearch synthesis, sourced from discussion of `ModuleDefinitionController.cs`, states: "Oqtane does not currently use EF Core for Migrations, instead it uses a third party component called DbUp which relies on the execution of SQL scripts for creating and modifying database schemas," and separately that "SQL scripts are embedded in the module assembly, and when an external module is installed it needs to get the version from the module and run all of the scripts <= the version," with an `Uninstall.sql` script referenced for teardown — [WebSearch synthesis, GitHub Discussion #1744](https://github.com/oqtane/oqtane.framework/discussions/1744)
- Multi-tenant behavior: migrations run "automatically during framework startup **across all databases in multi-tenant installations**" — i.e., a module's schema is applied to every tenant's database at the next app start, not selectively — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)
- Diagnosing migration failures is done via the server's `/Content/Log/error.log` file — [GitHub Discussion #2059](https://github.com/oqtane/oqtane.framework/discussions/2059)

### Inferences
- The two sources' apparent conflict (DbUp/raw-SQL vs. EF-Migrations/EntityBuilder) most likely reflects a real historical transition: Oqtane 1.x used script-based DbUp-style installs, and Oqtane 2.1+ moved to EF Core Code-First migrations with the `EntityBuilder` abstraction to support multiple database providers (SQLite/SQL Server/etc.), while still allowing a legacy/simplified raw-SQL path for SQL-Server-only modules. This reconciliation is an inference, not something one single source states outright — the report writer should treat the exact current-version behavior as version-dependent rather than a single fixed answer.
- Either way, both mechanisms confirm migrations/schema changes execute **at application startup**, reinforcing the earlier finding that a restart is functionally required for a newly installed module's database tables to be created.

### Gaps
- Could not directly fetch and read the primary `docs.oqtane.org/guides/migrations/database-migration.html` or `.../guides/migrations/index.html` page content in enough depth to definitively state which mechanism (EF migrations vs. DbUp/SQL scripts) is authoritative for the *current* released version as of this research (Sept 2026) — the fetched page returned only a thin summary and could not confirm DbUp vs. EF Core as the present default. This should be flagged as an open discrepancy in the final report rather than resolved by guessing.
- Did not verify the exact current Oqtane major version's migration approach against the changelog/release notes directly (e.g., whether DbUp was ever real or a misreading of "database-provider scripts" terminology, or whether it was fully removed by a specific version number).
