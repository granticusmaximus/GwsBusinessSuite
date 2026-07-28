#!/bin/sh
# Incrementally builds and opens the native GWS Business Suite Mac Catalyst app.
# Usage: scripts/open-gws-mac.sh
set -eu

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/GwsBusinessSuite.App/GwsBusinessSuite.App.csproj"
TARGET_FRAMEWORK="net10.0-maccatalyst"
CONFIGURATION="Debug"

case "$(uname -m)" in
  arm64)
    RUNTIME_IDENTIFIER="maccatalyst-arm64"
    ;;
  x86_64)
    RUNTIME_IDENTIFIER="maccatalyst-x64"
    ;;
  *)
    echo "Unsupported Mac architecture: $(uname -m)" >&2
    exit 1
    ;;
esac

APP_BUNDLE="$REPO_ROOT/src/GwsBusinessSuite.App/bin/$CONFIGURATION/$TARGET_FRAMEWORK/$RUNTIME_IDENTIFIER/GWS Business Suite.app"
PACKAGED_INFO_PLIST="$APP_BUNDLE/Contents/Info.plist"
APP_SOURCE_ROOT="$REPO_ROOT/src/GwsBusinessSuite.App"

XCODE_VERSION="$(xcodebuild -version | awk 'NR == 1 { print $2 }')"
VALIDATE_XCODE_VERSION="true"

# Xcode 27 is currently a beta toolchain. The installed .NET 10 workload can
# build this app with it, but its version guard only recognizes Xcode 26.6.
case "$XCODE_VERSION" in
  27.*)
    VALIDATE_XCODE_VERSION="false"
    echo "Using the installed .NET workload with Xcode $XCODE_VERSION compatibility mode."
    ;;
esac

NEEDS_BUILD="false"
if [ ! -f "$PACKAGED_INFO_PLIST" ]; then
  NEEDS_BUILD="true"
elif find "$APP_SOURCE_ROOT" \
  \( -path "$APP_SOURCE_ROOT/bin" -o -path "$APP_SOURCE_ROOT/obj" \) -prune -o \
  -type f -newer "$PACKAGED_INFO_PLIST" -print -quit | grep -q .; then
  NEEDS_BUILD="true"
fi

if [ "$NEEDS_BUILD" = "true" ]; then
  echo "GWS Mac app changes detected; refreshing the native app bundle..."
  dotnet build "$PROJECT" \
    --framework "$TARGET_FRAMEWORK" \
    --configuration "$CONFIGURATION" \
    --runtime "$RUNTIME_IDENTIFIER" \
    --target Rebuild \
    --nologo \
    -p:ValidateXcodeVersion="$VALIDATE_XCODE_VERSION"
else
  echo "The GWS Mac app is already up to date."
fi

echo "Opening GWS Business Suite for Mac..."
/usr/bin/open "$APP_BUNDLE"
