#!/usr/bin/env bash
# `open gws` (see ~/.zshrc): installs GWS Business Suite into /Applications when the source has
# changed (scripts/install-gws-mac.sh) and opens the installed app. Once installed, the app also
# opens from Spotlight, Launchpad, Finder or the Dock, and updates itself from its
# "Check for Updates..." menu item - the terminal isn't needed.
set -eu
exec "$(cd "$(dirname "$0")" && pwd)/install-gws-mac.sh" --open "$@"
