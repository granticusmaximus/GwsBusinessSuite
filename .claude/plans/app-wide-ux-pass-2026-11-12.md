# App-wide UI/UX pass (12-11-26)

Grant asked for everything from the app-wide review (22 pages x phone 390, iPad 820 portrait,
laptop 1440, Mac WebKit 1280; 88 captures with metrics), started immediately. Evidence: no
sideways scroll anywhere; touch sizing works; findings below.

Rules for this pass: keep the tree shippable at every step; never build the App in Release in its
default bin/obj (collides with `open gws`, crashed the app 12-11-26); verify-release at the end
of each step.

## Step 1: shell (every page)

- [x] 1. iPad portrait / narrow windows: sidebar keeps rail + sub-nav (272px of 820). Below the
  laptop tier show the 60px rail only; the sub-nav opens as a drawer.
- [x] 2. Phone page header takes 27-45% of the first screen (content at 232-377px of 844):
  one-line description, actions as icons in the title row, Pin into a "..." menu.

## Step 2: consistency

- [x] 3. One typeface: serif display titles on Threat Intelligence and Growth Studio.
- [x] 4. Page panels onto the --gws-neu-* surfaces (1 of 73 scoped sheets used them; 21 raw hex).
- [x] 7. Copy pass: engineering language in UI ("local SQLite persistence", config key names).
- [x] 8. Rules drift: 15 bare spinners -> LoadingIndicator; 9 sheets with viewport @media widths
  -> @container gws-main; reduced motion app-wide.

## Step 3: pages

- [x] 5. Phone toolbars/filters: CRM 4 rows of controls; Sentinel 8 buttons in 3 rows (labels
  wrap at 1280 in the Mac app). Search + Filters sheet; icon toolbars with More.
- [x] 6. Dashboard on phones: zero KPI cards one per row; two-up compact, all-zero -> "All clear".
- [x] 9. Overwatch phone: 81 text nodes < 12px; side panel covers the globe -> bottom sheet.
- [x] 10. Cesium assets fetched (and erroring) on Billing / Threat Intel: prefetch only when
  Overwatch is likely next.

## Step 4: Mac polish

- [x] 11. App-wide Mac shortcuts (Cmd+F, Cmd+[, Cmd+,), menu-bar commands, minimum window size.
- [x] 12. "Try the compact layout?" card covers content until answered -> a one-time toast.

## Progress

- 12-11-26: plan written; starting step 1.
- Step 1 done: tablet tier (app.js isTablet/setTabletNavigation, gws-responsive.css 768-1023.98),
  ShellTabletNavigationBrowserTests; PageHeader phone compaction (pin in breadcrumb line, 2-line
  description with title tooltip, natural-width actions). Header bottom on phone: Mail 232->176,
  Threat Intel 334->230, Settings 321->261, Growth 377->295. 17/17 live checks (iPad WebKit,
  1000px Chromium, laptop).
- Step 2 done: page-header-rich and all Georgia in page CSS -> --gws-font-sans; Growth follows
  the theme (palette = app tokens, no pinned --gws-*; literals mapped, text-on-amber kept dark);
  Intelligence pages share the amber accent (NEW badges dark text); Civic Watch hero title fixed
  (inherited dark heading in light theme) and palette; 15 flat card blocks raised; 4 panel
  loaders -> LoadingIndicator; 6 page sheets @media -> @container (and the breakpoint lint
  missed `@media(` without a space: fixed); app-wide reduced motion; copy (CRM, SEO, Mail).
  DesignSystemGuardTests keeps it. Checked dark + light on 7 pages.
- Steps 3-4 done (12-11-26): ListViewToolbar name box on demand (Save view... -> name, Save,
  Cancel; Return/Esc); Sentinel toolbar labels in spans, icons below 1100px content width, one
  scrolling row on phones, aria-labels; Home attention cards two-up on phones with "All clear"
  when every count is 0; Overwatch: every font-size < .75rem is max(size, 12px) (phone tiny
  labels 81 -> 0), phone sheet max 40%; overwatch-prefetch skips the Mac app and phones, fires on
  hover/focus of the Overwatch link; DensityOffer is a toast (close, or 15 s visible = keep);
  Mac app: Settings (Cmd+,), View > Back/Forward (Cmd+[ / Cmd+]), Edit > Find (Cmd+F -> gws-find:
  page search or command palette), MacToolbarActions.EvaluateScript, window minimum 820x600.
  Tests: OverwatchPrefetchBrowserTests, MacFind in ShellTabletNavigationBrowserTests,
  DesignSystemGuardTests (12px floor, Mac commands). Live: 10/10 (save view, toast), phone/Mac
  captures with no errors. Not clickable from here: the native Mac menu (built Debug, source-tested).

