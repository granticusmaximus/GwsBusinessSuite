#!/usr/bin/env bash
set -e
set -m  # job control: each backgrounded job gets its own process group

DOTNET_PORT=5000

# Kill any leftover processes holding the .NET port before starting
existing=$(lsof -ti :$DOTNET_PORT 2>/dev/null || true)
if [ -n "$existing" ]; then
  echo "Clearing stale process on port $DOTNET_PORT (PID $existing)..."
  kill $existing 2>/dev/null || true
fi

# Kill both child processes (and their grandchildren) when Ctrl+C is pressed.
# dotnet watch and npm run dev each spawn their own child process (the actual
# dotnet runtime, the actual vite server) - killing just the captured PID
# misses those. Signal the whole process group instead, SIGINT first since
# dotnet watch's graceful shutdown specifically listens for that, then fall
# back to SIGKILL if anything is still alive after a short grace period.
cleanup() {
  echo ""
  echo "Stopping dev servers..."
  kill -INT -- -"$DOTNET_PID" -"$VITE_PID" 2>/dev/null

  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if ! kill -0 "$DOTNET_PID" 2>/dev/null && ! kill -0 "$VITE_PID" 2>/dev/null; then
      break
    fi
    sleep 0.5
  done

  kill -KILL -- -"$DOTNET_PID" -"$VITE_PID" 2>/dev/null || true

  remaining=$(lsof -ti :$DOTNET_PORT 2>/dev/null || true)
  if [ -n "$remaining" ]; then
    kill -KILL $remaining 2>/dev/null || true
  fi

  exit 0
}
trap cleanup INT TERM

dotnet watch --project src/GwsBusinessSuite.Web run --urls "http://localhost:$DOTNET_PORT" &
DOTNET_PID=$!

(cd apps/public-site && npm run dev) &
VITE_PID=$!

wait "$DOTNET_PID" "$VITE_PID"
