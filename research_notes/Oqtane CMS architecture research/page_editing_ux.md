# Oqtane Page/Content Editing UX

## Does Oqtane have a visual, drag-and-drop page layout builder, or is it form/admin-panel-driven?

### Takeaway
Oqtane's page-building experience is an "in-context" admin overlay on the live page (not a separate builder canvas), but module placement and reordering are done via dropdown menus and named actions ("Move to Top," "Move to >"), not direct-manipulation drag-and-drop. It is closer to old-school portal/portlet systems (DotNetNuke, which Oqtane's creator also built) than to Gutenberg/Elementor-style block editors.

### Cited Findings
- The Content Editor is activated via a pencil icon; when enabled it "displays the borders of the content panes where the modules are placed and a small downward-pointing arrow next to each module," which opens a menu for module interaction — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- Module repositioning is done through a "Move Modules" menu with discrete actions: "Move Up," "Move Down," "Move to Top," "Move to Bottom," and "Move to >" (send to a different pane) — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- The documentation for both the Content Editor and the "Adding Modules" guide describes no drag-and-drop interaction at any point; module type, title, pane, and container are all chosen via dropdowns/form fields in a modal — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html); [Adding Modules guide](https://docs.oqtane.org/guides/modules/adding-modules.html)
- Adding a module requires opening the Control Panel (gear icon), choosing a module type from a categorized list (Admin Module / Common Module / Developer Module), then setting Title, Pane, and Container in a settings form — [Adding Modules guide](https://docs.oqtane.org/guides/modules/adding-modules.html)
- Oqtane's own marketing site describes the model as: "manage your content 'in context' without having to navigate to a separate administrative application" and "the capability for an administrator to dynamically construct a page from existing components without writing any code" — [oqtane.org](https://www.oqtane.org/)
- The GitHub README describes a "fully dynamic page compositing model" with a Control Panel for adding/editing/deleting pages and modules, plus context menus for module management, illustrated with screenshots (Installation Wizard, Admin Dashboard, Control Panel, context menus, responsive mobile view) — [oqtane/oqtane.framework README](https://github.com/oqtane/oqtane.framework)

### Inferences
- "Dynamically construct a page from existing components without writing any code" is Oqtane's own framing of its no-code capability, but the mechanism behind that claim (per the Content Editor and Adding Modules docs) is menu/dropdown-driven placement into predefined panes — not free-form direct manipulation. This is a materially different (and less sophisticated) interaction model than a block/section editor with live drag handles, insertion points, or drop-zone previews.
- The pencil-icon "Content Editor" overlay is conceptually similar to WordPress's classic widget/sidebar management screens (pre-Gutenberg) rather than to Gutenberg's block canvas.

### Gaps
- No first-party video or animated GIF of the Content Editor in actual use was found/fetched in this pass, so exact click-by-click behavior (e.g., whether the module menu is a right-click context menu, a hover-revealed caret, or a persistent toolbar) is described only via the text of the docs, not confirmed visually.
- Could not verify whether recent Oqtane releases (post-3.x) have added any drag-and-drop capability; docs fetched reflect current published documentation as of this research but a changelog/release-notes check specifically for "drag and drop" was not performed.

## How is a page structured internally (panes/zones), and how flexible is the layout system?

### Takeaway
Pages are composed of a fixed, theme-defined set of named "panes" (layout regions) chosen when the page's theme/layout is set; the framework itself is built on Bootstrap 5, so panes are effectively Bootstrap grid columns/rows baked into each theme's Razor layout, not an admin-configurable arbitrary grid.

### Cited Findings
- "Panes can span the full width of the page or be positioned in a column fashion... think of panes as windows in the design of the site where you can drop in modules" — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- "The available panes vary by theme—the default Oqtane theme includes over 20 options ranging from full-width layouts to asymmetrical ratios like 'Left 66% Pane and Right 33% Pane'" — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- Page settings include a "Layout: Number of content panes (columns) available on the page," set at the page level along with theme selection — [Page Management (Control Panel) docs](https://docs.oqtane.org/manuals/content/page-management.html)
- Oqtane themes are structured with "layouts that define the overall structure and design, views that render content and components, stylesheets that define visual appearance, and JavaScript that adds interactivity" — [Oqtane Themes Developer Guide search result](https://docs.oqtane.org/dev/themes/theme-development.html)
- "Oqtane is based on Bootstrap so you can use your own custom Bootstrap css for building responsive grid layouts, as Bootstrap provides a comprehensive grid system for responsive design" — [Theme discussion result citing Oqtane Themes Guide](https://docs.oqtane.org/guides/themes/index.html)
- A third-party theme example (Magic Themes "Oqtane Basic") ships with only 3 layouts (Default, Centered, Fullscreen) optimized for 2 named panes (Default and Header), illustrating how narrow a given theme's pane set typically is in practice — [cre8magic Oqtane Basic theme page](https://cre8magic.blazor-cms.org/magic-themes/oqtane-basic/index.html)
- Module settings include "Container type" (controls the wrapper/title styling around a module within a pane) and "Effective/expiry dates" (time-based show/hide), managed via the Manage Settings modal — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)

### Inferences
- Panes are a fixed vocabulary per theme (e.g., "ContentPane," "LeftPane," "RightPane" style names), defined by the theme developer in Razor/CSS, not something an admin can invent, nest arbitrarily, or resize at runtime. Flexibility comes from switching between a theme's pre-built layout variants (e.g., "20+ options" in the default theme) rather than free-form grid composition.
- Because it rides on Bootstrap 5, responsive breakpoint behavior exists in principle (in theme CSS), but it is inherited from Bootstrap's standard breakpoints as authored by the theme, not exposed as a per-page/per-module responsive control to the content admin.
- This is a two-level flexibility model: (1) admin picks a layout/theme with a given pane set for the page, (2) admin assigns modules to those named panes and orders them within a pane — there is no arbitrary nested container/section model (no nesting sections within sections, no per-breakpoint overrides) exposed to the content editor persona.

### Gaps
- Could not directly confirm from primary source how panes are literally declared in a theme's Razor file (e.g., `<Pane Name="ContentPane">`) — the theme-configuration.html fetch returned only generic/vague marketing-style language ("color schemes," "widget placement") that did not clearly match expected technical documentation content, so it is flagged as low-confidence and excluded from Cited Findings above.
- Did not verify whether "Layout" (pane count) can be changed after a page already has modules assigned, and what happens to existing module-to-pane assignments if the layout changes.

## Does Oqtane have a draft vs. published/live version of a page? How does it compare to a typical draft/publish workflow?

### Takeaway
Oqtane has no page-level draft/versioning system in the sense of staging edits before going live — its "Publish" feature is a binary visibility toggle (hidden/admin-only vs. public), not a draft-state content model. Version history exists only at the individual HTML/Text module level (content versions with view/restore/delete), not for the page as a whole or for other module types.

### Cited Findings
- "To toggle the publish status, click the Publish button, which will switch between publishing and unpublishing the page" — [search result summarizing Page Management docs](https://docs.oqtane.org/manuals/content/page-management.html)
- Publish "enables you to make a page public if it was previously marked as hidden, which is particularly useful for working on a page that you want to keep inaccessible to regular users until it is fully prepared for release" — [Page Management (Control Panel) docs](https://docs.oqtane.org/manuals/content/page-management.html)
- Direct fetch of the Page Management (Control Panel) doc concluded: "The documentation contains no reference to draft versions, content versioning, or revision history systems" for pages — [Page Management (Control Panel) docs](https://docs.oqtane.org/manuals/content/page-management.html)
- The site/admin-dashboard-level Page Management doc likewise: "does not mention draft pages, version control, or staging workflows. The system focuses on active pages with effective and expiry dates for time-based activation rather than draft/versioning concepts" — [Page Management (site) docs](https://docs.oqtane.org/manuals/site/page-management.html)
- By contrast, the HTML/Text module has its own "Versions tab" that "maintains a historical record of past edits, displaying metadata including the date each version was created and the user responsible," letting a user "View," "Restore," or "Delete" versions — [HTML/Text Editor docs](https://docs.oqtane.org/manuals/content/html-text-editor.html)
- Modules also support scheduled visibility via "Effective/expiry dates" set in the Manage Settings modal, i.e., time-windowed publication rather than a draft/review workflow — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)

### Inferences
- Oqtane conflates "publish" with "visibility to non-admin roles" — a hidden/unpublished page is still fully live in the database and editable in place by admins; there is no separate draft copy that diverges from a published copy, and no "review/approve then push live" pipeline at the page level.
- Content versioning is fragmented and module-specific: only the built-in HTML/Text module has undo/restore history. Other module types (and the page/module structure itself — pane assignment, order, settings) have no version history or rollback at all.
- This is meaningfully weaker than a typical modern CMS draft/publish workflow (e.g., WordPress's draft → preview → publish with full revision history, or a headless CMS's draft/published content states) — Oqtane has no equivalent of "preview unpublished changes as they will appear" separate from just toggling admin-only visibility, and no page-level diffing/rollback.

### Gaps
- Could not verify whether third-party/marketplace modules for Oqtane add page-level versioning or workflow (e.g., an approval/staging module) — this research only covered the core framework and its bundled HTML/Text module.
- Did not find documentation on whether the "Import Content" / "Export Content" actions mentioned in the Content Editor's module caret menu could be used manually as a poor-man's backup/versioning mechanism; this is speculative and not confirmed.

## Does Oqtane have a WYSIWYG rich-text/HTML editing module — what editor library does it use?

### Takeaway
Yes — the built-in "HTML/Text" module provides a WYSIWYG rich text editor built on QuillJS (v1.3.7), plus a separate raw HTML source tab and per-content version history; it is explicitly framed by Oqtane's own docs as a demo/learning vehicle for the platform's editing features rather than a flagship page-building tool.

### Cited Findings
- "The Rich Text Editor is a WYSIWYG editor powered by QuillJS (v1.3.7), providing an easy way to format content visually" — [HTML/Text Editor docs](https://docs.oqtane.org/manuals/content/html-text-editor.html)
- The module offers three sub-tabs: "Rich Text Editor – A WYSIWYG editor powered by QuillJS," "Raw HTML Editor – Enables direct HTML editing for more control over the source code," and "Settings – Provides configuration options for the editor's features" — [HTML/Text Editor docs](https://docs.oqtane.org/manuals/content/html-text-editor.html)
- Settings include toggling the rich text vs. raw HTML editors on/off, enabling image insertion, choosing the Quill "snow" theme, setting a debug level, and defining toolbar contents — [HTML/Text Editor docs](https://docs.oqtane.org/manuals/content/html-text-editor.html)
- Image insertion opens a dialog to select a folder, pick an existing file, or upload a new image via "Choose File and Upload" buttons — [HTML/Text Editor docs](https://docs.oqtane.org/manuals/content/html-text-editor.html)
- The module description frames it as ideal "for exploring Oqtane's editing and content management features, including moving modules between panes, configuring settings, and managing roles" — [HTML/Text Editor docs](https://docs.oqtane.org/manuals/content/html-text-editor.html) (also indexed as [Modules Overview](https://docs.oqtane.org/guides/modules/index.html))
- A GitHub issue exists titled "Editor error thrown: Quill is not defined," corroborating that Quill is the actual runtime dependency (as opposed to just being named in docs) and that its loading has been a real source of bugs — [oqtane/oqtane.framework issue #518](https://github.com/oqtane/oqtane.framework/issues/518)

### Inferences
- Quill v1.3.7 is a fairly old pinned version (Quill 1.x; Quill 2.0 was released years after 1.3.7) — Oqtane is not using a modern block-based or ProseMirror/CKEditor-class rich text engine, and is not Blazor-native (it's a JS interop wrapper around a JS library).
- The "Raw HTML Editor" sub-tab existing as a peer, switchable tab (not just a "view source" modal) suggests the WYSIWYG output is treated as plain HTML string content in a single database field, consistent with a classic "content = one HTML blob per module instance" model rather than a structured block/JSON content model.

### Gaps
- Could not confirm whether Quill has since been upgraded past 1.3.7 in the latest Oqtane release (3.x) — the version number came from indexed documentation and may lag the actual shipped version; not independently verified against release notes.
- Did not find information on accessibility or mobile editing behavior of this Quill integration.

## Is there any concept of reusable content blocks, templates, or design tokens in the page editor?

### Takeaway
Oqtane has page-level and site-level *templates* (used at site/page creation time to scaffold structure and starter content) and theme-level reusable UI components, but nothing resembling a content-author-facing "reusable block/pattern library" or a design-token system exposed in the editing UI.

### Cited Findings
- "Oqtane includes two Site Templates, DefaultSiteTemplate and EmptySiteTemplate. These templates define a PageTemplate, add a HtmlText module, and set the content of the HtmlText module" — [search result citing Oqtane framework structure](https://www.oqtane.org/blog/!/92/scalability-testing-and-site-templates)
- "Oqtane apps are composed of reusable web UI components implemented using C#, HTML, and CSS" — [search result, general framework description]
- Oqtane supports dynamic placeholder/token replacement in module content (e.g., `{UserName}`), configurable via a "Dynamic Tokens" tab in the module's Manage Settings modal — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html) (tokens themselves referenced via [2sxc Oqtane token docs](https://docs.2sxc.org/basics/server/render/tokens/index.html), a third-party extension, not core Oqtane)
- A third-party "OqtaneThemeTemplateConverter" tool exists to "transform an existing Oqtane theme source code folder into a reusable theme template with placeholder tokens that can be used to scaffold new themes via Oqtane's theme generator" — [tvatavuk/OqtaneThemeTemplateConverter](https://github.com/tvatavuk/OqtaneThemeTemplateConverter)
- No mention of design tokens (colour/spacing/typography variable systems) was found in any official Oqtane documentation page fetched in this research; the general definition of "design token" retrieved was from Wikipedia, not Oqtane material — [Design system, Wikipedia](https://en.wikipedia.org/wiki/Design_system)

### Inferences
- "Templates" in Oqtane are a developer/site-provisioning concept (scaffolding a new site or page with predefined modules/content at creation time), not an editor-facing "insert a saved pattern/block" feature like Gutenberg's reusable blocks or synced patterns.
- There is no evidence of a design-token or theme-variable system surfaced to content editors; visual consistency is achieved only through whichever theme/CSS the developer built (Bootstrap 5 variables at most, at the CSS layer, not exposed as an editor UI).

### Gaps
- Could not find primary Oqtane documentation specifically for "Page Templates" (distinct from "Site Templates") to confirm exactly what an admin can do with them at page-creation time (e.g., can an admin author and save their own reusable page template from the UI, or are templates strictly developer/code-defined?). This is a notable gap given the report's need to compare against reusable-block editors.
- No verification of any marketplace module that might add a Gutenberg-style block/pattern library on top of Oqtane.

## How does an admin reorder/reposition modules on a page?

### Takeaway
Reordering is entirely command/menu-driven — a caret/dropdown menu per module offers discrete "Move Up / Move Down / Move to Top / Move to Bottom / Move to [pane]" actions; there are no drag handles, and no documentation evidence of an explicit numeric "order" input field either (it appears to be action-based, not field-based).

### Cited Findings
- "You can move modules around the panes by using the content editor... Move Modules actions to reposition modules dynamically within a page, moving a module up or down in its current pane, placing it at the top or bottom, or using Move to to send it to a different pane" — [search result summarizing Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- Direct fetch confirms the action set precisely: "Move to Top" and "Move to Bottom" for vertical positioning; "Move Up" and "Move Down" for incremental repositioning; "Move to >" for selecting a specific destination pane — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- These actions are reached via "a small downward-pointing arrow next to each module, enabling you to open a menu for module interaction" — [Content Editor docs](https://docs.oqtane.org/manuals/content/content-editor.html)
- The Modules tab in the Page Management control panel is separately described as letting an admin "modify or reorder existing modules, providing more granular control over the content displayed on your page," corroborating that reordering is also reachable from the page settings modal, not only the in-context caret menu — [Page Management (Control Panel) docs](https://docs.oqtane.org/manuals/content/page-management.html)

### Inferences
- This is a strictly weaker interaction model than drag-and-drop reordering: each move is a discrete, single-step command (often requiring multiple clicks to move a module several positions), with no live visual preview of the drop position during the drag — because there is no drag.

### Gaps
- Could not confirm whether "Move Up/Down" operates purely on the caret-menu's async command dispatch (i.e., a full page/component re-render per move) or has any optimistic/animated transition; no video evidence was reviewed.

## Are there screenshots, demo videos, or a live demo site showing the actual page-editing UI in action?

### Takeaway
No persistent public sandbox/demo instance of the Oqtane admin UI was found; oqtane.org instead links to independent showcase sites (which are live production sites, not editable demos) and the GitHub README includes static screenshots (Installation Wizard, Admin Dashboard, Control Panel, module context menus, responsive mobile view) that were not directly viewable/described pixel-by-pixel in this text-based research pass.

### Cited Findings
- oqtane.org lists three sites "built with Oqtane" as showcases: .NET Foundation Trends (dnfprojects.org), "Built On Blazor!" (builtonblazor.net), and HockeyTrends.com — [oqtane.org](https://www.oqtane.org/)
- oqtane.org includes images labeled "tabs-2.jpg" (described as illustrating "the end-user administrative interface") and "tabs-4.jpg" (showing "Blazor functionality"), but exact visual content could not be rendered/verified in this text-mode research — [oqtane.org](https://www.oqtane.org/)
- The GitHub README for oqtane/oqtane.framework includes screenshots for: Installation Wizard, Admin Dashboard, Control Panel ("enabling users to add, edit, and delete pages while adding modules"), context menus ("for managing specific modules on pages"), and a responsive mobile view — [oqtane/oqtane.framework README](https://github.com/oqtane/oqtane.framework)
- Instead of a persistent demo, the README offers "try it yourself" deployment paths: an Azure deployment button and a MonsterASP.NET free-hosting option — [oqtane/oqtane.framework README](https://github.com/oqtane/oqtane.framework)

### Inferences
- The absence of a standing public admin-demo sandbox (unlike, say, WordPress.com's editor demos or many SaaS page-builder marketing sites) is itself informative: Oqtane's go-to-market for its editing UX leans on "spin up your own instance" rather than "watch/try our polished builder," consistent with a developer-framework positioning rather than a marketing-led no-code page-builder positioning.

### Gaps
- This research was text/HTML-fetch based; the actual pixel content of the referenced screenshots (tabs-2.jpg, tabs-4.jpg, and the GitHub README images) was not visually inspected, so claims about what they show are limited to the alt-text/surrounding-caption descriptions surfaced by the fetch tool, not direct visual confirmation. A follow-up with actual image viewing would be needed to describe visual polish, information density, or UI affordances (e.g., cursor/hover states suggesting drag vs. click) with confidence.
- Did not attempt to log into any of the three showcase sites' admin areas (would require credentials and is out of scope/likely not permitted); cannot describe their live editing UI first-hand.
- No YouTube walkthrough or blog post with screenshots was directly fetched in this pass (searches surfaced doc pages and third-party theme sites instead); a dedicated video-focused search was not performed due to the tool-call budget for this assignment.
