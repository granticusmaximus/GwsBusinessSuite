#!/usr/bin/env bash
set -e

DOTNET_PORT=5000

# Kill any leftover processes holding the .NET port before starting
existing=$(lsof -ti :$DOTNET_PORT 2>/dev/null || true)
if [ -n "$existing" ]; then
  echo "Clearing stale process on port $DOTNET_PORT (PID $existing)..."
  kill $existing 2>/dev/null || true
fi

# Kill both child processes when Ctrl+C is pressed
cleanup() {
  kill "$DOTNET_PID" "$VITE_PID" 2>/dev/null
  wait "$DOTNET_PID" "$VITE_PID" 2>/dev/null
  exit 0
}
trap cleanup INT TERM

dotnet watch --project src/GwsBusinessSuite.Web run --urls "http://localhost:$DOTNET_PORT" &
DOTNET_PID=$!

(cd apps/public-site && npm run dev) &
VITE_PID=$!

wait "$DOTNET_PID" "$VITE_PID"
