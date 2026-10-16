# Sentinel Web Clipper

A browser extension (Manifest V3) that clips the readable article, the selection, or just the link
of the current page into Sentinel: a new page under **Web Clips** or a section of today's daily
note. It works in Chrome, Edge and Safari. It is installed unpacked, not from a store.

## Set up in Sentinel

Open Sentinel, click **Web clipper** at the top, and create a token. Copy it straight away,
because it is only shown once. A token can only add clips. You can revoke it from the same panel.

## Chrome / Edge

1. Open `chrome://extensions` (or `edge://extensions`) and turn on **Developer mode**.
2. Click **Load unpacked** and pick this folder (`extensions/sentinel-web-clipper`).
3. Open the extension's settings, enter the server (e.g. `https://admin.gwsapp.net`) and the token,
   then click **Save and test**. Allow access to the server when the browser asks.

## Safari (macOS)

1. Run `./scripts/build-safari-clipper.sh`. It wraps this folder in a small macOS app with
   Apple's `safari-web-extension-converter` and builds it, ad-hoc signed, into
   `artifacts/safari-clipper/`.
2. Open the built `Sentinel Web Clipper.app` once.
3. In Safari, go to Settings > Advanced and turn on "Show features for web developers". Then in
   Develop, turn on "Allow Unsigned Extensions". Safari turns this off again each time it quits.
4. Safari > Settings > Extensions: enable **Sentinel Web Clipper**, then set it up as above.

## Shortcut

Cmd-Shift-S (Ctrl-Shift-S) opens the clipper. Change it at `chrome://extensions/shortcuts`.
