#!/usr/bin/env bash
# Builds the interop proof app and serves it, so the C# triangle can be seen on a real GPU.
#
#   ./Tests/WebGPU/Harness/InteropProof/serve.sh [port]
#
# Then open the printed URL in a browser with WebGPU.
set -euo pipefail

port="${1:-8080}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$here/InteropProof.csproj" --nologo

bundle="$here/bin/Debug/net10.0-browser/browser-wasm/AppBundle"
if [ ! -f "$bundle/_framework/dotnet.js" ]; then
    echo "No AppBundle at $bundle" >&2
    exit 1
fi

echo
echo "Interop proof at http://localhost:$port/"
cd "$bundle" && python3 -m http.server "$port"
