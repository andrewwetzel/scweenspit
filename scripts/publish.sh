#!/usr/bin/env bash
# Cross-publish Windows binaries from any OS (Linux/macOS included).
# Requires the .NET 8 SDK; EnableWindowsTargeting in the csproj does the rest.
set -euo pipefail
out="${1:-./publish}"
cd "$(dirname "$0")/.."

pub() {  # rid, self-contained, dir
  dotnet publish -c Release -r "$1" --self-contained "$2" -p:PublishSingleFile=true -o "$out/$3"
}

pub win-x64   true  win-x64
pub win-arm64 true  win-arm64
pub win-x64   false win-x64-framework-dependent

echo
echo "built:"
find "$out" -name '*.exe' -printf '  %-52p %8s bytes\n' 2>/dev/null || find "$out" -name '*.exe'
