#!/usr/bin/env bash
set -e
set -m  # job control: each backgrounded job gets its own process group

DOTNET_PORT=5050  # avoid 5000: macOS Control Center / AirPlay Receiver claims it by default

# Kill any leftover processes holding the .NET port before starting
existing=$(lsof -ti :$DOTNET_PORT 2>/dev/null || true)
if [ -n "$existing" ]; then
  echo "Clearing stale process on port $DOTNET_PORT (PID $existing)..."
  kill $existing 2>/dev/null || true
fi

# Kill dotnet watch and its child runtime when Ctrl+C is pressed. Signal the
# whole process group rather than only the watch PID, SIGINT first since
# dotnet watch's graceful shutdown specifically listens for that, then fall
# back to SIGKILL if anything is still alive after a short grace period.
cleanup() {
  echo ""
  echo "Stopping dev server..."
  kill -INT -- -"$DOTNET_PID" 2>/dev/null

  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if ! kill -0 "$DOTNET_PID" 2>/dev/null; then
      break
    fi
    sleep 0.5
  done

  kill -KILL -- -"$DOTNET_PID" 2>/dev/null || true

  remaining=$(lsof -ti :$DOTNET_PORT 2>/dev/null || true)
  if [ -n "$remaining" ]; then
    kill -KILL $remaining 2>/dev/null || true
  fi

  exit 0
}
trap cleanup INT TERM

dotnet watch --project src/GwsBusinessSuite.Web run --urls "http://localhost:$DOTNET_PORT" </dev/null &
DOTNET_PID=$!

wait "$DOTNET_PID"
