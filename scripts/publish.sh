#!/usr/bin/env bash
# Cross-publish Windows binaries from any OS (Linux/macOS included).
# Requires the .NET 8 SDK; EnableWindowsTargeting in the csproj does the rest.
set -euo pipefail
out="${1:-./publish}"
cd "$(dirname "$0")/.."

# Framework-dependent: ~250 KB against the shared .NET Desktop Runtime.
dotnet publish ScweenSpit.csproj -c Release -r win-x64 --self-contained false \
    -p:PublishSingleFile=true -o "$out/win-x64"

# ScweenSpit-Setup.exe is Native AOT and cannot be cross-compiled; CI (windows-latest) builds it.
echo "note: the self-installing launcher is built on Windows only - see .github/workflows/build.yml"

# Want the old no-dependencies build instead? It is 63 MB, almost all of it runtime:
#   dotnet publish ScweenSpit.csproj -c Release -r win-x64 --self-contained true \
#       -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$out/self-contained"

echo
echo "built:"
find "$out" -name '*.exe' -printf '  %-52p %8s bytes\n' 2>/dev/null || find "$out" -name '*.exe'
