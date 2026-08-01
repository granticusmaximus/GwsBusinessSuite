#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "Creating a transactionally consistent encrypted production-format backup..."
docker compose exec -T gwssuite dotnet GwsBusinessSuite.Web.dll --backup-create

echo "Restoring and verifying the latest backup in an isolated temporary directory..."
docker compose exec -T gwssuite dotnet GwsBusinessSuite.Web.dll --backup-verify

echo "Restore rehearsal passed. The temporary restored database and plaintext archive were removed."
