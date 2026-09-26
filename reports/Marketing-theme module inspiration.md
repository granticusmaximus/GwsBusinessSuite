# Marketing Theme & Admin Dashboard Module Inspiration

Research pass over 9 ThemeForest marketing/business themes + 1 CodeCanyon
ASP.NET Core admin dashboard template, extracted via WebFetch against the
live marketplace pages. Goal: identify concrete, reusable page-section
"modules" a CMS page builder should offer, based on patterns that recur
across real commercial themes — not marketing copy or visual design opinions.

---

## 1. Brooklyn — Creative Multipurpose WordPress Theme

**URL:** https://themeforest.net/item/brooklyn-responsive-multipurpose-wordpress-theme/6221179
**Category:** Creative / Multipurpose Portfolio

- Hero section (full-screen background, video capable)
- Portfolio grid with dynamic filtering
- Slider/carousel (interactive, full-screen presentations)
- Blog layouts (classic + grid)
- CTA sections
- Countdown timer
- Configurable buttons
- 60+ modular block elements (general-purpose composition primitives)
- Team section
- Testimonial section
- Feature grid
- Stats/numbers counter

*Note: some items (team, testimonials, stats) are reasonably inferred from
the theme's stated agency/portfolio use case rather than explicitly
enumerated in fetched text; the fetch could not access screenshots.*

---

## 2. Rayo — Digital Agency & Personal Portfolio WordPress Theme

**URL:** https://themeforest.net/item/rayo-digital-agency-personal-portfolio-wordpress-theme/61465815
**Category:** Creative / Portfolio (Agency)

- Hero section
- Portfolio / single-project showcase layouts
- Services grid
- Case studies (dedicated layout)
- Team section
- Testimonials/reviews
- Feature grid (custom widgets)
- Header builder
- Footer builder
- Blog / news section (+ single post layout)
- Contact form (Contact Form 7 integration)
- CTA banners
- Accordion sections
- Tabs
- Image slider (Swiper)
- Google Map integration
- Stats counter

Built on Elementor; 9+ predefined homepage layouts.

---

## 3. Engitech — IT Solutions & Services WordPress Theme

**URL:** https://themeforest.net/item/engitech-it-solutions-services-wordpress-theme/25892002
**Category:** IT Services / Software Technology

- Hero/banner sections (multiple homepage layouts)
- Header variations (16 layouts, header builder)
- Footer layouts (15 layouts, footer builder)
- Mega menu
- Slider/carousel (Revolution Slider)
- Feature blocks
- Portfolio/project grid (incl. single project layout)
- Team section
- Testimonial content
- CTA banners
- Blog/news section (grid + single post)
- Accordion / tabs
- Google Map integration
- Contact form section
- Statistics/counter blocks
- Integration/partner logos section

Elementor-based; 26 predefined homepage layouts.

---

## 4. NextSaaS (fetched as "Nexsas") — SaaS & AI Startup WordPress Theme

**URL:** https://themeforest.net/item/nextsaas-saas-software-startup-wordpress-theme/61429174
**Category:** Technology / SaaS & AI Startup

- Hero section
- Feature grid
- Pricing table
- Testimonial slider
- Team section
- Integration logos
- Stats counter
- CTA banner
- Blog teaser
- Case study block
- Portfolio grid
- Tab widget
- Rotating card widget
- Compatibility tab widget (feature comparison matrix)
- Pricing tab widget (tabbed pricing variants, e.g. monthly/yearly)
- Process widget (step-by-step visualization)
- Customer logo widget
- Glowing card extension (decorative card variant)

60+ custom Elementor widgets; 100+ prebuilt demo sites.

---

## 5. Stratus — App, SaaS & Software Startup Tech Theme

**URL:** https://themeforest.net/item/stratus-app-saas-product-showcase/13674236
**Category:** Technology / Software / SaaS-App Showcase

- Hero section
- Feature grid
- Pricing table
- Testimonial section
- Demo/product-preview showcase
- Lead-capture form integration
- CTA banner
- Header & footer builder
- Slider components (Master Slider Pro, Revolution Slider)

*Note: fetch returned a thinner result than most — explicitly stated the
page content didn't spell out module names beyond these categories (48+
widgets, 35+ modular demos referenced but not itemized).*

---

## 6. Inotek — IT Solutions and Business Technology WordPress Theme

**URL:** https://themeforest.net/item/inotek-it-solution-wordpress-theme/61829280
**Category:** Technology / IT Solutions

