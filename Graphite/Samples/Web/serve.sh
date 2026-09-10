#!/usr/bin/env bash
# Builds a browser sample and serves it, so it can be run on a real GPU.
#
#   ./Samples/Web/serve.sh [sample] [port]      # default: HelloTriangle on 8080
set -euo pipefail

sample="${1:-HelloTriangle}"
port="${2:-8080}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$here/$sample/$sample.Web.csproj" --nologo

bundle="$here/$sample/bin/Debug/net10.0-browser/browser-wasm/AppBundle"
if [ ! -f "$bundle/_framework/dotnet.js" ]; then
    echo "No AppBundle at $bundle" >&2
    exit 1
fi

echo
echo "$sample at http://localhost:$port/"
cd "$bundle" && python3 -m http.server "$port"
