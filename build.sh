#!/usr/bin/env bash
# Build and publish AquariumSaver on Linux (cross-compile to Windows)
set -euo pipefail

cd "$(dirname "$0")"

echo "=== AquariumSaver Build ==="

echo "[1/2] Publishing for win-x64 (self-contained)..."
PUBLISH_DIR="./publish"
rm -rf "$PUBLISH_DIR"

dotnet publish AquariumSaver.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishReadyToRun=true \
  -o "$PUBLISH_DIR" \
  --verbosity quiet

echo "[2/2] Renaming to .scr..."
if [ -f "$PUBLISH_DIR/AquariumSaver.exe" ]; then
  mv "$PUBLISH_DIR/AquariumSaver.exe" "$PUBLISH_DIR/AquariumSaver.scr"
  echo "  Created: $PUBLISH_DIR/AquariumSaver.scr"
else
  echo "  ERROR: AquariumSaver.exe not found."
  exit 1
fi

echo "=== Build complete ==="
echo "Screensaver: $PUBLISH_DIR/AquariumSaver.scr"
