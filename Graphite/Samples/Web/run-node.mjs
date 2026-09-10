// Runs a browser sample under Node against a mock WebGPU, driving a fixed number of frames.
//
// Everything except the GPU is real: the .NET runtime, the interop marshalling, the shim, the
// backend, the render graph, and the ahead-of-time shaders. That makes this the cheapest way to find
// out whether a frame actually records, without a browser or a display.
//
//   dotnet build Samples/Web/HelloTriangle/HelloTriangle.Web.csproj
//   node Samples/Web/run-node.mjs HelloTriangle [frames]
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, extname, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const sample = process.argv[2] ?? "HelloTriangle";
const frameBudget = Number(process.argv[3] ?? 3);

const bundle = join(here, sample, "bin", "Debug", "net10.0-browser", "browser-wasm", "AppBundle");

if (!existsSync(join(bundle, "_framework", "dotnet.js"))) {
    console.error(`No AppBundle for '${sample}' at ${bundle}\nBuild its csproj first.`);
    process.exit(2);
}

// -- asset server -------------------------------------------------------------

const MIME = { ".js": "text/javascript", ".json": "application/json", ".wasm": "application/wasm",
               ".html": "text/html", ".wgsl": "text/plain", ".png": "image/png", ".jpg": "image/jpeg" };

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

// -- mock WebGPU --------------------------------------------------------------

let framesRun = 0;
const counts = new Map();
const count = (name) => {
    counts.set(name, (counts.get(name) ?? 0) + 1);
    if (process.env.TRACE) console.log(`  [frame ${framesRun}] ${name}`);
};
const problems = [];
const obj = (kind, extra = {}) => ({ __kind: kind, ...extra });

// The mock is deliberately picky about the things the backend is most likely to get wrong, so a
// silent mistake becomes a reported one.
function checkRenderPass(desc) {
    for (const attachment of desc.colorAttachments ?? []) {
        if (!attachment.view) problems.push("render pass colour attachment has no view");
        if (!["clear", "load"].includes(attachment.loadOp)) problems.push(`bad loadOp ${attachment.loadOp}`);
    }
    const depth = desc.depthStencilAttachment;
    if (depth && !depth.view) problems.push("depth attachment has no view");
}

function checkPipeline(desc) {
    if (!desc.vertex?.module) problems.push("pipeline has no vertex module");
    if (desc.fragment && !(desc.fragment.targets ?? []).length) problems.push("fragment stage with no targets");
    for (const target of desc.fragment?.targets ?? []) {
        if (!target.format) problems.push("colour target has no format");
    }
    if (desc.depthStencil && !desc.depthStencil.format) problems.push("depthStencil with no format");
}

// Mirrors the validation a real implementation does at draw time. The mock used to accept anything,
// which is how a strip pipeline missing its index format reached a real GPU before being noticed.
const pass = () => {
    let bound = null;
    return obj("pass", {
        setPipeline: (p) => { count("setPipeline"); bound = p; },
        setBindGroup: (...a) => count(`setBindGroup(${a.length} args)`),
        setVertexBuffer: () => count("setVertexBuffer"),
        setIndexBuffer: () => count("setIndexBuffer"),
        setViewport: () => count("setViewport"),
        setScissorRect: () => count("setScissorRect"),
        draw: () => count("draw"),
        drawIndexed: () => {
            count("drawIndexed");
            if (!bound) { problems.push("drawIndexed with no pipeline bound"); return; }
            if (bound.strip && !bound.stripIndexFormat) {
                problems.push(`indexed draw on a ${bound.topology} pipeline with no stripIndexFormat`);
            }
        },
        end: () => count("endRenderPass")
    });
};

const mockDevice = {
    limits: { maxBindGroups: 4, maxTextureDimension2D: 8192, maxTextureDimension3D: 2048,
              minUniformBufferOffsetAlignment: 256, minStorageBufferOffsetAlignment: 256 },
    features: new Set(),
    addEventListener() {}, lost: new Promise(() => {}), destroy() {},
    createBuffer: d => { count("createBuffer"); if (d.size % 4) problems.push(`buffer size ${d.size} not 4-aligned`);
                         return obj("buffer", { destroy() {} }); },
    createTexture: d => { count("createTexture"); if (!d.format) problems.push("texture with no format");
                          return obj("texture", { createView: () => obj("view"), destroy() {} }); },
    createSampler: () => { count("createSampler"); return obj("sampler"); },
    createShaderModule: d => { count("createShaderModule");
                               if (!d.code?.includes("@vertex") && !d.code?.includes("@fragment"))
                                   problems.push("shader module with no entry point attribute");
                               return obj("module"); },
    createBindGroupLayout: () => { count("createBindGroupLayout"); return obj("bgl"); },
    createPipelineLayout: () => { count("createPipelineLayout"); return obj("pl"); },
    createBindGroup: () => { count("createBindGroup"); return obj("bg"); },
    createRenderPipeline: d => {
        count("createRenderPipeline");
        checkPipeline(d);
        const topology = d.primitive?.topology ?? "triangle-list";
        return obj("pipeline", {
            topology,
            strip: topology.endsWith("-strip"),
            stripIndexFormat: d.primitive?.stripIndexFormat ?? null
        });
    },
    createCommandEncoder: () => obj("encoder", {
        beginRenderPass: d => { count("beginRenderPass"); checkRenderPass(d); return pass(); },
        copyBufferToBuffer: () => count("copyBufferToBuffer"),
        finish: () => obj("cmdbuf")
    }),
    queue: {
        writeBuffer: (_b, offset) => { count("writeBuffer"); if (offset % 4) problems.push(`writeBuffer offset ${offset} not 4-aligned`); },
        writeTexture: (destination, data, layout, size) => {
            count("writeTexture");
            const [width, height] = size;
            if (!destination?.texture) problems.push("writeTexture with no destination texture");
            if (layout.bytesPerRow < width * 4) {
                problems.push(`writeTexture bytesPerRow ${layout.bytesPerRow} too small for width ${width}`);
            }
            if (data.length < layout.bytesPerRow * height) {
                problems.push(`writeTexture data ${data.length} bytes short of ${layout.bytesPerRow * height}`);
            }
        },
        submit: () => count("submit"),
        // Deliberately slower than a frame: a queue that answers instantly hides the ring starving.
        onSubmittedWorkDone: () => new Promise(r => setTimeout(r, 50))
    }
};

