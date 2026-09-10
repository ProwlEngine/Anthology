// Drives graphite-webgpu.js through the same call sequence WgpuInterop makes from C#, using WGSL and
// a manifest produced by Tools/ShaderPrecompile from Samples/HelloTriangle/Shader.slang.
//
// This exercises the JavaScript half of the boundary on its own: the handle table, the JSON
// descriptor shapes, and the WebGPU calls themselves. It does not cover C#-to-JS marshalling, which
// needs the wasm-tools workload and a real .NET WebAssembly app.
import * as wgpu from './graphite-webgpu.js';

const lines = [];
const log = (m) => {
    lines.push(m);
    document.getElementById('log').textContent = lines.join('\n');
    console.log(m);
};

const finish = (ok, note) => {
    window.__harness = { ok, log: lines };
    const banner = document.getElementById('result');
    banner.textContent = ok ? 'PASS  ' + note : 'FAIL  ' + note;
    banner.className = ok ? 'pass' : 'fail';
};

// GPUBufferUsage / GPUShaderStage are enums on the global objects; the C# side has the same values
// as constants, so spelling them out here keeps the two halves comparable.
const USAGE_VERTEX = GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST;
const USAGE_UNIFORM = GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST;
const VISIBILITY_VF = GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT;

const VERTEX_FORMATS = { Float2: 'float32x2', Float3: 'float32x3', Float4: 'float32x4' };

try {
    if (!wgpu.isSupported()) throw new Error('navigator.gpu is undefined; this browser has no WebGPU.');
    log('isSupported: true');

    const manifest = await (await fetch('./Shader.shader.json')).json();
    const stageOf = (name) => manifest.Stages.find(s => s.Stage === name);
    const vertexStage = stageOf('Vertex');
    const fragmentStage = stageOf('Fragment');
    const vsSrc = await (await fetch('./' + vertexStage.File)).text();
    const fsSrc = await (await fetch('./' + fragmentStage.File)).text();
    log(`manifest v${manifest.Version}: entry points '${vertexStage.EntryPoint}' and '${fragmentStage.EntryPoint}'`);

    const device = await wgpu.initialize('');
    if (device === 0) throw new Error('initialize returned 0: ' + wgpu.takeError());
    log('device handle: ' + device);

    const info = JSON.parse(wgpu.getDeviceInfo());
    log(`adapter: ${info.vendor || '?'} ${info.architecture || ''} maxBindGroups=${info.limits.maxBindGroups}`);

    const format = wgpu.getPreferredCanvasFormat();
    const context = wgpu.configureCanvas('#graphite-canvas', format, 'opaque', 400, 400);
    if (context === 0) throw new Error('configureCanvas returned 0: ' + wgpu.takeError());
    log(`canvas context ${context}, format ${format}`);

    const vs = wgpu.createShaderModule(vsSrc, 'HelloTriangle.vertex');
    const fs = wgpu.createShaderModule(fsSrc, 'HelloTriangle.fragment');

    // The shader takes three separate vertex streams, one per manifest layout.
    const streams = [
        new Float32Array([0.0, 0.7, 0.0, -0.7, -0.6, 0.0, 0.7, -0.6, 0.0]),  // POSITION0
        new Float32Array([0.5, 1.0, 0.0, 0.0, 1.0, 0.0]),                     // UV0
        new Float32Array([1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1])                // COLOR0
    ];

    const vertexBuffers = streams.map(arr => {
        const h = wgpu.createBuffer(JSON.stringify({ size: arr.byteLength, usage: USAGE_VERTEX }));
        wgpu.writeBuffer(h, 0, new Uint8Array(arr.buffer));
        return h;
    });
    log('vertex buffers: ' + vertexBuffers.join(', '));

    // One uniform buffer laid out exactly as the manifest describes: a std140 float4x4 then a float4.
    const set = manifest.ResourceLayouts[0];
    const uniformBytes = set.Elements[0].Fields.reduce((n, f) => Math.max(n, (f.Offset ?? 0) + f.Size), 0);
    const uniform = new Float32Array(uniformBytes / 4);
    uniform.set([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1], 0);  // MatrixMVP, identity
    uniform.set([1, 1, 1, 1], 16);                                      // Color, white
    const uniformBuffer = wgpu.createBuffer(JSON.stringify({ size: uniform.byteLength, usage: USAGE_UNIFORM }));
    wgpu.writeBuffer(uniformBuffer, 0, new Uint8Array(uniform.buffer));
    log(`uniform buffer ${uniformBuffer}, ${uniformBytes} bytes`);

    const bindGroupLayout = wgpu.createBindGroupLayout(JSON.stringify({
        entries: set.Elements.map(e => ({
            binding: e.Binding ?? 0,
            visibility: VISIBILITY_VF,
            buffer: { type: 'uniform' }
        }))
    }));
    const bindGroup = wgpu.createBindGroup(JSON.stringify({
        layout: bindGroupLayout,
        entries: [{ binding: set.Elements[0].Binding ?? 0, buffer: uniformBuffer }]
    }));
    const pipelineLayout = wgpu.createPipelineLayout(JSON.stringify({ bindGroupLayouts: [bindGroupLayout] }));
    log(`bind group layout ${bindGroupLayout}, bind group ${bindGroup}, pipeline layout ${pipelineLayout}`);

    const pipeline = wgpu.createRenderPipeline(JSON.stringify({
        layout: pipelineLayout,
        vertex: {
            module: vs,
            entryPoint: vertexStage.EntryPoint,
            buffers: manifest.VertexLayouts.map(layout => ({
                arrayStride: layout.Stride,
                attributes: layout.Elements.map(e => ({
                    // Each stream holds one attribute, and the buffer slot and the WGSL @location
                    // line up for these shaders. A backend cannot assume that in general.
                    shaderLocation: layout.Location ?? 0,
                    offset: e.Offset ?? 0,
                    format: VERTEX_FORMATS[e.Format]
                }))
            }))
        },
        fragment: {
            module: fs,
            entryPoint: fragmentStage.EntryPoint,
            targets: [{ format }]
        },
        primitive: { topology: 'triangle-list' }
    }));
    log('render pipeline: ' + pipeline);

    // One frame, recorded the way the backend's command buffer will record it.
    const encoder = wgpu.createCommandEncoder('frame');
    const view = wgpu.getCurrentTextureView(context);
    const pass = wgpu.beginRenderPass(encoder, JSON.stringify({
        colorAttachments: [{
            view,
            loadOp: 'clear',
            storeOp: 'store',
            clearValue: { r: 0.10, g: 0.12, b: 0.16, a: 1.0 }
        }]
    }));
    wgpu.setPipeline(pass, pipeline);
    wgpu.setBindGroup(pass, 0, bindGroup, new Int32Array(0));
    vertexBuffers.forEach((h, slot) => wgpu.setVertexBuffer(pass, slot, h, 0, 0));
    wgpu.draw(pass, 3, 1, 0, 0);
    wgpu.endRenderPass(pass);
    wgpu.submit(encoder);
    await wgpu.onSubmittedWorkDone();
    log('frame submitted, queue reported completion');

    const error = wgpu.takeError();
    if (error) throw new Error('device reported: ' + error);

    // endRenderPass and submit release the pass and the encoder themselves, so only the frame view
    // is left over. Anything else still live would be a leak in the shim.
    wgpu.release(view);
    log('live handles after frame: ' + wgpu.liveHandleCount());

    finish(true, 'triangle drawn, no device errors');
} catch (e) {
    log('EXCEPTION: ' + (e && e.message));
    finish(false, (e && e.message) || 'unknown failure');
}
