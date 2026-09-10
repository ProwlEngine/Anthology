#!/usr/bin/env bash
# Builds the harness inputs and serves them, so the shim can be exercised in a real browser.
#
#   ./Tests/WebGPU/Harness/run.sh [port]
#
# Then open the printed URL. A green PASS banner means every shim call succeeded and the triangle
# rendered; the page also leaves window.__harness = { ok, log } for scripted checks.
set -euo pipefail

port="${1:-8731}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
out="$here/.serve"

rm -rf "$out"
mkdir -p "$out"

dotnet run --project "$repo/Tools/ShaderPrecompile" -- \
    --out "$out" "$repo/Samples/HelloTriangle/Shader.slang"

cp "$here/index.html" "$here/harness.js" "$out/"
cp "$here/../../../Graphite/Platform/WebGPU/Interop/graphite-webgpu.js" "$out/"

echo
echo "Serving harness at http://localhost:$port/"
cd "$out" && python3 -m http.server "$port"
