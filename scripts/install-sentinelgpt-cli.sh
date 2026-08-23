#!/bin/sh
# Installs the self-contained SentinelGPT CLI for the current macOS user.
# Usage: ./scripts/install-sentinelgpt-cli.sh [--sync-models]
set -eu

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
project="$repo_root/src/GwsBusinessSuite.SentinelCli/GwsBusinessSuite.SentinelCli.csproj"
install_root="${SENTINELGPT_INSTALL_ROOT:-$HOME/.local/share/gws/sentinelgpt}"
bin_dir="${SENTINELGPT_BIN_DIR:-$HOME/.local/bin}"
sync_models=false

case "${1:-}" in
  "") ;;
  --sync-models) sync_models=true ;;
  *) echo "Usage: $0 [--sync-models]" >&2; exit 2 ;;
esac

case "$(uname -s):$(uname -m)" in
  Darwin:arm64) runtime_identifier="osx-arm64" ;;
  Darwin:x86_64) runtime_identifier="osx-x64" ;;
  *) echo "SentinelGPT CLI installation currently supports macOS arm64 and x64." >&2; exit 1 ;;
esac

mkdir -p "$install_root" "$bin_dir"
dotnet publish "$project" \
  --configuration Release \
  --runtime "$runtime_identifier" \
  --self-contained true \
  --output "$install_root" \
  -p:PublishSingleFile=true \
  --nologo

ln -sfn "$install_root/sentinelgpt" "$bin_dir/sentinelgpt"

echo "Installed SentinelGPT CLI at $bin_dir/sentinelgpt"
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) echo "Add $bin_dir to PATH before opening a new terminal." ;;
esac

if [ "$sync_models" = true ]; then
  "$bin_dir/sentinelgpt" models sync --yes
else
  echo "Run 'sentinelgpt models sync' to install or refresh the canonical GWS Ollama models."
fi