Object.defineProperty(globalThis, "navigator", { configurable: true, writable: true, value: {
    gpu: {
        requestAdapter: async () => ({ info: { vendor: "node-mock", architecture: "mock" },
                                       requestDevice: async () => mockDevice }),
        getPreferredCanvasFormat: () => "bgra8unorm"
    }
}});

let currentTextureCalls = 0;
const canvas = {
    width: 600, height: 600, clientWidth: 600, clientHeight: 600,
    getContext: () => ({
        configure: () => count("configure"),
        getCurrentTexture: () => { currentTextureCalls++; return obj("swaptex", { createView: () => obj("swapview") }); }
    })
};

const status = { textContent: "", classList: { add() {} } };

// The host module decodes images through createImageBitmap and an OffscreenCanvas. Node has neither,
// so these stand in with a fixed size and a recognisable pattern; the point is to exercise the
// upload path, not to check the pixels.
const STUB_IMAGE = 4;
globalThis.createImageBitmap = async () => ({ width: STUB_IMAGE, height: STUB_IMAGE, close() {} });
globalThis.OffscreenCanvas = class {
    constructor(width, height) { this.width = width; this.height = height; }
    getContext() {
        return {
            drawImage: () => {},
            getImageData: (_x, _y, w, h) => ({
                data: Uint8ClampedArray.from({ length: w * h * 4 }, (_, i) => i % 256)
            })
        };
    }
};

globalThis.window = globalThis;
globalThis.devicePixelRatio = 1;
globalThis.addEventListener ??= () => {};
globalThis.removeEventListener ??= () => {};
globalThis.document = {
    baseURI: base,
    querySelector: s => (s === "#graphite-canvas" ? canvas : null),
    getElementById: id => (id === "status" ? status : null)
};

// A bounded frame loop: real requestAnimationFrame never stops, and this needs to end.
globalThis.requestAnimationFrame = (fn) => {
    if (framesRun >= frameBudget) return 0;
    framesRun++;
    setTimeout(() => fn(performance.now() + framesRun * 16.7), 0);
    return framesRun;
};

// -- run ----------------------------------------------------------------------

// A frame callback runs from a timer, so anything it throws is unhandled and would take the process
// down before the report prints. Record it and let the run finish reporting instead.
let frameFailure = null;
process.on("uncaughtException", (e) => { frameFailure ??= e?.message ?? String(e); });
process.on("unhandledRejection", (e) => { frameFailure ??= e?.message ?? String(e); });

const { dotnet } = await import(`file://${join(bundle, "_framework", "dotnet.js")}`);
const { runMain } = await dotnet.withDiagnosticTracing(false).create();

let failure = null;
try {
    await runMain();
    // Let the queued frames drain.
    await new Promise(r => setTimeout(r, 400));
} catch (e) {
    failure = e?.message ?? String(e);
}

failure ??= frameFailure;

server.close();

console.log(`\n--- ${sample}: ${framesRun} frame(s) driven ---`);
for (const [name, n] of [...counts].sort()) console.log(`  ${String(n).padStart(4)}x ${name}`);
console.log(`  ${String(currentTextureCalls).padStart(4)}x getCurrentTexture`);

console.log("\n--- status line ---");
console.log("  " + (status.textContent || "(never set)"));

if (problems.length > 0) {
    console.log("\n--- problems seen by the mock ---");
    for (const p of [...new Set(problems)]) console.log("  " + p);
}

const drew = (counts.get("draw") ?? 0) + (counts.get("drawIndexed") ?? 0) > 0;
const ok = !failure && problems.length === 0 && drew && framesRun > 0;

console.log("\n--- result ---");
if (failure) console.log("FAIL  " + failure);
else if (!drew) console.log("FAIL  no draw was recorded");
else if (problems.length) console.log("FAIL  the mock rejected something above");
else console.log(`PASS  ${framesRun} frames recorded a draw with no complaints`);

process.exit(ok ? 0 : 1);
