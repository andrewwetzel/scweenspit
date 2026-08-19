#!/usr/bin/env bash
# Cross-publish Windows binaries from any OS (Linux/macOS included).
# Requires the .NET 8 SDK; EnableWindowsTargeting in the csproj does the rest.
set -euo pipefail
out="${1:-./publish}"
cd "$(dirname "$0")/.."

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$out/win-x64"

echo
echo "built:"
find "$out" -name '*.exe' -printf '  %-52p %8s bytes\n' 2>/dev/null || find "$out" -name '*.exe'
