#!/usr/bin/env bash
# Build and publish AquariumSaver on Linux (cross-compile to Windows)
set -euo pipefail

cd "$(dirname "$0")"

# Ensure dotnet is available — install user-local if missing
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

if ! command -v dotnet &>/dev/null; then
  echo "dotnet not found. Installing .NET 8.0 SDK..."
  INSTALL_SCRIPT="/tmp/dotnet-install.sh"
  if [ ! -f "$INSTALL_SCRIPT" ]; then
    wget -q https://dot.net/v1/dotnet-install.sh -O "$INSTALL_SCRIPT"
  fi
  chmod +x "$INSTALL_SCRIPT"
  "$INSTALL_SCRIPT" --channel 8.0 --install-dir "$DOTNET_ROOT"
fi

echo "=== AquariumSaver Build ==="
echo ".NET SDK: $(dotnet --version)"

echo "[1/3] Restoring packages..."
dotnet restore AquariumSaver.csproj --verbosity quiet

echo "[2/3] Publishing for win-x64 (self-contained)..."
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
  --verbosity minimal

echo "[3/3] Renaming to .scr..."
if [ -f "$PUBLISH_DIR/AquariumSaver.exe" ]; then
  mv "$PUBLISH_DIR/AquariumSaver.exe" "$PUBLISH_DIR/AquariumSaver.scr"
  echo "  Created: $PUBLISH_DIR/AquariumSaver.scr"
else
  echo "  ERROR: AquariumSaver.exe not found."
  exit 1
fi

echo "=== Build complete ==="
echo "Screensaver: $PUBLISH_DIR/AquariumSaver.scr"
ls -lh "$PUBLISH_DIR/AquariumSaver.scr"
