#!/bin/sh
# Removes the most recently added EF Core migration for ApplicationDbContext, as long as
# it hasn't been applied to a database yet (dotnet ef refuses otherwise).
# Usage: scripts/remove-migration.sh
set -e

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

dotnet ef migrations remove \
  --project "$REPO_ROOT/src/GwsBusinessSuite.Infrastructure" \
  --startup-project "$REPO_ROOT/src/GwsBusinessSuite.Web"
