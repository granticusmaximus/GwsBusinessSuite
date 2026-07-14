#!/bin/sh
# Adds a new EF Core migration for ApplicationDbContext.
# Usage: scripts/add-migration.sh <MigrationName>
#
# Wraps the --project/--startup-project flags this repo needs (Infrastructure holds
# the DbContext/migrations, Web is the startup project that provides the connection
# string) so they don't have to be remembered/re-typed by hand every time.
set -e

if [ -z "$1" ]; then
  echo "Usage: scripts/add-migration.sh <MigrationName>" >&2
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

dotnet ef migrations add "$1" \
  --project "$REPO_ROOT/src/GwsBusinessSuite.Infrastructure" \
  --startup-project "$REPO_ROOT/src/GwsBusinessSuite.Web"

echo
echo "Migration '$1' added. Review the generated file(s) under"
echo "src/GwsBusinessSuite.Infrastructure/Migrations/ before committing - the app applies"
echo "pending migrations automatically on startup (see Program.cs), so nothing further"
echo "is needed to deploy it."