- Hero section
- Feature grid
- Services section
- Team section (+ team detail page)
- Pricing table
- Portfolio/project grid (+ project detail page)
- Testimonial section
- Blog grid/list (+ detail page)
- CTA banner
- FAQ accordion
- Gallery/image grid
- Shop/product grid (WooCommerce)
- Contact form section
- Statistics counter
- Integration logos
- Testimonial slider

22+ homepage layouts, 38+ screen designs, Elementor-based.

---

## 7. Codera — IT Solutions & Services WordPress Theme

**URL:** https://themeforest.net/item/codera-it-solutions-services-wordpress-theme/63606114
**Category:** Technology / Software (IT Services)

- Hero section
- Feature grid
- Pricing table
- Testimonial slider
- Team section
- Case study / portfolio grid
- Service cards (distinct card-style layout)
- Statistics counter
- CTA banner
- Accordion (FAQ)
- Contact form integration
- Google Maps
- Parallax & video background sections
- Coming-soon page template
- Header variations (3+ options)
- Footer options (3+ options)
- Blog/magazine teaser
- Integration/partner logos

60+ Elementor custom widgets.

---

## 8. SaaSapp — App & SaaS Landing WordPress Theme

**URL:** https://themeforest.net/item/saasapp-app-saas-landing-wordpress-theme/61438081
**Category:** Technology / SaaS-App Landing Page

- Home page variations (3 distinct designs)
- Features section
- Pricing plans
- Integrations showcase
- Testimonials
- CTA areas
- Inner pages (unspecified set of standard supporting pages)

*Note: this fetch returned a noticeably thinner result than the others —
the source page text didn't break out hero/feature-grid/team/stats detail
the way most of the other listings did. Treat this one as the least
information-dense source; the categories above are the only ones the model
could confirm directly rather than infer.*

---

## 9. Stackly — Multipurpose WordPress Theme (SaaS, CRM & Fintech)

**URL:** https://themeforest.net/item/stackly-multipurpose-wordpress-theme/62377286
**Category:** Technology / Multipurpose (SaaS / CRM / Fintech)

- Hero section
- Feature grid
- Pricing table
- Testimonial section
- CTA banner
- Integration logos
- Contact form (Contact Form 7)
- Header variants (multiple layouts)
- Footer variants (multiple layouts)
- Statistics/counter blocks
- Inner pages (22+ / 20+ pre-designed supporting pages)

Elementor-based.

---

## 10. Dhonu — ASP.NET Core / MVC Admin Dashboard Template

**URLs tried:**
- https://preview.codecanyon.net/item/dhonu-aspnet-core-10-core-mvc-admin-dashboard-template/full_screen_preview/63570913
  → **FAILED**: this is a live, JS-rendered app preview; WebFetch only
  retrieved navigation/purchase-link boilerplate, no substantive content.
- https://codecanyon.net/item/dhonu-aspnet-core-10-core-mvc-admin-dashboard-template/63570913
  → **SUCCEEDED**: the marketplace item page had a real feature list.

**Category:** ASP.NET Core / MVC Admin Dashboard Template

**Pages/widgets/components:**
- Main dashboard with analytics overview
- Email interface (incl. an Outlook-style email view — listed separately
  from the general email interface)
- Team board (kanban-style project/task management view)
- Chat interface
- Calendar application
- Invoice management system
- Issue tracker
- App management interface
- Timeline view
- Pricing page
- Empty/blank template page (starter shell)
- Authentication pages: Sign In, Sign Up, Reset Password, New Password
- Security pages: Two-Factor, Lock Screen
- Error pages: 404 Not Found
- UI kit: data tables, modals, notifications, tooltips, tabs, badges, cards

**Visual style:**
- Built on Bootstrap 5.3.8, fully responsive
- Five predefined color skins: Default, Minimal, Modern, Flat, Galaxy
- Built-in light/dark mode support
- Collapsible, responsive sidebar navigation
- Overall aesthetic: clean, modern, "enterprise" oriented, developer-productivity framing

---

## Synthesis: Recurring page-builder modules across the 9 marketing themes

Across all 9 marketing/business themes, the same set of modules recurs
repeatedly regardless of stated niche (agency, IT services, SaaS,
multipurpose). This is the practical module palette a page builder needs to
cover the patterns from all 9 without duplicating theme-specific effort:

1. **Hero variants** — full-width hero, video-background hero, split hero
   (copy + product/app screenshot)
