#!/usr/bin/env bash
# Installs GWS Business Suite for Mac (and the Sentinel menu-bar companion) as real apps in
# /Applications, so they open from Spotlight, Launchpad, Finder or the Dock like any other app.
# Rebuilds from this repo first when the source has changed since the installed copy was built.
#
#   ./scripts/install-gws-mac.sh             build if needed and install
#   ./scripts/install-gws-mac.sh --open      ...then open the app (what `open gws` runs)
#   ./scripts/install-gws-mac.sh --relaunch  ...quit the running app first and reopen it after
#                                            (what the app's "Check for Updates..." runs)
#   ./scripts/install-gws-mac.sh --check     only report whether an update is waiting (exit 10 if so)
#
# Local ad-hoc builds only (no Apple Developer signing): a locally built app carries no quarantine
# flag, so Gatekeeper opens it without prompts. See the Aug 2026 keychain note in the plan files
# for why the app isn't provisioned.
set -euo pipefail

# Started from the app (Check for Updates...), this runs with a GUI app's minimal PATH: add the
# usual dotnet / Homebrew locations.
export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:$HOME/.dotnet:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIGURATION="Release"
APP_NAME="GWS Business Suite"
APP_ID="net.gwsapp.businesssuite"
APP_PROJECT_DIR="$REPO_ROOT/src/GwsBusinessSuite.App"
MENU_PROJECT_DIR="$REPO_ROOT/src/GwsBusinessSuite.SentinelMenuBar"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

OPEN_AFTER="false"
RELAUNCH="false"
CHECK_ONLY="false"
for arg in "$@"; do
  case "$arg" in
    --open) OPEN_AFTER="true" ;;
    --relaunch) RELAUNCH="true"; OPEN_AFTER="true" ;;
    --check) CHECK_ONLY="true" ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

case "$(uname -m)" in
  arm64) ARCH="arm64" ;;
  x86_64) ARCH="x64" ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac

if [ -w /Applications ]; then INSTALL_DIR="/Applications"; else INSTALL_DIR="$HOME/Applications"; mkdir -p "$INSTALL_DIR"; fi
INSTALLED_APP="$INSTALL_DIR/$APP_NAME.app"
INSTALLED_MENU="$INSTALL_DIR/Sentinel Menu Bar.app"
BUILT_APP="$APP_PROJECT_DIR/bin/$CONFIGURATION/net10.0-maccatalyst/maccatalyst-$ARCH/$APP_NAME.app"

# Xcode 27 is newer than the .NET workload's pinned Xcode; the workload still builds with it.
VALIDATE_XCODE="true"
case "$(xcodebuild -version 2>/dev/null | awk 'NR == 1 { print $2 }')" in
  27.*) VALIDATE_XCODE="false" ;;
esac

# True when any source file under $1 is newer than $2 (or $2 doesn't exist).
sources_newer_than() {
  local source_dir="$1" stamp="$2"
  [ ! -e "$stamp" ] && return 0
  find "$source_dir" \( -path "$source_dir/bin" -o -path "$source_dir/obj" \) -prune -o \
    -type f -newer "$stamp" -print -quit | grep -q .
}

app_needs_update="false"
if sources_newer_than "$APP_PROJECT_DIR" "$INSTALLED_APP/Contents/Info.plist"; then app_needs_update="true"; fi

if [ "$CHECK_ONLY" = "true" ]; then
  if [ "$app_needs_update" = "true" ]; then echo "An update is waiting to be installed."; exit 10; fi
  echo "GWS Business Suite is up to date."
  exit 0
fi

if [ "$RELAUNCH" = "true" ]; then
  # Let the running app close cleanly (it saves its own state) before replacing it.
  /usr/bin/osascript -e "tell application id \"$APP_ID\" to quit" >/dev/null 2>&1 || true
  for _ in $(seq 1 30); do
    pgrep -f "$APP_NAME.app/Contents/MacOS" >/dev/null 2>&1 || break
    sleep 1
  done
fi

if [ "$app_needs_update" = "true" ]; then
  echo "Building $APP_NAME ($CONFIGURATION)..."
  dotnet build "$APP_PROJECT_DIR/GwsBusinessSuite.App.csproj" \
    --framework net10.0-maccatalyst --configuration "$CONFIGURATION" --runtime "maccatalyst-$ARCH" \
    --nologo -v quiet -p:ValidateXcodeVersion="$VALIDATE_XCODE"
  echo "Installing to $INSTALLED_APP..."
  rm -rf "$INSTALLED_APP"
  # ditto keeps the bundle's signature and symlinks intact.
  /usr/bin/ditto "$BUILT_APP" "$INSTALLED_APP"
  # Fresh timestamp: the next check compares the source against this install.
  touch "$INSTALLED_APP/Contents/Info.plist"
  "$LSREGISTER" -f "$INSTALLED_APP" >/dev/null 2>&1 || true
else
  echo "$APP_NAME is already up to date in $INSTALL_DIR."
fi

# The menu-bar companion (optional: skipped quietly when its project or workload isn't there).
if [ -d "$MENU_PROJECT_DIR" ] && sources_newer_than "$MENU_PROJECT_DIR" "$INSTALLED_MENU/Contents/Info.plist"; then
  echo "Building the Sentinel menu-bar companion..."
  if dotnet build "$MENU_PROJECT_DIR/GwsBusinessSuite.SentinelMenuBar.csproj" -c "$CONFIGURATION" --nologo -v quiet \
       -p:ValidateXcodeVersion=false -p:EnableCodeSigning=false; then
    built_menu="$(find "$MENU_PROJECT_DIR/bin/$CONFIGURATION" -maxdepth 3 -name '*.app' -type d | head -1)"
    if [ -n "$built_menu" ]; then
      running_menu="false"
      if pgrep -f "Sentinel Menu Bar.app/Contents/MacOS" >/dev/null 2>&1 || pgrep -f "$(basename "$built_menu")/Contents/MacOS" >/dev/null 2>&1; then
        running_menu="true"
        pkill -f "Sentinel Menu Bar.app/Contents/MacOS" >/dev/null 2>&1 || true
      fi
      rm -rf "$INSTALLED_MENU"
      /usr/bin/ditto "$built_menu" "$INSTALLED_MENU"
      touch "$INSTALLED_MENU/Contents/Info.plist"
      "$LSREGISTER" -f "$INSTALLED_MENU" >/dev/null 2>&1 || true
      [ "$running_menu" = "true" ] && /usr/bin/open "$INSTALLED_MENU"
      echo "Installed the menu-bar companion to $INSTALLED_MENU."
    fi
  else
    echo "The menu-bar companion didn't build - skipped (the main app is installed)." >&2
  fi
fi

if [ "$OPEN_AFTER" = "true" ]; then
  echo "Opening $APP_NAME..."
  /usr/bin/open "$INSTALLED_APP"
fi
