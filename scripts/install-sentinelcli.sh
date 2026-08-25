#!/bin/sh
# Installs the self-contained SentinelCLI tool for the current macOS user.
# Usage: ./scripts/install-sentinelcli.sh [--sync-models]
set -eu

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
project="$repo_root/src/SentinelCLI/SentinelCLI.csproj"
install_root="${SENTINELCLI_INSTALL_ROOT:-$HOME/.local/share/gws/sentinelcli}"
bin_dir="${SENTINELCLI_BIN_DIR:-$HOME/.local/bin}"
skills_root="$HOME/.config/sentinelcli/skills"
legacy_install_root="$HOME/.local/share/gws/sentinelgpt"
legacy_skills_root="$HOME/.config/sentinelgpt/skills"
sync_models=false

case "${1:-}" in
  "") ;;
  --sync-models) sync_models=true ;;
  *) echo "Usage: $0 [--sync-models]" >&2; exit 2 ;;
esac

case "$(uname -s):$(uname -m)" in
  Darwin:arm64) runtime_identifier="osx-arm64" ;;
  Darwin:x86_64) runtime_identifier="osx-x64" ;;
  *) echo "SentinelCLI installation currently supports macOS arm64 and x64." >&2; exit 1 ;;
esac

mkdir -p "$install_root" "$bin_dir" "$skills_root"

# Copy forward user-authored state from the former sentinelgpt command without deleting it.
# Existing SentinelCLI state always wins, so reinstalling is idempotent.
if [ -d "$legacy_install_root/sessions" ] && [ ! -d "$install_root/sessions" ]; then
  mkdir -p "$install_root/sessions"
  cp -R "$legacy_install_root/sessions/." "$install_root/sessions/"
  echo "Copied existing CLI sessions into $install_root/sessions"
fi
if [ -d "$legacy_skills_root" ] && [ -z "$(find "$skills_root" -mindepth 1 -maxdepth 1 -print -quit)" ]; then
  cp -R "$legacy_skills_root/." "$skills_root/"
  echo "Copied existing CLI skills into $skills_root"
fi

dotnet publish "$project" \
  --configuration Release \
  --runtime "$runtime_identifier" \
  --self-contained true \
  --output "$install_root" \
  -p:PublishSingleFile=true \
  --nologo

ln -sfn "$install_root/sentinelcli" "$bin_dir/sentinelcli"

echo "Installed SentinelCLI at $bin_dir/sentinelcli"

# Retire only the exact symlink created by the former GWS installer. A user-managed command
# with the same name is deliberately left alone.
legacy_link="$bin_dir/sentinelgpt"
if [ -L "$legacy_link" ] && [ "$(readlink "$legacy_link")" = "$legacy_install_root/sentinelgpt" ]; then
  rm "$legacy_link"
  echo "Retired the former sentinelgpt command; use sentinelcli from now on."
fi
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) echo "Add $bin_dir to PATH before opening a new terminal." ;;
esac

if [ "$sync_models" = true ]; then
  "$bin_dir/sentinelcli" models sync --yes
else
  echo "Run 'sentinelcli models sync' to install or refresh the canonical GWS Ollama models."
fi
