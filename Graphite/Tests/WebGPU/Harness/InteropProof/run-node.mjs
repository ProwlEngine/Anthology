// Runs the InteropProof WebAssembly app under Node against a mock WebGPU.
//
// This is the test for the interop boundary itself. Only the GPU is faked; the .NET runtime, the
// JSImport marshalling, the shim and the ahead-of-time shader loading are all real, which is why it
// catches things a compile cannot -- reflection-free JSON, Span-to-MemoryView byte layout, whether an
// empty Span<int> really omits the dynamic offsets argument.
//
//   dotnet build Tests/WebGPU/Harness/InteropProof/InteropProof.csproj
//   node Tests/WebGPU/Harness/InteropProof/run-node.mjs
//
// For the real thing on a real GPU, serve the same AppBundle and open index.html in a browser.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, extname, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const bundle = process.argv[2]
    ?? join(here, "bin", "Debug", "net10.0-browser", "browser-wasm", "AppBundle");

if (!existsSync(join(bundle, "_framework", "dotnet.js"))) {
    console.error(`No AppBundle at ${bundle}\nBuild InteropProof.csproj first.`);
    process.exit(2);
}

// -- a server, because the app fetches its shaders over HTTP like it does in a browser ------------

const MIME = { ".js": "text/javascript", ".json": "application/json", ".wasm": "application/wasm",
               ".html": "text/html", ".wgsl": "text/plain", ".dat": "application/octet-stream" };

const server = createServer(async (req, res) => {
    try {
        const body = await readFile(join(bundle, decodeURIComponent(req.url.split("?")[0])));
        res.writeHead(200, { "Content-Type": MIME[extname(req.url)] ?? "application/octet-stream" });
        res.end(body);
    } catch {
        res.writeHead(404).end();
    }
});
await new Promise(r => server.listen(0, "127.0.0.1", r));
const base = `http://127.0.0.1:${server.address().port}/`;

// -- mock WebGPU ----------------------------------------------------------------------------------

const seen = [];
const rec = (name, detail) => seen.push([name, detail]);
const obj = (kind, extra = {}) => ({ __kind: kind, ...extra });

const mockDevice = {
    limits: { maxBindGroups: 4 },
    features: new Set(),
    addEventListener() {},
    lost: new Promise(() => {}),
    createBuffer: d => { rec("createBuffer", d); return obj("buffer", { destroy() {} }); },
    createShaderModule: d => { rec("createShaderModule", { bytes: d.code.length, label: d.label }); return obj("module"); },
    createBindGroupLayout: d => { rec("createBindGroupLayout", d); return obj("bgl"); },
    createPipelineLayout: d => { rec("createPipelineLayout", d); return obj("pl"); },
    createBindGroup: d => { rec("createBindGroup", d); return obj("bg"); },
    createRenderPipeline: d => { rec("createRenderPipeline", d); return obj("pipeline"); },
    createCommandEncoder: () => obj("encoder", {
        beginRenderPass: p => { rec("beginRenderPass", p); return obj("pass", {
            setPipeline: () => rec("setPipeline"),
            // The argument count is the assertion: an empty Span<int> must not become an argument.
            setBindGroup: (...a) => rec("setBindGroup", { args: a.length }),
            setVertexBuffer: (slot, _b, offset, size) => rec("setVertexBuffer", { slot, offset, size }),
            draw: (...a) => rec("draw", a),
            end: () => rec("end")
        }); },
        finish: () => obj("cmdbuf")
    }),
    queue: {
        // first4 lets the caller see the actual bytes that crossed the boundary.
        writeBuffer: (_b, offset, data) => rec("writeBuffer", { offset, bytes: data.length, first4: [...data.slice(0, 4)] }),
        submit: () => rec("submit"),
        onSubmittedWorkDone: () => Promise.resolve()
    }
};

Object.defineProperty(globalThis, "navigator", { configurable: true, writable: true, value: {
    gpu: {
        requestAdapter: async () => ({
            info: { vendor: "node-mock", architecture: "mock" },
            requestDevice: async () => mockDevice
        }),
        getPreferredCanvasFormat: () => "bgra8unorm"
    }
}});

const canvas = { width: 0, height: 0, getContext: () => ({
    configure: () => rec("configure"),
    getCurrentTexture: () => obj("swaptex", { createView: () => obj("swapview") })
}) };

globalThis.window = globalThis;
globalThis.document = {
    baseURI: base,
    querySelector: s => (s === "#graphite-canvas" ? canvas : null),
    getElementById: () => ({ set textContent(_) {}, set className(_) {} })
};

// -- run ------------------------------------------------------------------------------------------

// Anything thrown outside the awaited call would otherwise vanish and leave an empty report.
let asyncFailure = null;
process.on("uncaughtException", (e) => { asyncFailure ??= e?.message ?? String(e); });
process.on("unhandledRejection", (e) => { asyncFailure ??= e?.message ?? String(e); });

const { dotnet } = await import(`file://${join(bundle, "_framework", "dotnet.js")}`);
const { runMain } = await dotnet.withDiagnosticTracing(false).create();
await runMain();

server.close();

console.log("\n--- WebGPU calls observed from C# ---");
for (const [name, detail] of seen) {
    console.log("  " + name + (detail === undefined ? "" : " " + JSON.stringify(detail).slice(0, 150)));
}

if (asyncFailure) console.log("\n--- unhandled ---\n  " + asyncFailure);

const proof = globalThis.__proof;
console.log("\n--- result ---");
if (!proof) {
    console.log("no result reported");
    process.exit(1);
}
console.log((proof.ok ? "PASS  " : "FAIL  ") + proof.summary);
process.exit(proof.ok ? 0 : 1);
