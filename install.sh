#!/usr/bin/env bash
# Installiert linksta nach ~/.local/bin (bzw. $1). Atomarer Austausch: laufende linksta-Prozesse stürzen nicht ab.
set -euo pipefail
cd "$(dirname "$0")"
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) rid=osx-arm64 ;; Darwin-x86_64) rid=osx-x64 ;;
  Linux-aarch64) rid=linux-arm64 ;; Linux-x86_64) rid=linux-x64 ;;
  *) echo "Unbekannte Plattform"; exit 1 ;;
esac
dest="${1:-$HOME/.local/bin}"
mkdir -p "$dest"
[ -f "dist/$rid/linksta" ] || ./publish.sh
cp "dist/$rid/linksta" "$dest/.linksta.new"
mv -f "$dest/.linksta.new" "$dest/linksta"
echo "✓ installiert: $dest/linksta"
