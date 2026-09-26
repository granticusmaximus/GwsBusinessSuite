# Oqtane Theming System

## What is an Oqtane "theme" architecturally — Blazor component library, CSS-only skin, or a combination?

### Takeaway
An Oqtane theme is a compiled Blazor component library (a .dll), not a CSS-only skin: it implements the `Oqtane.Themes.ITheme` interface and ships one or more Razor components (a theme/layout component inheriting `ThemeBase` and a container component inheriting `ContainerBase`), plus its own CSS/JS/image assets. CSS is bundled inside the theme package as a resource, not a separate mechanism.

### Cited Findings
- "Since Oqtane is based on .net and Blazor, themes are built using Blazor components and these are compiled into a DLL that is loaded by the Oqtane framework." — [Oqtane Themes Developer Guide](https://docs.oqtane.org/dev/themes/index.html)
- An Oqtane theme minimally requires: a `ThemeInfo.cs` file that implements `Oqtane.Themes.ITheme` and provides metadata (name, version, package name, resource declarations); a `Theme.razor` file — "the main thing shown to the user" — which uses `@inherits ThemeBase`; and a `Container.razor` file — "the main wrapper around a module" — which uses `@inherits ContainerBase`. — [Themes - Code Structure of a Theme](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html)
- "It is crucial that the namespace of the theme is unique, and that all these core elements (ThemeInfo, Theme, Container) are in exactly this namespace." — [Themes - Code Structure of a Theme](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html)
- The `Oqtane.Client/Themes` source directory contains interface files `ITheme.cs`, `ILayoutControl.cs`, `IContainerControl.cs`, and base classes `ThemeBase.cs`, `ContainerBase.cs`, `LayoutBase.cs`, `ThemeControlBase.cs`, confirming the interface-driven component-library architecture. — [oqtane.framework/Oqtane.Client/Themes (GitHub)](https://github.com/oqtane/oqtane.framework/tree/master/Oqtane.Client/Themes)
- The `ITheme` interface (namespace `Oqtane.Themes`, assembly `Oqtane.Client.dll`) exposes a `Theme` property returning an `Oqtane.Models.Theme` object — the metadata model the framework uses to identify/register the theme. — [Interface ITheme (Oqtane API docs)](https://docs.oqtane.org/api/Oqtane.Themes.ITheme.html)
- "Rather than requiring resources to be repeated in every Razor component within a module or theme, it is now possible to declare them in the IModule or ITheme interface implementation" — i.e., CSS/JS resources are declared once in the theme's metadata class and centrally injected, rather than being a separate static-skin layer. — [Oqtane Themes Developer Guide](https://docs.oqtane.org/dev/themes/index.html)
- Themes need "a basic understanding of HTML, CSS, and JavaScript" and "familiarity with Razor syntax and Blazor components," confirming the combination (Blazor component logic + CSS/JS assets), not a CSS-only skin system. — [Developing Themes](https://docs.oqtane.org/dev/themes/theme-development.html)

### Inferences
- Because the theme's core files (`ThemeInfo.cs`, `Theme.razor`, `Container.razor`) must share one namespace and are compiled together, a theme is fundamentally a small Blazor class library/plugin assembly, structurally identical in kind to an Oqtane module (which similarly implements `IModule`) — themes and modules appear to be siblings in the same extension architecture, just targeting layout/chrome instead of content.
- Pure CSS-only re-skinning without touching the Razor layout is possible in principle (a theme's CSS could be swapped independently), but Oqtane's packaging unit is always the compiled theme assembly plus its resources together — there's no documented "CSS-only theme" install path distinct from the full component-library theme.

### Gaps
- The full member list of `IThemeControl`, `ILayoutControl`, and `IContainerControl` (beyond confirming they exist as interface files) could not be retrieved — the API doc page fetched only exposed `ITheme.Theme`, and dedicated pages for the other interfaces were not directly fetched within the tool budget for this task.

## What ships with a theme — layout templates ("Containers"), CSS/SCSS, static assets?

### Takeaway
A theme package ships at minimum one theme/layout component and one or more Container components (Oqtane's own term for the wrapper markup around a single module instance), plus declared CSS/JS resources and any wwwroot static assets (images, fonts) scoped under a folder matching the theme's name.

### Cited Findings
- "A container file like Container.razor is the main wrapper around a module." — [Themes - Code Structure of a Theme](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html); term "Containers" is used explicitly by Oqtane (e.g., `AdminContainer.razor`, `DefaultContainer.razor` ship in the core `Themes/Controls` folder). — [oqtane.framework/Oqtane.Client/Themes (GitHub)](https://github.com/oqtane/oqtane.framework/tree/master/Oqtane.Client/Themes)
- "Themes have static resources such as images or CSS files which are located in the wwwroot folder with a subfolder name matching the theme name." — [Oqtane Themes Developer Guide / GitHub theme-folder research](https://docs.oqtane.org/dev/themes/index.html)
- `ThemeInfo.cs` declares a `List<Resource>` "supporting stylesheets and scripts with configurable locations, reload behavior, and render modes" — i.e., CSS/JS are first-class declared resources on the theme metadata object, not loose files referenced ad hoc. — [Themes - Code Structure of a Theme](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html)
- A theme can optionally declare `ThemeSettingsType` and `ContainerSettingsType` in `ThemeInfo.cs`, pointing to Razor components (e.g., `Themes/OqtaneTheme/Themes/ThemeSettings.razor` and `Themes/OqtaneTheme/Containers/ContainerSettings.razor`) that the framework injects at runtime to let admins configure theme-specific and container-specific settings. — [GitHub Discussion #3851 "Theme Settings?"](https://github.com/oqtane/oqtane.framework/discussions/3851)
- Additional supplementary Blazor components (e.g., `NavMenu.razor`) can also ship inside a theme, inheriting appropriate base classes and sharing the theme's namespace. — [Themes - Code Structure of a Theme](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html)
- The built-in `Oqtane.Client/Themes` directory ships two full built-in themes (`BlazorTheme`, `OqtaneTheme`) alongside a shared `Controls` folder containing generic container implementations (`AdminContainer.razor`, `DefaultContainer.razor`) usable by any theme. — [oqtane.framework/Oqtane.Client/Themes (GitHub)](https://github.com/oqtane/oqtane.framework/tree/master/Oqtane.Client/Themes)

### Inferences
- A single theme package is not limited to one container — the presence of both a theme-specific `Containers` folder (with `ContainerSettings.razor`) and framework-shared generic containers (`AdminContainer.razor`, `DefaultContainer.razor`) implies Oqtane's model is "one theme, N containers," each a distinct wrapper style selectable independently for different modules on the same page (see the per-module-instance question below).

### Gaps
- No official doc page enumerating "SCSS" support specifically was found — sources refer only to "CSS/stylesheets," so it's unclear whether Oqtane's build tooling compiles SCSS for theme authors or whether that's purely a theme-author's own build-time concern before packaging plain CSS.

## How are themes packaged and distributed — same mechanism as modules, or different?

### Takeaway
Themes use the same distribution mechanism as modules: compiled into a class library, packaged as a NuGet package, and installed through the same admin "Module Management"/"Theme Management" interface — either from an in-product Marketplace (Open Source or Commercial) or by manually uploading the NuGet package file.

### Cited Findings
- "Themes are installed through the admin dashboard via two approaches: Marketplace ('Open Source' or 'Commercial' themes directly), or Upload — install custom themes as NuGet packages by selecting files and uploading them." — [Theme Management (Oqtane docs)](https://docs.oqtane.org/manuals/system/theme-management.html)
- `ThemeInfo.cs` includes a `PackageName` field explicitly "for installation differentiation," i.e., the metadata class itself carries the package identity used by the installer. — [Themes - Code Structure of a Theme](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html)
- When creating modules or themes from within Oqtane's own theme/module creator tooling, "you need to compile them and then restart the framework for it to be aware of them" — the same restart-based load path is described identically for both modules and themes. — [search synthesis of GitHub Issue/Discussion threads on module & theme installation](https://docs.oqtane.org/dev/themes/theme-installation.html) and [Installing Modules](https://docs.oqtane.org/guides/modules/module-installation.html)
- Dedicated theme repositories exist and are distributed independently of the core framework repo, e.g. `oqtane/oqtane.theme.bootswatch` (a collection of themes based on Bootswatch) and `oqtane/Oqtane.Theme.Corporate` ("Corporate Theme for Oqtane 6.1+"), confirming themes are shipped/distributed as separate installable packages, not only baked into the core repo. — [GitHub: oqtane/oqtane.theme.bootswatch](https://github.com/oqtane/oqtane.theme.bootswatch); [GitHub: oqtane/Oqtane.Theme.Corporate](https://github.com/oqtane/Oqtane.Theme.Corporate)

### Inferences
- Because `PackageName` is a metadata field on the same `ITheme`/module info pattern, and both modules and themes are installed via NuGet upload or Marketplace pick, Oqtane appears to treat "module" and "theme" as two flavors of the same generic "Extension/Package" installation pipeline rather than maintaining a theme-specific registry format.

### Gaps
- Exact internal package manifest format (e.g., whether it's a literal `.nupkg` with a `manifest.json`, or an Oqtane-specific wrapper) was not confirmed from primary source in the time available — docs consistently say "NuGet package" but the precise manifest schema was not directly inspected in source.

## Is theme switching a runtime-swappable operation, or does it require rebuild/redeploy? (Precise distinction)

### Takeaway
Two distinct operations exist and must not be conflated: (1) **switching between already-installed themes** for a site or a specific page is a pure runtime admin-UI operation (a dropdown selection stored as data — no compile, no restart); (2) **installing a brand-new theme package** (whether custom-uploaded or created via Oqtane's in-product theme creator) requires the application/AppDomain to restart so the new assembly can be loaded — this is not a full source redeploy, but it is more than a live, zero-interruption swap.

### Cited Findings
- Runtime, no-restart theme *selection*: "In Oqtane's Page Management, you can customize the theme and container for individual pages... Theme: Select the theme for this page. Default Container: Select the default container for the page," accessed by editing a page's "Appearance Configuration" section — a plain admin-UI dropdown action. — [search synthesis of Page Management / Site Settings docs](https://docs.oqtane.org/manuals/site/page-management.html)
- Site-wide runtime selection: Site Settings expose "Default Theme," "Default Container," and "Default Admin Container" as selectable site-level settings. — [Site Settings (Oqtane docs)](https://docs.oqtane.org/manuals/site/site-settings.html)
- Restart requirement for *installing new* theme/module packages: "When modules or themes are installed, the server application must be restarted so that it can complete the installation and load them into the appdomain. This is a core requirement in Oqtane's current architecture." — [search synthesis citing Theme Installation / Module Deployment docs](https://docs.oqtane.org/dev/themes/theme-installation.html)
- On the in-product theme-creation workflow specifically: "you will see a notification confirming the creation and instructing you to compile the new theme project and restart the application." — [Theme Management (Oqtane docs)](https://docs.oqtane.org/manuals/system/theme-management.html)
- On why restart (not dynamic hot-load) is used: "There was an idea to load packages dynamically including services and controllers without need of system restart. However, restart application is safe, working and clean method how to update application domain," per community/GitHub discussion threads on the .NET 8 / Oqtane 5.0 upgrade. — [search synthesis of GitHub Discussion #3390 and related threads](https://github.com/oqtane/oqtane.framework/discussions/3390)
- Theme *creation* (as opposed to installing a pre-built package) is explicitly discouraged in production: docs warn theme creation should occur only in "development environments," not production, citing "performance issues, security concerns, or disruptions to the live user experience." — [Theme Management (Oqtane docs)](https://docs.oqtane.org/manuals/system/theme-management.html)

### Inferences
- The precise line: swapping *which already-installed theme a site/page uses* is genuinely runtime and immediate (a metadata/database change picked up on next render, no compile step, no server restart) — this satisfies "pick a different theme per site/page from an admin UI without redeploy." Getting a *new, not-yet-installed* theme onto the server is not a redeploy of the whole app/source tree, but it is a restart of the running server process (AppDomain reload) to load the new assembly — a middle ground between "fully runtime hot-swap" and "full redeploy."

### Gaps
- Whether the application restart on module/theme install causes any visible downtime or is a graceful in-place AppDomain reload (zero-downtime) was not confirmed from a primary source — community discussion language ("restart the application") does not specify request-draining or blue/green behavior.

## Can themes be scoped per-page or per-section, or only site-wide? Multiple containers per theme, selectable per-module-instance?

### Takeaway
Oqtane supports three levels of scoping simultaneously: site-wide default theme/container, per-page theme/container override, and per-module-instance container selection — a single theme can ship multiple containers, and a page admin can pick a different container for each individual module placed on a page.

### Cited Findings
- Site level: "Default Theme," "Default Container," "Default Admin Container" are configured in Site Settings. — [Site Settings (Oqtane docs)](https://docs.oqtane.org/manuals/site/site-settings.html)
- Page level: Page Management's "Appearance Configuration" lets an admin select "Theme" and "Default Container" for that specific page, overriding the site default. — [Page Management (Oqtane docs)](https://docs.oqtane.org/manuals/site/page-management.html)
- Container settings can be scoped even more granularly: "The Setting Scope option in the Oqtane Theme contains 'Site' or 'Page' as options," meaning a theme's own custom settings component can declare whether its configurable options apply at the site or page level. — [GitHub Discussion #3851 "Theme Settings?"](https://github.com/oqtane/oqtane.framework/discussions/3851)
- Multiple containers per theme and per-module selection: `ContainerSettingsType` in `ThemeInfo.cs` and the existence of a distinct `Containers` subfolder inside a theme package (e.g., `Themes/OqtaneTheme/Containers/ContainerSettings.razor`) plus framework-shared generic containers (`AdminContainer.razor`, `DefaultContainer.razor`) confirm more than one container ships and is selectable independently of the theme layout itself. — [GitHub Discussion #3851](https://github.com/oqtane/oqtane.framework/discussions/3851); [oqtane.framework/Oqtane.Client/Themes (GitHub)](https://github.com/oqtane/oqtane.framework/tree/master/Oqtane.Client/Themes)

### Inferences
- Combining the Site Settings "Default Container" field with Page Management's "Default Container" field (a page-level override) and the general Oqtane content model (pages contain multiple module instances, each individually configurable) strongly implies each module instance's container can itself be overridden independent of the page default — this is a standard, long-documented Oqtane content-pane behavior (choose a container when adding/editing a module on a page), consistent with all sourced evidence, though this exact "per-module-instance container picker" UI step was not directly quoted from a fetched primary source in this session.

### Gaps
- A direct primary-source screenshot/quote of the per-module (not just per-page) container-selection UI control was not retrieved within the tool-call budget; the per-module-instance claim rests on inference from the Site/Page default-container pattern plus general architecture description rather than a directly cited UI walkthrough.

## Are there built-in themes that ship with Oqtane, and how many/which ones?

### Takeaway
The Oqtane core framework repository ships with two built-in themes out of the box — `BlazorTheme` and `OqtaneTheme` — plus separate first-party theme packages distributed outside the core repo (e.g., a Bootswatch-based theme collection and a "Corporate" theme).

### Cited Findings
- The `Oqtane.Client/Themes` directory in the core framework repo contains exactly two theme implementation folders: `BlazorTheme` and `OqtaneTheme`, alongside the shared `Controls` folder (generic containers) and the interface/base-class files. — [oqtane.framework/Oqtane.Client/Themes (GitHub)](https://github.com/oqtane/oqtane.framework/tree/master/Oqtane.Client/Themes)
- Additional official/first-party theme packages exist as separate repos: `oqtane/oqtane.theme.bootswatch` ("A collection of themes based on Bootswatch") and `oqtane/Oqtane.Theme.Corporate` ("Corporate Theme for Oqtane 6.1+"). — [GitHub: oqtane/oqtane.theme.bootswatch](https://github.com/oqtane/oqtane.theme.bootswatch); [GitHub: oqtane/Oqtane.Theme.Corporate](https://github.com/oqtane/Oqtane.Theme.Corporate)
- A community-contributed sample/starter is also referenced (the "Arsha theme," mentioned alongside "the core OqtaneTheme" as reference implementations for container development). — [search synthesis referencing Oqtane theme container documentation](https://docs.oqtane.org/dev/themes/oqtane-theme-code-explained.html)

### Inferences
- The Bootswatch theme collection package likely provides several visual variants (Bootswatch itself has ~20+ Bootstrap-based skins), but the exact count bundled inside `oqtane.theme.bootswatch` was not verified by inspecting that repo directly in this session.

### Gaps
- The precise number of individual theme variants inside the `oqtane.theme.bootswatch` package was not confirmed (repo not opened directly). The current default theme name for a fresh Oqtane install (`BlazorTheme` vs `OqtaneTheme`) was not explicitly confirmed from a fetched primary source in this session.

## How does Oqtane's theming relate to its multi-tenancy — can different tenants/sites run different themes simultaneously?

### Takeaway
Yes — theme assignment is a per-site setting (and further overridable per-page), and Oqtane's multi-tenancy model runs multiple independent Sites from one shared application instance/database, so different tenants/sites can simultaneously use entirely different themes without any redeploy or conflict.

### Cited Findings
- "You can have a single installation of Oqtane that contains multiple sites, where each site can have its own domain, subdomain, or subfolder," and "each site can have its own unique visual theme." — [search synthesis of Oqtane multi-tenancy discussion/docs](https://www.jocheojeda.com/2025/10/08/understanding-multi-tenancy-in-oqtane-and-how-to-set-up-sites/)
- Site Settings (which include "Default Theme," "Default Container," "Default Admin Container") are configured per Site entity, not globally for the whole Oqtane installation. — [Site Settings (Oqtane docs)](https://docs.oqtane.org/manuals/site/site-settings.html)
- Oqtane's advertised core capabilities explicitly include "multi-tenancy" and "a fully dynamic page compositing model" together, per Oqtane's own project description. — [GitHub: oqtane/oqtane.framework README summary via search](https://github.com/oqtane/oqtane.framework)

### Inferences
- Since theme/container assignment lives on the Site (and Page) data records rather than in compiled configuration, and since all installed theme assemblies are loaded once into the shared AppDomain/process serving all tenants, any installed theme is available to be assigned to any site independently — meaning simultaneous multi-theme operation across tenants is a natural consequence of the data-driven assignment model, not a special-cased feature.

### Gaps
- No primary Oqtane doc page was found that explicitly states "different tenants can run different themes" in those exact words as a callout/feature bullet; this conclusion is inferred by combining the (a) per-site Default Theme setting and (b) general multi-tenancy architecture description, both independently well-sourced above, rather than from one single explicit statement.
