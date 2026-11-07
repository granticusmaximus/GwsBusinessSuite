# The Mac app's native side looks like the web app (06-11-26) - NOT STARTED

Grant: "I am basically wanting the actual mac native side in general match that of the web
interface."

## What's there now (checked 06-11-26)

- **Native pages** in `src/GwsBusinessSuite.App`: MainPage (the workspace shell around the
  WebView), SentinelGptPage, CapturePage, VoiceNotesPage, OfflinePage, SignInApprovalPage, plus
  StreamingCaretView and TypingIndicatorView.
- **Colours:** about 214 hex values typed straight into the XAML (stone #1C1917/#231F1D, text
  #FAFAF9/#A8A29E/#78716C, amber #F59E0B). Dark only. `Resources/Styles/Colors.xaml` and
  `Styles.xaml` are the MAUI template's and aren't the web app's.
- **Text:** mostly 11-12 pt; the web app's body text is 14-15.
- **SentinelGptPage:**
  - 18 buttons, 3 Pickers (Catalyst needs `MacMenuPickerHandler`; never a plain Picker), a
    single-line Entry.
  - Agent progress shown as text, not the web panel's cards.

## Plan

1. **Tokens, not hex values.**
   - Carry the web theme's CSS variables (`wwwroot/css/app.css` `:root` and its dark-theme
     blocks, `--gws-*`) into `Colors.xaml` as named colours for light and dark (AppThemeBinding).
   - Follow the person's theme choice: the web app keeps it per user (UserPreferences). Read it
     through the native session API, falling back to macOS appearance.
   - Replace every hex value in the XAML.
   - Add a test that fails if a hex value creeps back into a native page.
2. **Shared styles** (`Styles.xaml`), matching the web app:
   - Type: the web's font (bundle it if it isn't a system font), body 14, small 12, headings as the web's.
   - Buttons: primary (amber), outline, link.
   - Input box, multi-line Editor, card (Border, radius and padding as the web cards), chip,
     status pill.
3. **Shared native pieces:**
   - A PageHeader (icon, title, description, actions) on every native page.
   - The web's loading screen and empty states.
   - A toast.
   - The workspace rail mirroring the web nav (tech-forward-ux T11).
4. **Pages,** one at a time:
   - SentinelGptPage first:
     - steps list, action cards with Open/Undo, Confirm/Decline, plan card with Go;
     - a "where it ran" line;
     - composer at the bottom (multi-line, amber Send, Cmd+Enter), suggestion chips;
     - model, Deep Analysis and folder in one Options menu;
     - says so when it answers without the app's tools.
   - Then VoiceNotesPage, CapturePage, OfflinePage, SignInApprovalPage and MainPage's chrome.
5. **Check on screen:**
   - Run the Mac app locally (compile with `-p:ValidateXcodeVersion=false`).
   - Screenshot each page in light and dark, next to its web counterpart.
   - It reaches Grant through a Mac app update.
