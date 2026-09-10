// Runs graphite-webgpu.js against a mock WebGPU under plain Node: no browser, no GPU, no .NET.
//
// This covers the parts of the shim that are pure bookkeeping and therefore worth testing without a
// device: the handle table, what release destroys and reuses, and the handle-to-object substitution
// inside every JSON descriptor. Anything needing a real adapter belongs in the browser harness.
//
//   node Tests/WebGPU/Harness/shim.test.mjs
const calls = [];
const rec = (name, args) => calls.push([name, args]);
const obj = (kind, extra = {}) => ({ __kind: kind, ...extra });

const mockDevice = {
  limits: { maxBindGroups: 4, maxTextureDimension2D: 8192 },
  features: new Set(["depth-clip-control"]),
  addEventListener() {}, lost: new Promise(() => {}), destroy() { rec("device.destroy"); },
  createBuffer: d => { rec("createBuffer", d); return obj("buffer", { destroy() { rec("buffer.destroy"); } }); },
  createTexture: d => { rec("createTexture", d); return obj("texture", { createView: v => obj("view", v), destroy() {} }); },
  createSampler: d => { rec("createSampler", d); return obj("sampler"); },
  createShaderModule: d => { rec("createShaderModule", d); return obj("module"); },
  createBindGroupLayout: d => { rec("createBindGroupLayout", d); return obj("bgl"); },
  createPipelineLayout: d => { rec("createPipelineLayout", d); return obj("pl"); },
  createBindGroup: d => { rec("createBindGroup", d); return obj("bg"); },
  createRenderPipeline: d => { rec("createRenderPipeline", d); return obj("pipeline"); },
  createCommandEncoder: d => { rec("createCommandEncoder", d); return obj("encoder", {
      beginRenderPass: p => { rec("beginRenderPass", p); return obj("pass", {
          setPipeline: x => rec("setPipeline", x.__kind),
          setBindGroup: (...a) => rec("setBindGroup", a.map(v => v && v.__kind || v)),
          setVertexBuffer: (...a) => rec("setVertexBuffer", a.map(v => v && v.__kind || v)),
          draw: (...a) => rec("draw", a),
          end: () => rec("pass.end")
      }); },
      finish: () => obj("cmdbuf")
  }); },
  queue: {
    writeBuffer: (b, o, d) => rec("writeBuffer", [b.__kind, o, d.length]),
    submit: l => rec("submit", l.map(x => x.__kind)),
    onSubmittedWorkDone: () => Promise.resolve()
  }
};

Object.defineProperty(globalThis, "navigator", { configurable: true, writable: true, value: {
  gpu: {
    requestAdapter: async () => ({ info: { vendor: "mock", architecture: "test" },
                                   requestDevice: async () => mockDevice }),
    getPreferredCanvasFormat: () => "bgra8unorm"
  }
}});
const canvas = { width: 0, height: 0, getContext: () => ({ configure: c => rec("configure", { format: c.format, alphaMode: c.alphaMode }),
                                                           getCurrentTexture: () => obj("swaptex", { createView: () => obj("swapview") }),
                                                           canvas: null }) };
globalThis.document = { querySelector: s => (s === "#graphite-canvas" ? canvas : null) };

const shimPath = process.argv[2]
  ?? new URL("../../../Graphite/Platform/WebGPU/Interop/graphite-webgpu.js", import.meta.url).pathname;
const w = await import("file://" + shimPath);

let failures = 0;
const check = (name, cond, detail = "") => {
  if (cond) console.log(`  ok    ${name}`);
  else { console.log(`  FAIL  ${name} ${detail}`); failures++; }
};

check("isSupported", w.isSupported() === true);

const dev = await w.initialize("");
check("initialize returns a handle", dev > 0, `got ${dev}`);
check("no error queued", w.takeError() === "");

const info = JSON.parse(w.getDeviceInfo());
check("device info carries vendor + limits", info.vendor === "mock" && info.limits.maxBindGroups === 4);

const ctx = w.configureCanvas("#graphite-canvas", "bgra8unorm", "opaque", 400, 400);
check("configureCanvas returns a handle", ctx > 0);
check("canvas sized in device pixels", canvas.width === 400 && canvas.height === 400);

const missing = w.configureCanvas("#nope", "bgra8unorm", "opaque", 1, 1);
check("bad selector returns 0", missing === 0);
check("bad selector queues an error", w.takeError().includes("No canvas matched"));

