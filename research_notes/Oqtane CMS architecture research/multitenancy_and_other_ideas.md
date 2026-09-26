# Oqtane Multi-Tenancy Deep Dive + Other Standout Architectural Ideas

## What is Oqtane's tenancy model technically (shared DB + discriminator vs. separate databases vs. mix)?

### Takeaway
It is a genuine hybrid: **Tenant = an isolation boundary tied to a physical database** (its own connection string), while **Site = a discriminator-column row inside a tenant's database** (a `SiteId` FK on content tables). So "multi-tenant" in Oqtane can mean either full database isolation (one DB per tenant) or a shared database with multiple tenants/sites distinguished by `TenantId`/`SiteId` columns — the framework explicitly supports both, and the choice is an operator decision, not a hardcoded architecture.

### Cited Findings
- The `Tenant` model doc string reads: "Describes a Tenant in Oqtane. Tenants can contain multiple Sites and have all their data in a separate Database." Its mapped properties are `TenantId`, `Name`, `DBConnectionString`, `DBType` (added v2.1.0), and `Version` — i.e., a tenant record is fundamentally a **named database connection**, not just a logical grouping key. — [Tenant.cs](https://raw.githubusercontent.com/oqtane/oqtane.framework/dev/Oqtane.Shared/Models/Tenant.cs)
- The `Site` model has a `TenantId` property but it is marked `[NotMapped]` in the Site table itself — meaning Site rows don't carry a persisted TenantId column in their own table; the Tenant/Site relationship is instead resolved by which physical tenant database the Site row lives in. — [Site.cs](https://raw.githubusercontent.com/oqtane/oqtane.framework/dev/Oqtane.Shared/Models/Site.cs)
- Core content tables (Page, Module, Folder, Permission, SearchContent) carry a `SiteId` foreign key; `Log` carries a nullable `SiteId`. This is the classic shared-schema-with-discriminator-column pattern, but it operates **within** a tenant's database, not across the whole install. — [Oqtane Database Schema Documentation](https://docs.oqtane.org/guides/database-management/database-schema.html)
- Maintainer sbwalker (Oqtane's creator) stated directly in a GitHub discussion: "Oqtane supports shared or isolated tenancy. Tenants are managed in the 'master' database and Sites are managed in each individual tenant database." — [Discussion #979 "Uniquely identify a tenant and a site"](https://github.com/oqtane/oqtane.framework/discussions/979)
- The oqtane.org homepage explicitly advertises "shared and isolated tenancy models" as a feature, i.e., both a shared-database mode and a database-per-tenant mode are first-class supported configurations. — [oqtane.org homepage](https://www.oqtane.org/)
- Third-party writeup: "each tenant can either use a separate database or share the same one as the host" — confirms the operator-configurable nature of the isolation boundary. — [Joche Ojeda, "Understanding Multi-Tenancy in Oqtane"](https://www.jocheojeda.com/2025/10/08/understanding-multi-tenancy-in-oqtane-and-how-to-set-up-sites/)

### Inferences
- The correct mental model is: **one master/host database** catalogs all Tenants (name + connection string + DB type), and Oqtane's EF Core layer dynamically switches `DbContext` connection strings per-request based on which Tenant the resolved Alias belongs to. Within a given tenant's database, Sites are just rows discriminated by `SiteId`, and content tables cascade that discriminator down.
- This means Oqtane isn't "one DbContext, one discriminator column, done" — it's "one DbContext class, N possible physical databases (chosen dynamically per request), each internally using a discriminator column for its own Sites." That's a meaningfully more sophisticated (and more operationally flexible) design than a naive single-shared-database SaaS pattern.

### Gaps
- Could not directly inspect the actual EF Core `DbContext`/`TenantResolver`/connection-switching code in `Oqtane.Server` (no direct repo browse tool was used beyond targeted raw-file fetches of model classes; a full source clone would be needed to see the exact DI/middleware mechanism). The mechanism is inferred from docs and model shapes, not confirmed by reading the resolver class itself.
- Could not confirm whether a single Oqtane process can have tenant A on SQL Server and tenant B on PostgreSQL simultaneously (the `DBType` field per-Tenant suggests yes, but this is not explicitly confirmed by a source).

---

## What is the relationship between "Tenant" and "Site" — same concept, or is one-tenant-many-sites modeled explicitly?

### Takeaway
They are explicitly distinct concepts: **one Tenant can host multiple Sites**, and a Site is scoped to exactly one Tenant (i.e., a Site cannot span tenant databases). This is modeled via the Tenant owning a physical database, and Site being a row (with `SiteId`) inside that database.

### Cited Findings
- "A Tenant can have multiple sites, and a Site is a distinct portal that shares its membership with the other portals in the same Tenant." — [WebSearch synthesis citing Oqtane site-management docs](https://docs.oqtane.org/manuals/system/site-management.html)
- The `Alias` model — which is what a real HTTP request actually resolves against — carries **both** `TenantId` and `SiteId` as mapped FK columns, and computes an unmapped `SiteKey` property as `"TenantId:SiteId"`, explicitly treating the tenant+site pair as a compound identity for a given site instance. — [Alias.cs](https://github.com/oqtane/oqtane.framework/blob/dev/Oqtane.Shared/Models/Alias.cs)
- Per sbwalker's clarification, Tenants live in the master DB, Sites live inside each tenant's own DB — confirming Site is subordinate to, and contained within, exactly one Tenant. — [Discussion #979](https://github.com/oqtane/oqtane.framework/discussions/979)

### Inferences
- "Shared membership within a tenant" (per the site-management synthesis) implies that when multiple Sites live in the same Tenant database, they likely share the same **User** table (since Users, like other tables, live in the tenant DB) — i.e., isolation between sibling Sites in one Tenant is logical/row-level, not physical. Full user-store isolation only happens when Sites are placed in *separate* Tenants (separate databases). This distinction is important for the "how isolated really" question below.

### Gaps
- Could not get a primary-source (docs or code) confirmation of exactly which tables are Site-scoped vs. Tenant-wide for Users specifically — the User model summary noted a "SiteId" reference described as "not mapped, contextual reference," which is ambiguous and wasn't independently verified in this pass. Flagging this as needing direct code inspection (e.g., `Oqtane.Shared/Models/User.cs`, `UserRepository.cs`) if higher precision is needed later.

---

## How is tenant/site resolution done at request time?

### Takeaway
Resolution is **hostname/URL-based via an "Alias" record**, not a session-based site picker or explicit login-time selection — a request's `{scheme}://{hostname}/{path}` is matched against Alias rows (which can be full domains, subdomains, or path segments) to determine both Tenant and Site before any tenant-scoped middleware executes.

### Cited Findings
- "A hostname is basically the domain that points to one of your tenants in Oqtane, and when a request arrives, the server reads the hostname from the request and routes it to the correct tenant." — [WebSearch synthesis of Oqtane docs/community discussion](https://github.com/oqtane/oqtane.framework/discussions/1947)
- A full Oqtane route is documented as: `{scheme}://{hostname}/{aliaspath}/{pagepath}/*/{moduleid}/{action}/!/{urlparameters}?{query}#{fragment}` — the alias/hostname is the first-class routing unit, ahead of page path. — [Oqtane docs, Route class reference](https://docs.oqtane.org/api/Oqtane.Models.Route.html)
- Alias `Name` can be a bare domain (`oqtane.me`), a subdomain (`www.oqtane.me`), or a domain+path (`oqtane.me/products`) — all three resolution styles (domain, subdomain, path-prefix) are supported simultaneously by the same Alias mechanism, not mutually exclusive modes. — [Class Alias | Oqtane Docs](https://docs.oqtane.org/api/Oqtane.Models.Alias.html)
- "Virtual folders" let multiple sites share one `localhost:port` in dev via path suffixes (`site1 = localhost:port#`, `site2 = localhost:port#/site2`) — confirming path-based resolution is a supported, documented pattern, not just a domain-based one. — [WebSearch synthesis of Oqtane site-management docs](https://docs.oqtane.org/manuals/system/site-management.html)
- `IsDefault` on Alias: non-default aliases redirect to the default alias for the tenant/site, explicitly to avoid duplicate-content SEO problems — a deliberate, documented design choice, not an oversight. — [GitHub Discussion #1947](https://github.com/oqtane/oqtane.framework/discussions/1947)
- General middleware pattern note (from a third-party ASP.NET Core multi-tenancy explainer used to corroborate the mechanism class): tenant-resolving middleware must run before anything (e.g. MVC/Blazor) that needs tenant context. — [Michael McKenna, ASP.NET Core tenant resolution](https://michael-mckenna.com/multi-tenant-asp-dot-net-core-application-tenant-resolution)

### Inferences
- Because Alias carries `TenantId` + `SiteId` directly, hostname resolution is effectively a **single indexed lookup** (`Alias.Name` → Tenant+Site), which is efficient and simple, and the same lookup table elegantly unifies three different addressing schemes (full domain, subdomain, path-prefix) that many other multi-tenant frameworks handle with three separate resolution strategies.

### Gaps
- Did not locate and read the actual middleware/resolver class (e.g., something like `TenantResolverMiddleware` or `SiteState` initialization code) in `Oqtane.Server`; the mechanism described above is reconstructed from the Alias data model and documentation rather than from reading the resolution code path directly.

---

## Can one deployment host multiple independent sites with separate domains/users/content/themes — how isolated are they really?

### Takeaway
Yes, and isolation is **tunable rather than fixed**: sites in different Tenants get real database-level isolation (separate user stores, separate content, separate everything), while sites in the *same* Tenant get logical isolation only — separate content/theme/settings per Site row, but a shared user table ("shared membership") across sibling sites.

### Cited Findings
- "A Site is a distinct portal that shares its membership with the other portals in the same Tenant" — explicit confirmation that same-tenant multi-site is NOT full user-store isolation; it's shared users across an operator's sites, with per-site content/theme separation layered on top. — [Site Management docs synthesis](https://docs.oqtane.org/manuals/system/site-management.html)
- Cross-tenant isolation, by contrast, is real database separation: "Tenants can contain multiple Sites and have all their data in a separate Database" (from the Tenant model's own doc comment) — this is as strong an isolation guarantee as physically-separate-database multi-tenancy gets. — [Tenant.cs](https://raw.githubusercontent.com/oqtane/oqtane.framework/dev/Oqtane.Shared/Models/Tenant.cs)
- Each Site has independently settable `DefaultThemeType`, `DefaultContainerType`, `AdminContainerType`, `LogoFileId`, `FaviconFileId`, `TimeZoneId`, `CultureCode`, `HeadContent`/`BodyContent` injection points, and its own `Pages`/`Languages`/`Themes` collections — so presentation, branding, localization, and content are all genuinely per-Site, even within a shared-membership Tenant. — [Site.cs](https://raw.githubusercontent.com/oqtane/oqtane.framework/dev/Oqtane.Shared/Models/Site.cs)
- Real-world third-party confirmation: "Each tenant maintains own unique visual theme and independent site settings, demonstrating isolation at the presentation and configuration levels." — [Joche Ojeda blog](https://www.jocheojeda.com/2025/10/08/understanding-multi-tenancy-in-oqtane-and-how-to-set-up-sites/)
- Permission model (see "other ideas" section below) can additionally be used to segment access even within a shared-user Tenant, via the extended entity-permission system — so operators who want tighter same-tenant isolation than "everyone in the user table can theoretically be granted access anywhere" have a permission-based lever, not just the Tenant/Site split. — [Oqtane Blog: Permission Enhancements](https://www.oqtane.org/blog/!/55/permission-enhancements)

### Inferences
- The honest characterization is a **two-tier isolation model**: Tenant boundary = hard/physical isolation (separate DB, separate users, use this for genuinely unrelated customers/organizations); Site boundary within one Tenant = soft/logical isolation (shared users, separate content/branding/theme, use this for one organization's multiple properties/brands/microsites). This maps well onto real hosting-provider use (each customer = a Tenant) vs. one organization running several branded portals off one shared user base (each portal = a Site).

### Gaps
- No source directly confirmed whether cross-Site data leakage is prevented purely by application-layer `SiteId` filtering (i.e., a bug in a module's query could theoretically leak another Site's data within the same Tenant DB) versus some additional DB-level enforcement (e.g., row-level security). This is a real architectural question for "how deep is same-tenant Site isolation" that remains unverified — treat same-tenant Site isolation as **logical/query-discipline-based**, not database-enforced, unless proven otherwise.

---

## Site aliases / subsites within one tenant — transferable idea for a single-tenant app?

### Takeaway
Yes — this is the one piece of Oqtane's tenancy machinery genuinely worth borrowing for a single-tenant app: the **Alias-as-first-class-entity** pattern, where a Site can have many Alias rows (multiple domains, subdomains, or `/path` prefixes all pointing at the same Site, with one flagged `IsDefault` and non-default ones redirecting to it) is a clean way to run a distinct sub-brand or micro-site off the same content system without duplicating the whole app.

### Cited Findings
- "Sites can have multiple Aliases" and an Alias's `Name` can be a bare domain, a subdomain, or `domain/path` — meaning one Site's content can simultaneously be reachable at `mainbrand.com`, `microsite.mainbrand.com`, and `mainbrand.com/microbrand`, all resolving through the same underlying Site/content store. — [Class Alias | Oqtane Docs](https://docs.oqtane.org/api/Oqtane.Models.Alias.html)
- "You can create sub-websites under the main site by using a forward slash ('/') followed by the desired name (e.g., `/subsite`)" — explicit documented support for path-based sub-sites hanging off a primary domain. — [Site Management docs synthesis](https://docs.oqtane.org/manuals/system/site-management.html)
- The `IsDefault` + redirect-to-default behavior is a deliberate SEO safeguard (avoids duplicate-content penalties across multiple aliases of the same site) — a detail worth copying if building an analogous multi-alias feature, since it's an easy mistake to omit. — [GitHub Discussion #1947](https://github.com/oqtane/oqtane.framework/discussions/1947)
- Practically, this is used for exactly the "same content, multiple front doors" scenario: virtual folders in dev (`localhost:port/site2`) mapping to production domains (`www.site2.com`) is the same Alias mechanism used for environment aliasing, not just branding aliasing — showing the abstraction is reused for multiple purposes (branding sub-sites, dev/prod environment mapping, SEO canonicalization) from one data model. — [GitHub Discussion #1947](https://github.com/oqtane/oqtane.framework/discussions/1947)

### Inferences
- For a single-tenant app, the transferable subset is narrow but concrete: a table of `(HostPattern, PathPrefix, IsDefault, SiteId-or-equivalent)` rows resolved at the top of the request pipeline, used to let one existing content/domain model answer under several different hostnames/paths (e.g., a sub-brand microsite, a partner-branded portal, or a staging-vs-prod alias) — without needing Oqtane's full Tenant/database-switching layer, which genuinely is single-tenant-irrelevant. This is a much smaller, self-contained idea than "adopt multi-tenancy," and is the one piece worth flagging as reusable.

### Gaps
- None significant for this narrow question; the Alias mechanism is well-documented across multiple independent sources.

---

## How real/production-grade is Oqtane's multi-tenancy — first-class feature or rarely-used?

### Takeaway
It is a first-class, deliberately-engineered feature (not a bolt-on) with a clear design lineage: Oqtane's creator (Shaun Walker, also DNN/DotNetNuke's original creator) explicitly built Oqtane's tenancy model to fix "tenancy isolation" as one of DNN's acknowledged core limitations — but available evidence does not establish how commonly multi-tenant (vs. single-site) configurations are actually run in the wild.

### Cited Findings
- Oqtane's own comparison post states Oqtane was "designed with modularity, extensibility, and multi-tenancy in mind" specifically to "address some of DNN's most widely acknowledged core limitations (ie. automated upgrades, **tenancy isolation**, streamlined module development, etc.)" — i.e., multi-tenancy isolation was called out by name as a known DNN weakness that Oqtane was built to correct. — [Oqtane Blog: Oqtane vs DNN](https://www.oqtane.org/blog/!/19/)
- The permission-enhancement work (v3.3.0) shows multi-tenancy/multi-site concerns are still being actively deepened years into the project — the shift from static role-based policies to dynamic `EntityName:PermissionName:DefaultRoles` policies was driven by real delegated-administration needs in "larger organizations," implying genuine production usage pressure on the isolation/permission model, not a stagnant feature. — [Oqtane Blog: Permission Enhancements](https://www.oqtane.org/blog/!/55/permission-enhancements)
- Project activity is substantial and ongoing: "over 2900 pull requests from 58 contributors" and "55 official releases," and Oqtane is described as ranking "among the most active open source projects within the .NET Foundation" — general evidence of a maturely maintained project, though this is about overall project health, not multi-tenancy usage specifically. — [WebSearch synthesis citing indiebase.io/oqtane.org sources](https://www.oqtane.org/blog)
- A third-party developer's hands-on blog series (Oct 2025) walks through setting up multi-tenancy step by step as a genuinely-supported, if somewhat under-documented, feature requiring some manual hostname/database configuration — consistent with "real and functional" but not necessarily "the default, most-exercised path" for typical users. — [Joche Ojeda, "Understanding Multi-Tenancy in Oqtane"](https://www.jocheojeda.com/2025/10/08/understanding-multi-tenancy-in-oqtane-and-how-to-set-up-sites/) and [Joche Ojeda, "Setting Up Hostnames for Multi-Tenant Sites in Oqtane"](https://www.jocheojeda.com/2025/10/09/setting-up-hostnames-for-multi-tenant-sites-in-oqtane/)

### Inferences
- The fact that a third party felt it worth writing a multi-part hands-on tutorial series on "how to actually set up" multi-tenancy in late 2025 is weak circumstantial evidence that it's not a one-click, heavily-trodden path for most users — if it were the dominant deployment mode, it would likely be better-trodden in mainstream docs/tutorials rather than needing community explainer posts.

### Gaps
- No source directly states what fraction of real-world Oqtane deployments run multi-tenant vs. single-site — this specific statistic/claim was not found anywhere in searches and should be treated as genuinely unknown rather than inferred. The single most relevant, honest statement available is the "designed to fix DNN's tenancy isolation limitation" framing, which speaks to design intent/pedigree, not adoption rate.

---

## Other novel/well-executed architectural ideas (outside module system, theming, page-editing UX)

### Takeaway
The most concretely well-executed non-tenancy, non-module, non-theming ideas are: (1) a **dynamic, entity-scoped permission/policy system** that moved from static role checks to runtime-composed `EntityName:PermissionName:DefaultRoles` authorization policies enabling true delegated administration; (2) a built-in **scheduled-jobs subsystem** with per-job logs, start/stop/edit controls, and a pluggable job-method model; and (3) a **headless REST API with Swagger built in by default**, treating API-first/headless consumption as a first-class mode rather than an afterthought bolted onto a server-rendered CMS.

### Cited Findings
- **Dynamic permission policies**: Oqtane 3.3.0 replaced statically-declared-at-startup ASP.NET Core authorization policies (e.g. hardcoded `PolicyNames.ViewModule`/`EditModule`) with a custom `AuthorizationPolicyProvider` that composes policies at runtime from an `"EntityName:PermissionName:DefaultRoles"` string format, usable directly as `[Authorize(Policy = "User:Read:Registered Users")]` on API methods without pre-registration — extending permission checks from UI/module-instance scope to whole-API-surface scope, enabling scenarios like a non-Administrator "Supervisor" role getting delegated access to specific API methods (e.g., an Inventory API) without per-record `EntityId` grants. — [Oqtane Blog: Permission Enhancements](https://www.oqtane.org/blog/!/55/permission-enhancements)
- The permission system is explicitly generalized: "designed with flexibility in mind so that it can be used with any entity or permission," and community usage extends it to arbitrary custom entities (e.g., per-client-record permissions to prevent cross-client data access in custom modules) — i.e., the permission table isn't CMS-specific, it's a general-purpose ACL primitive any module author can attach to their own domain entities. — [WebSearch synthesis, GitHub Discussion #2159](https://github.com/oqtane/oqtane.framework/discussions/2159)
- **Scheduled jobs**: "The Scheduled Jobs feature in Oqtane allows you to manage recurring, scheduled tasks that are automatically executed at specified intervals," with per-job Edit (including a "Next Execution" override field), Delete, Log (view a specific job's execution logs), and Stop controls, plus a pre-built `NotificationJob` or the ability to point a job at a custom method. — [Oqtane Docs: Scheduled Jobs](https://docs.oqtane.org/manuals/system/scheduled-jobs.html) / [Discussion #1054](https://github.com/oqtane/oqtane.framework/discussions/1054)
- A real third-party module (`DNF.Projects`) demonstrates the scheduled-job extensibility pattern end-to-end: "a sample Oqtane module demonstrating a scheduled job and JSInterop visualizations using Chart.js," powering live trend analysis on a production site — evidence the job system is actually used by module authors in the wild, not just a documented-but-unused stub. — [GitHub: oqtane/DNF.Projects](https://github.com/oqtane/DNF.Projects)
- **Headless/API-first mode**: oqtane.org advertises "a rich set of secure REST-based core APIs" with "Swagger integration... included by default," and the `Site` model has a `RenderMode` property explicitly documented as "the default render mode for the site ie. Static, Interactive, **Headless**" — meaning headless is a per-site render-mode toggle within the same core, not a separate product/fork. — [oqtane.org homepage](https://www.oqtane.org/) and [Site.cs](https://raw.githubusercontent.com/oqtane/oqtane.framework/dev/Oqtane.Shared/Models/Site.cs)
- **MAUI/mobile**: ".NET MAUI / Blazor Hybrid support" was introduced in v3.2.0 (Sep 2022), letting the same Blazor component model target native desktop/mobile shells alongside the web app — reusing UI code across web and native rather than maintaining a separate mobile codebase. — [Oqtane Roadmap](https://docs.oqtane.org/guides/roadmap/index.html)
- Third-party engineering-quality endorsement (independent of Oqtane's own marketing): "The source code is clean and educational—perfect for learning by reading. The Oqtane team is very responsive on GitHub." — [WebSearch synthesis of Joche Ojeda's "My Journey Exploring the Oqtane Framework"](https://www.jocheojeda.com/2025/10/13/my-journey-exploring-the-oqtane-framework/)

### Inferences
- The permission-policy redesign (static-declared-at-startup → dynamically-composed-string-based policies resolved by a custom `AuthorizationPolicyProvider`) is the single most technically distinctive idea surfaced in this pass outside the three excluded categories (modules/theming/page-editing). It's a reusable pattern for any app wanting to let plugin/module authors declare their own fine-grained, role-delegable permissions without the host app having to pre-register every possible policy string at startup.
- The job-scheduler's log/stop/edit-next-run controls suggest the operational maturity bar (visibility + control per background job) is higher than a typical bare `IHostedService` timer loop — worth noting as a UX pattern (admin-visible job control surface) even though scheduled-job systems themselves are common.

### Gaps
- Search/indexing approach: a `SearchContent` table with `SiteId` was seen in the database-schema doc, confirming search indexing exists and is site-scoped, but no source examined described the actual indexing algorithm/technology (e.g., whether it's a simple SQL LIKE search, a dedicated Lucene-style index, or something else) — flagged as unverified from available sources.
- SEO/sitemap handling: the alias-redirect-to-default behavior (found under tenancy) is one concrete SEO-related mechanism, but no dedicated sitemap-generation or meta-tag/structured-data feature was found in the sources reviewed — not confirmed either way.
- Localization/i18n architecture: `CultureCode` per-Site and a `Languages` collection on Site were seen in the Site model, confirming per-site localization exists, but the deeper i18n mechanism (resource files vs. DB-stored translations, RTL support, etc.) was not investigated — not enough tool budget remained to pursue this thread further.
- Audit logging/telemetry: one WebSearch synthesis mentioned "event logging and audit trails" as a claimed feature, but this was not corroborated by an independently-fetched primary source in this pass — treat as a plausible-but-unverified claim, not a confirmed fact.

---

## Backward compatibility / core framework upgrade strategy across versions

### Takeaway
Oqtane markets a "Seamless Upgrade Experience" as a core feature dating to v1.0.0, and its major-version cadence is explicitly tied to .NET's own major-version releases (v3.0.0→.NET 6, v4.0.0→.NET 7, v5.x→.NET 8 adding SSR, v6.0.0→.NET 9) — meaning modules/themes should generally expect to need at least a recompile/retarget at each .NET major bump, and the "automated upgrades" goal was explicitly cited (alongside tenancy isolation) as one of the specific DNN pain points Oqtane was designed to fix.

### Cited Findings
- Oqtane's roadmap/version history ties major releases directly to .NET versions: v3.0.0 (.NET 6, Nov 2021), v3.2.0 (.NET MAUI support, Sep 2022), v4.0.0 (.NET 7, Jun 2023), v5.x (.NET 8, adds Static Server Rendering), v6.0.0 (.NET 9, Nov 2024). — [Oqtane Roadmap and History](https://docs.oqtane.org/guides/roadmap/index.html)
- Oqtane's own DNN-comparison post names "automated upgrades" and "tenancy isolation" together as the specific "widely acknowledged core limitations" of DNN that motivated Oqtane's from-scratch design — i.e., smooth core-framework upgrades (surviving with installed modules intact) was a named founding design goal, not an incidental feature. — [Oqtane Blog: Oqtane vs DNN](https://www.oqtane.org/blog/!/19/)
- Version-based "site migrations" (introduced in v3.1) were mentioned as enabling environment-specific deployments through CI/CD pipelines, suggesting the upgrade/versioning strategy extends to site-content/config migration tooling, not just core-binary upgrades. — [GitHub Discussion #1947](https://github.com/oqtane/oqtane.framework/discussions/1947)

### Inferences
- Tying major releases to .NET's own major version cadence is a double-edged design choice: it keeps Oqtane's core continuously modern (no lagging on an old .NET LTS), but it also means third-party module/theme authors are implicitly required to re-test/re-target roughly annually alongside each .NET major release if they want to track Oqtane's latest — a real, structural backward-compatibility tax passed on to the module ecosystem, even if Oqtane's own "seamless upgrade" tooling for the host app works well.

### Gaps
- Could not find a primary source explaining the actual mechanism of the "Seamless Upgrade Experience" (e.g., automatic EF Core migrations run on startup detection of a new core version, a dedicated upgrade UI, DLL-hot-swap behavior) — this claim is asserted on the roadmap/marketing page but the technical how was not found in the sources reviewed in this pass.
- No source directly addressed the real-world failure rate of third-party modules surviving a core upgrade (e.g., community complaints about breaking changes at .NET major bumps) — this would need a dedicated search of GitHub issues/discussions filtered by upgrade-breakage reports, which was outside this pass's tool budget.
