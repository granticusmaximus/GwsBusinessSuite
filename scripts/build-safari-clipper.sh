#!/usr/bin/env bash
# Wraps extensions/sentinel-web-clipper in a macOS app (Safari web extensions ship inside an app)
# and builds it ad-hoc signed into artifacts/safari-clipper/. Local use only: Safari loads it with
# Develop > "Allow Unsigned Extensions". See extensions/sentinel-web-clipper/README.md.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd -P)"
source_dir="$repo/extensions/sentinel-web-clipper"
out="$repo/artifacts/safari-clipper"
app_name="Sentinel Web Clipper"

xcrun safari-web-extension-converter "$source_dir" \
  --project-location "$out/project" \
  --app-name "$app_name" \
  --bundle-identifier net.gwsapp.SentinelWebClipper \
  --swift --macos-only --copy-resources --no-open --no-prompt --force

# A synced ~/Desktop (iCloud file provider) tags files with extended attributes that codesign
# rejects ("resource fork, Finder information, or similar detritus not allowed"), so build in a
# temporary folder and copy the signed app back.
build_dir="$(mktemp -d)/build"
trap 'rm -rf "$(dirname "$build_dir")"' EXIT

project="$(find "$out/project" -maxdepth 2 -name '*.xcodeproj' | head -1)"
# The converter derives the app's id from the app name ("net.gwsapp.Sentinel-Web-Clipper") but
# gives the extension "<bundle id>.Extension"; Xcode requires the extension id to start with the
# app's, so align the app to the requested id.
sed -i '' 's/"net.gwsapp.Sentinel-Web-Clipper"/net.gwsapp.SentinelWebClipper/' "$project/project.pbxproj"
xcodebuild -project "$project" -scheme "$app_name" -configuration Release \
  -derivedDataPath "$build_dir" \
  CODE_SIGN_IDENTITY="-" CODE_SIGNING_REQUIRED=NO DEVELOPMENT_TEAM="" \
  build > "$out/xcodebuild.log" 2>&1 || {
    grep -E "error:|detritus|BUILD FAILED" "$out/xcodebuild.log" >&2
    echo "Safari build failed - full log: $out/xcodebuild.log" >&2
    exit 1
  }

rm -rf "$out/$app_name.app"
ditto --noextattr "$build_dir/Build/Products/Release/$app_name.app" "$out/$app_name.app"
app="$out/$app_name.app"
echo "Built: $app"
echo "Open it once, then enable the extension in Safari > Settings > Extensions."