const buf = w.createBuffer(JSON.stringify({ size: 64, usage: 40 }));
w.writeBuffer(buf, 0, new Uint8Array(64));
check("writeBuffer reached the queue", calls.some(([n, a]) => n === "writeBuffer" && a[2] === 64));

const bgl = w.createBindGroupLayout(JSON.stringify({ entries: [{ binding: 0, visibility: 3, buffer: { type: "uniform" } }] }));
const bg = w.createBindGroup(JSON.stringify({ layout: bgl, entries: [{ binding: 0, buffer: buf, offset: 0, size: 64 }] }));
const bgDesc = calls.find(([n]) => n === "createBindGroup")[1];
check("bind group layout handle resolved to an object", bgDesc.layout.__kind === "bgl");
check("buffer entry wrapped as a resource", bgDesc.entries[0].resource.buffer.__kind === "buffer");

const tex = w.createTexture(JSON.stringify({ size: [4, 4], format: "rgba8unorm", usage: 4 }));
const texView = w.createTextureView(tex, "");
const samp = w.createSampler(JSON.stringify({ magFilter: "linear" }));
const bg2 = w.createBindGroup(JSON.stringify({ layout: bgl, entries: [{ binding: 1, resource: texView }, { binding: 2, resource: samp }] }));
const bg2Desc = calls.filter(([n]) => n === "createBindGroup")[1][1];
check("texture view binds directly", bg2Desc.entries[0].resource.__kind === "view");
check("sampler binds directly", bg2Desc.entries[1].resource.__kind === "sampler");

const pl = w.createPipelineLayout(JSON.stringify({ bindGroupLayouts: [bgl] }));
check("pipeline layout resolved its layouts", calls.find(([n]) => n === "createPipelineLayout")[1].bindGroupLayouts[0].__kind === "bgl");

const vs = w.createShaderModule("@vertex fn vertex() {}", "vs");
const pipe = w.createRenderPipeline(JSON.stringify({ layout: pl, vertex: { module: vs, entryPoint: "vertex", buffers: [] }, primitive: { topology: "triangle-list" } }));
check("pipeline resolved layout and module", (() => { const d = calls.find(([n]) => n === "createRenderPipeline")[1]; return d.layout.__kind === "pl" && d.vertex.module.__kind === "module"; })());

// A frame, including the ownership rules endRenderPass and submit are meant to enforce.
const before = w.liveHandleCount();
const enc = w.createCommandEncoder("frame");
const view = w.getCurrentTextureView(ctx);
const pass = w.beginRenderPass(enc, JSON.stringify({ colorAttachments: [{ view, loadOp: "clear", storeOp: "store", clearValue: { r: 0, g: 0, b: 0, a: 1 } }] }));
check("render pass resolved its attachment view", calls.find(([n]) => n === "beginRenderPass")[1].colorAttachments[0].view.__kind === "swapview");

w.setPipeline(pass, pipe);
w.setBindGroup(pass, 0, bg, new Int32Array(0));
check("empty dynamic offsets omitted", calls.find(([n]) => n === "setBindGroup")[1].length === 2);
w.setBindGroup(pass, 0, bg, new Int32Array([256, 512]));
check("non-empty dynamic offsets forwarded", calls.filter(([n]) => n === "setBindGroup")[1][1].length === 3);
w.setVertexBuffer(pass, 0, buf, 0, 0);
check("size 0 becomes undefined", calls.find(([n]) => n === "setVertexBuffer")[1][3] === undefined);
w.draw(pass, 3, 1, 0, 0);
w.endRenderPass(pass);
w.submit(enc);
await w.onSubmittedWorkDone();

check("pass.end was called", calls.some(([n]) => n === "pass.end"));
check("submit reached the queue", calls.some(([n]) => n === "submit"));
w.release(view);
check("frame handles fully reclaimed", w.liveHandleCount() === before, `before ${before}, after ${w.liveHandleCount()}`);

// Handle reuse and liveness.
const a = w.createBuffer(JSON.stringify({ size: 4, usage: 40 }));
w.release(a);
check("release destroys destroyable objects", calls.some(([n]) => n === "buffer.destroy"));
const b = w.createBuffer(JSON.stringify({ size: 4, usage: 40 }));
check("released slot is reused", a === b, `${a} vs ${b}`);
w.release(9999);
check("releasing an unknown handle is a no-op", true);
let threw = false;
try { w.setPipeline(99999, pipe); } catch { threw = true; }
check("using a dead handle throws", threw);

console.log(failures === 0 ? "\nALL PASS" : `\n${failures} FAILURE(S)`);
process.exit(failures === 0 ? 0 : 1);
