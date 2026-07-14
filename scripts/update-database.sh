#!/bin/sh
# Applies pending EF Core migrations directly to the connection string configured for
# the Web project (appsettings.Development.json locally), without starting the app.
# Optional - GwsBusinessSuite.Web already calls dbContext.Database.MigrateAsync() on
# startup (see Program.cs), so this is only useful for inspecting/applying schema
# changes ahead of actually running the app (e.g. before pointing a DB tool at it).
# Usage: scripts/update-database.sh
set -e

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

dotnet ef database update \
  --project "$REPO_ROOT/src/GwsBusinessSuite.Infrastructure" \
  --startup-project "$REPO_ROOT/src/GwsBusinessSuite.Web"
