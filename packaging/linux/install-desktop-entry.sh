#!/usr/bin/env bash
# Adds SnipFlow from this portable folder to the current user's application menu.
# Run again after moving the folder; run with --remove to delete the entry.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
entry="$data/applications/snipflow.desktop"
icon="$data/icons/hicolor/256x256/apps/snipflow.png"

if [[ "${1:-}" == "--remove" ]]; then
  rm -f "$entry" "$icon"
  echo "Removed SnipFlow from the application menu."
  exit 0
fi

mkdir -p "$(dirname "$entry")" "$(dirname "$icon")"
cp "$here/snipflow.png" "$icon"
# Quote the executable path for the Exec key (spaces and reserved characters).
escaped=$(printf '%s' "$here/SnipFlow" | sed -e 's/[\\"`$]/\\&/g')
sed -e "s|^Exec=snipflow|Exec=\"$escaped\"|" "$here/snipflow.desktop" > "$entry"
chmod 644 "$entry"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$data/applications" || true
echo "SnipFlow was added to the application menu."