2. **Feature grid** — icon + heading + short copy, N-column
3. **Pricing table** — including tabbed/toggle variant (monthly vs. yearly)
4. **Testimonial section** — both static grid and slider/carousel variants
5. **Team section** — grid of people with photo/name/role (+ optional detail page)
6. **Stats/counter block** — animated number counters for key metrics
7. **Logo cloud / integration showcase** — "as seen in" / partner or integration logos
8. **CTA banner** — full-width conversion prompt, usually pre-footer
9. **Portfolio/project grid** — filterable grid with a detail/single-item layout
10. **Case study block** — a heavier variant of portfolio grid with narrative structure
11. **Blog teaser / news grid** — latest N posts in card layout
12. **FAQ accordion**
13. **Tabs widget** — generic tabbed content container (features, pricing, compatibility)
14. **Process/steps widget** — numbered step-by-step visualization ("how it works")
15. **Contact form section** — with optional embedded map
16. **Google Map / location embed**
17. **Header/footer builder** — configurable nav and footer regions treated as their own composable module, not page content
18. **Slider/carousel (generic)** — image/content slider independent of hero or testimonials
19. **Gallery/image grid** — general media grid distinct from portfolio (used for services like Inotek)
20. **Service/feature "card" variant** — a distinct visual treatment (icon-card, glowing-card, rotating-card) layered on top of the basic feature grid — worth treating as *style variants* of module #2 rather than separate modules

**Notes on scope for the admin-dashboard reference (Dhonu):** this is a
different artifact class (authenticated app UI, not marketing page), so its
modules (kanban board, invoice screen, calendar, chat, ticketing, 2FA/lock
screens) are relevant to an "admin/CRM" feature set, not the marketing page
builder module palette above. Keep them as a separate backlog if/when
building out an internal dashboard UI kit, rather than merging into the
page-builder module list.

---

## Failed/weak fetches to flag

- **Dhonu full_screen_preview URL** (item 10 as originally given) failed —
  it's a JS-rendered live app demo; WebFetch got only nav chrome. Substituted
  the plain CodeCanyon item page URL, which succeeded and is used above.
- **SaaSapp** (item 8) returned unusually thin content — the model could
  only confirm 7 shallow categories, explicitly noting the source page text
  didn't break out hero/team/stats details the way the others did. Flagged
  inline in its section above.
- **Stratus** (item 5) similarly thinner than most, explicitly noted in its
  section.
- All other 7 fetches (Brooklyn, Rayo, Engitech, NextSaaS, Inotek, Codera,
  Stackly) returned solid, itemized feature lists.

---

## Addendum (2026-09-26): CodeCanyon-style plugin/module additions, approved for the marketing-modules phase

Following a separate discussion of what CodeCanyon-style WordPress
plugins/pre-built modules would round out this app's own CMS, three
specific additions were proposed and approved to fold into whichever plan
covers the marketing-page module palette above, prioritized by how much they
reuse existing app subsystems rather than requiring new backend work:

1. **Booking/scheduling embed** — this app already has a full Scheduling
   feature under CRM (`admin/scheduling`); a page-builder block that embeds
   the existing booking widget on a public page is near-zero new backend
   work and high value (reuses an existing subsystem end-to-end).
2. **Form presets** (quote request, appointment request, contact) — the
   exact same pattern already approved for the newsletter-signup block in
   the blog-modules plan (a `CmsSectionTemplates.cs` entry pre-seeding the
   existing generic `form` widget with different fields), just applied to a
   few more common lead-capture shapes.
3. **FAQ block with schema markup** — an accordion widget already exists
   (`WidgetType == "accordion"`); adding FAQPage JSON-LD structured-data
   output alongside its existing HTML is a real, currently-missing SEO win
   — no JSON-LD/structured-data of any kind exists anywhere on this app's
   public pages today (confirmed during the responsive/UI-UX audit pass).

Also discussed but deliberately deferred/scoped carefully, not rejected:
- **Cookie consent banner** — genuinely useful, often legally expected
  (GDPR/CCPA), but compliance-sensitive; build deliberately rather than
  quickly if/when picked up.
- **Popup/announcement bar builder** — real conversion value, but should
  stay scoped to one dismissible bar rather than a full popup-trigger
  engine, to avoid becoming intrusive.

Explicitly not recommended: chat widgets, live "social proof" visitor
trackers, or anything requiring a live third-party account/integration —
real complexity for a narrow payoff unless specifically requested.
