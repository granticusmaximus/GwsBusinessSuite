# GWS Business Suite UI design system

## Cross-platform contract

The ASP.NET Core/Blazor UI is the canonical presentation layer for the browser, Windows, macOS,
iOS, Android, and Linux. MAUI and Electron are deliberately transparent hosts: they must not add
navigation, page headers, editing controls, or alternate application layouts around the hosted
UI. This keeps interaction behavior, accessibility, and responsive changes uniform everywhere.

Platform-specific UI is limited to operating-system integration and states where the hosted app
cannot render, such as splash, loading, offline, permissions, file pickers, notifications, and
external-link handoff. Those states follow the same tokens and language as the web app.

## Canonical tokens

The source of truth is `src/GwsBusinessSuite.Web/wwwroot/app.css`.

| Role | Token/value | Usage |
| --- | --- | --- |
| App background | `#141210` | Window, splash, full-screen fallback |
| Panel | `#1c1917` | Cards and offline surfaces |
| Raised panel | `#231f1d` | Inputs and secondary surfaces |
| Border | `#292524` | Quiet component boundaries |
| Strong text | `#fafaf9` | Titles and primary content |
| Soft text | `#a8a29e` | Supporting copy and metadata |
| Accent | `#f59e0b` | Primary actions and active state |
| Accent highlight | `#fbbf24` | Hover and emphasis |
| Small radius | `8px` | Buttons and controls |
| Medium radius | `12px` | Panels and dialogs |
| UI type | `Inter`, system sans-serif | Controls, navigation, body text |
| Editorial type | `Playfair Display`, serif fallback | Deliberate brand moments only |

If a visual token changes, update `app.css` first, then the MAUI resource dictionary, app/splash
art, and Electron offline document in the same change.

## Interaction rules

- Use inline editing in Sentinel; do not introduce a separate preview pane.
- Keep primary navigation, page hierarchy, terminology, and action placement identical across
  environments.
- Design from responsive web components instead of maintaining desktop and mobile page forks.
- Keep touch targets at least 44 CSS pixels on coarse-pointer devices and expose actions that
  would otherwise depend on hover.
- Respect safe-area insets and dynamic viewport height on mobile devices.
- Let trusted application links remain inside the host and open external HTTPS links with the
  operating system.
- Use native controls only when the operating system owns the interaction.

## Review checklist

Every interface change should be checked at phone, tablet, and desktop widths in the browser.
Changes to the client shell must also build for Android and be syntax/package checked for Linux;
Windows and Apple packaging are validated on their matching build hosts.
