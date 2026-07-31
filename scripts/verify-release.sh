#!/usr/bin/env bash

set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
base_url=""
output_path=""
require_clean=false
skip_tests=false
install_playwright_deps=false

usage() {
  sed -n '2,80p' "$0" | sed -n '/^# Usage:/,/^$/p' | sed 's/^# \{0,1\}//'
}

# Usage:
#   ./scripts/verify-release.sh [--base-url https://admin.example.com]
#       [--output artifacts/release-readiness/report.md] [--require-clean]
#       [--install-playwright-deps] [--skip-tests]
#
# Runs privacy-safe local release gates and, when --base-url is supplied, deployed endpoint
# checks. The Markdown report contains statuses and durations only; command output remains in
# an automatically removed temporary directory and is printed only when a check fails.

while (($# > 0)); do
  case "$1" in
    --base-url)
      base_url="${2:-}"
      shift 2
      ;;
    --output)
      output_path="${2:-}"
      shift 2
      ;;
    --require-clean)
      require_clean=true
      shift
      ;;
    --skip-tests)
      skip_tests=true
      shift
      ;;
    --install-playwright-deps)
      install_playwright_deps=true
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -n "$base_url" ]]; then
  base_url="${base_url%/}"
  if [[ ! "$base_url" =~ ^https:// ]] \
      && [[ ! "$base_url" =~ ^http://(127\.0\.0\.1|localhost)(:[0-9]+)?$ ]]; then
    echo "--base-url must use HTTPS unless it targets localhost." >&2
    exit 2
  fi
fi

if [[ -z "$output_path" ]]; then
  timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
  output_path="$repo_root/artifacts/release-readiness/release-$timestamp.md"
elif [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

mkdir -p "$(dirname "$output_path")"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT
rows_file="$temp_dir/rows.md"
failed=0

slugify() {
  printf '%s' "$1" | tr '[:upper:] ' '[:lower:]-' | tr -cd 'a-z0-9_-'
}

run_check() {
  local name="$1"
  shift
  local log_file="$temp_dir/$(slugify "$name").log"
  local started ended elapsed
  started="$(date +%s)"
  echo "Checking: $name"
  if "$@" >"$log_file" 2>&1; then
    status="PASS"
  else
    status="FAIL"
    failed=1
    echo "Failed: $name" >&2
    tail -80 "$log_file" >&2
  fi
  ended="$(date +%s)"
  elapsed=$((ended - started))
  printf '| %s | %s | %ss |\n' "$name" "$status" "$elapsed" >>"$rows_file"
}

check_clean_worktree() {
  [[ -z "$(git status --porcelain=v1 --untracked-files=all)" ]]
}

check_http_200() {
  local url="$1"
  local code
  code="$(curl --silent --show-error --location --output /dev/null --write-out '%{http_code}' \
    --connect-timeout 10 --max-time 30 "$url")"
  [[ "$code" == "200" ]]
}

cd "$repo_root"
commit="$(git rev-parse --verify HEAD 2>/dev/null || printf 'unknown')"
branch="$(git branch --show-current 2>/dev/null || printf 'detached')"
worktree_state="Clean"
if [[ -n "$(git status --porcelain=v1 --untracked-files=all)" ]]; then
  worktree_state="Modified"
fi

run_check "Restore" dotnet restore GwsBusinessSuite.slnx
run_check "Dependency vulnerability audit" \
  dotnet list GwsBusinessSuite.slnx package --vulnerable --include-transitive --no-restore
run_check "Release build" \
  dotnet build GwsBusinessSuite.slnx -c Release --no-restore --disable-build-servers -m:1

if [[ "$install_playwright_deps" == true ]]; then
  run_check "Playwright browser install" \
    pwsh tests/GwsBusinessSuite.Tests/bin/Release/net10.0/playwright.ps1 \
      install --with-deps chromium
fi

if [[ "$skip_tests" == true ]]; then
  printf '| Full automated suite | NOT RUN | 0s |\n' >>"$rows_file"
else
  run_check "Full automated suite" \
    dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj \
      -c Release --no-build --disable-build-servers -m:1
fi

run_check "Docker Compose rendering" docker compose config --quiet
run_check "Patch whitespace" git diff --check

if [[ "$require_clean" == true ]]; then
  run_check "Clean working tree" check_clean_worktree
fi

if [[ -n "$base_url" ]]; then
  run_check "Deployed liveness" check_http_200 "$base_url/health/live"
  run_check "Deployed readiness" check_http_200 "$base_url/health/ready"
  run_check "Deployed login surface" check_http_200 "$base_url/admin/login"
else
  printf '| Deployed liveness | NOT RUN | 0s |\n' >>"$rows_file"
  printf '| Deployed readiness | NOT RUN | 0s |\n' >>"$rows_file"
  printf '| Deployed login surface | NOT RUN | 0s |\n' >>"$rows_file"
fi

overall="PASS"
if ((failed != 0)); then
  overall="FAIL"
elif [[ "$skip_tests" == true || -z "$base_url" ]]; then
  overall="PARTIAL"
fi

cat >"$output_path" <<EOF
# GWS release verification

- Generated (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)
- Commit: \`$commit\`
- Branch: \`$branch\`
- Working tree: **$worktree_state**
- Overall: **$overall**
- Deployed target: $(if [[ -n "$base_url" ]]; then printf '`%s`' "$base_url"; else printf 'Not supplied'; fi)

The report intentionally contains no command output, credentials, prompts, imported content,
or private application data. Failed command output was shown only in the invoking terminal.

| Check | Status | Duration |
| --- | --- | ---: |
$(cat "$rows_file")
EOF

echo "Release verification: $overall"
echo "Report: $output_path"
exit "$failed"
