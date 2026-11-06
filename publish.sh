#!/usr/bin/env bash
# Baut eigenständige Einzeldatei-Programme (kein .NET auf dem Zielrechner nötig).
set -euo pipefail
cd "$(dirname "$0")"
for rid in osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64; do
  echo "→ $rid"
  dotnet publish src/Linksta -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
    -p:PackAsTool=false -o "dist/$rid" >/dev/null
done
echo "Fertig: dist/<plattform>/linksta"
