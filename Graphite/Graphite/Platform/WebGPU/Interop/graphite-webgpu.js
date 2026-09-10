// JavaScript half of the Graphite WebGPU backend.
//
// Everything the browser owns -- GPUDevice, buffers, textures, pipelines, encoders -- lives in the
// handle table below and is addressed from C# by a plain integer. Passing GPUObject references across
// the interop boundary instead would allocate a GCHandle and a proxy per call, which is far too much
// for something invoked once per draw.
//
// Two calling conventions, split by how often a call happens:
//
//   Cold path (resource and pipeline creation): takes a JSON descriptor string. WebGPU descriptors are
//   deep, optional-heavy objects, and JSON maps onto them directly, so the C# side builds the same
//   shape the WebGPU spec documents instead of a flattened argument list that drifts from it.
//
//   Hot path (per-draw command recording): flat integers only. No JSON, no allocation, no string
//   marshalling. Bulk data arrives as a MemoryView over the caller's Span, which copies nothing.

// ---------------------------------------------------------------------------
// Handle table
// ---------------------------------------------------------------------------

// Index 0 is a permanent null so a zero handle from C# always means "nothing", the same way a null
// pointer does. Released slots go on a free list and get reused.
const handles = [null];
const freeList = [];

function alloc(obj) {
    if (freeList.length > 0) {
        const h = freeList.pop();
        handles[h] = obj;
        return h;
    }
    handles.push(obj);
    return handles.length - 1;
}

function get(h) {
    const obj = handles[h];
    if (obj === undefined || obj === null) {
        throw new Error(`Graphite WebGPU: handle ${h} is not live.`);
    }
    return obj;
}

export function release(h) {
    if (h <= 0 || h >= handles.length || handles[h] == null) {
        return;
    }
    // Buffers and textures hold GPU memory that the garbage collector will not reclaim promptly, so
    // destroy what can be destroyed rather than only dropping the reference.
    const obj = handles[h];
    if (typeof obj.destroy === "function") {
        try { obj.destroy(); } catch { /* already destroyed, or not destroyable */ }
    }
    handles[h] = null;
    freeList.push(h);
}

export function liveHandleCount() {
    let n = 0;
    for (let i = 1; i < handles.length; i++) {
        if (handles[i] != null) n++;
    }
    return n;
}

// ---------------------------------------------------------------------------
// Device state and error plumbing
// ---------------------------------------------------------------------------

let device = null;
let queue = null;
let adapterInfo = null;

// WebGPU reports validation failures asynchronously, so they cannot be thrown back out of the call
// that caused them. They queue up here and C# drains them, which keeps the failure visible without
// pretending the boundary is synchronous.
const errors = [];

function recordError(message) {
    // A broken shader can otherwise produce errors every frame until the tab dies.
    if (errors.length < 64) {
        errors.push(String(message));
    }
}

export function isSupported() {
    return typeof navigator !== "undefined" && navigator.gpu != null;
}

export async function initialize(powerPreference) {
    if (!isSupported()) {
        recordError("WebGPU is not available in this browser (navigator.gpu is undefined).");
        return 0;
    }

    try {
        const adapter = await navigator.gpu.requestAdapter(
            powerPreference ? { powerPreference } : undefined);

        if (adapter == null) {
            recordError("navigator.gpu.requestAdapter() returned no adapter.");
            return 0;
        }

        device = await adapter.requestDevice();
        queue = device.queue;
        adapterInfo = adapter.info ?? null;

        device.addEventListener("uncapturederror", e => recordError(e.error.message));
        device.lost.then(info => recordError(`Device lost (${info.reason}): ${info.message}`));

        return alloc(device);
    } catch (e) {
        recordError(`Device creation failed: ${e.message}`);
        return 0;
    }
}

// Drains one queued error, or returns an empty string when there are none. C# polls this rather than
// being called back, so reporting stays on the caller's thread.
export function takeError() {
    return errors.length > 0 ? errors.shift() : "";
}

export function getDeviceInfo() {
    const info = adapterInfo ?? {};
    return JSON.stringify({
        vendor: info.vendor ?? "",
        architecture: info.architecture ?? "",
        device: info.device ?? "",
        description: info.description ?? "",
        limits: device ? limitsOf(device.limits) : {},
        features: device ? [...device.features] : []
    });
}

function limitsOf(limits) {
    const out = {};
    for (const key in limits) {
        const v = limits[key];
        if (typeof v === "number") out[key] = v;
    }
    return out;
}

// ---------------------------------------------------------------------------
// Canvas and presentation
// ---------------------------------------------------------------------------

export function getPreferredCanvasFormat() {
    return navigator.gpu.getPreferredCanvasFormat();
}

export function configureCanvas(selector, format, alphaMode, width, height) {
    const canvas = document.querySelector(selector);
    if (canvas == null) {
        recordError(`No canvas matched selector '${selector}'.`);
        return 0;
    }

    // The drawing buffer is sized in device pixels; CSS sizing is the page's business, not ours.
    if (width > 0) canvas.width = width;
    if (height > 0) canvas.height = height;

    const context = canvas.getContext("webgpu");
    if (context == null) {
        recordError("canvas.getContext('webgpu') returned null.");
        return 0;
    }

    context.configure({
        device,
        format,
        alphaMode: alphaMode || "opaque"
    });

    return alloc(context);
}

export function resizeCanvas(contextHandle, width, height) {
    const context = get(contextHandle);
    const canvas = context.canvas;
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }
}

// The swapchain texture is valid for exactly one frame and must not be cached across frames, so this
// allocates a fresh handle each call. The caller releases it after submitting.
export function getCurrentTextureView(contextHandle) {
    return alloc(get(contextHandle).getCurrentTexture().createView());
}

// ---------------------------------------------------------------------------
// Resource creation (cold path, JSON descriptors)
// ---------------------------------------------------------------------------

export function createBuffer(descJson) {
    return alloc(device.createBuffer(JSON.parse(descJson)));
}

export function createTexture(descJson) {
    return alloc(device.createTexture(JSON.parse(descJson)));
}

export function createTextureView(textureHandle, descJson) {
    const desc = descJson ? JSON.parse(descJson) : undefined;
    return alloc(get(textureHandle).createView(desc));
}

export function createSampler(descJson) {
    return alloc(device.createSampler(JSON.parse(descJson)));
}

export function createShaderModule(code, label) {
    return alloc(device.createShaderModule({ code, label: label || undefined }));
}

export function createBindGroupLayout(descJson) {
    return alloc(device.createBindGroupLayout(JSON.parse(descJson)));
}

export function createPipelineLayout(descJson) {
    // Layout handles arrive as integers inside the JSON; swap them for the real objects.
    const desc = JSON.parse(descJson);
    desc.bindGroupLayouts = desc.bindGroupLayouts.map(get);
    return alloc(device.createPipelineLayout(desc));
}

export function createBindGroup(descJson) {
    const desc = JSON.parse(descJson);
    desc.layout = get(desc.layout);
    desc.entries = desc.entries.map(entry => {
        const out = { binding: entry.binding };
        if (entry.buffer !== undefined) {
            out.resource = { buffer: get(entry.buffer) };
            if (entry.offset !== undefined) out.resource.offset = entry.offset;
            if (entry.size !== undefined) out.resource.size = entry.size;
        } else {
            // Texture views and samplers bind directly rather than through a wrapper object.
            out.resource = get(entry.resource);
        }
        return out;
    });
    return alloc(device.createBindGroup(desc));
}

export function createRenderPipeline(descJson) {
    const desc = JSON.parse(descJson);
    desc.layout = desc.layout === "auto" ? "auto" : get(desc.layout);
    desc.vertex.module = get(desc.vertex.module);
    if (desc.fragment) desc.fragment.module = get(desc.fragment.module);
    return alloc(device.createRenderPipeline(desc));
}

export function createComputePipeline(descJson) {
    const desc = JSON.parse(descJson);
    desc.layout = desc.layout === "auto" ? "auto" : get(desc.layout);
    desc.compute.module = get(desc.compute.module);
    return alloc(device.createComputePipeline(desc));
}

// ---------------------------------------------------------------------------
// Uploads
// ---------------------------------------------------------------------------

// `data` is a MemoryView over the caller's Span, valid only for this call. writeBuffer copies out of
// it synchronously, so nothing is retained past the return.
export function writeBuffer(bufferHandle, bufferOffset, data) {
    queue.writeBuffer(get(bufferHandle), bufferOffset, data.slice());
}

export function writeTexture(textureHandle, destJson, layoutJson, width, height, depth, data) {
    const dest = JSON.parse(destJson);
    dest.texture = get(textureHandle);
    queue.writeTexture(dest, data.slice(), JSON.parse(layoutJson), [width, height, depth]);
}

// ---------------------------------------------------------------------------
// Command recording (hot path, flat arguments)
// ---------------------------------------------------------------------------

export function createCommandEncoder(label) {
    return alloc(device.createCommandEncoder({ label: label || undefined }));
}

export function beginRenderPass(encoderHandle, descJson) {
    const desc = JSON.parse(descJson);
    desc.colorAttachments = desc.colorAttachments.map(a => {
        const out = { view: get(a.view), loadOp: a.loadOp, storeOp: a.storeOp };
        if (a.clearValue !== undefined) out.clearValue = a.clearValue;
        if (a.resolveTarget !== undefined) out.resolveTarget = get(a.resolveTarget);
        return out;
    });
    if (desc.depthStencilAttachment) {
        desc.depthStencilAttachment.view = get(desc.depthStencilAttachment.view);
    }
    return alloc(get(encoderHandle).beginRenderPass(desc));
}

export function setPipeline(passHandle, pipelineHandle) {
    get(passHandle).setPipeline(get(pipelineHandle));
}

// `dynamicOffsets` is a MemoryView over a Span<int>; an empty span means the bind group declares no
// dynamic offsets. The slice is required because setBindGroup keeps no reference either way.
export function setBindGroup(passHandle, index, bindGroupHandle, dynamicOffsets) {
    const pass = get(passHandle);
    const group = get(bindGroupHandle);
    if (dynamicOffsets && dynamicOffsets.byteLength > 0) {
        pass.setBindGroup(index, group, dynamicOffsets.slice());
    } else {
        pass.setBindGroup(index, group);
    }
}

export function setVertexBuffer(passHandle, slot, bufferHandle, offset, size) {
    get(passHandle).setVertexBuffer(slot, get(bufferHandle), offset, size > 0 ? size : undefined);
}

export function setIndexBuffer(passHandle, bufferHandle, format, offset, size) {
    get(passHandle).setIndexBuffer(get(bufferHandle), format, offset, size > 0 ? size : undefined);
}

export function setViewport(passHandle, x, y, width, height, minDepth, maxDepth) {
    get(passHandle).setViewport(x, y, width, height, minDepth, maxDepth);
}

export function setScissorRect(passHandle, x, y, width, height) {
    get(passHandle).setScissorRect(x, y, width, height);
}

export function draw(passHandle, vertexCount, instanceCount, firstVertex, firstInstance) {
    get(passHandle).draw(vertexCount, instanceCount, firstVertex, firstInstance);
}

export function drawIndexed(passHandle, indexCount, instanceCount, firstIndex, baseVertex, firstInstance) {
    get(passHandle).drawIndexed(indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
}

export function endRenderPass(passHandle) {
    get(passHandle).end();
    release(passHandle);
}

export function copyBufferToBuffer(encoderHandle, srcHandle, srcOffset, dstHandle, dstOffset, size) {
    get(encoderHandle).copyBufferToBuffer(get(srcHandle), srcOffset, get(dstHandle), dstOffset, size);
}

// ---------------------------------------------------------------------------
// Submission
// ---------------------------------------------------------------------------

// Finishing and submitting in one call keeps the command buffer from ever needing a handle: nothing
// else can be done with it, and it is consumed immediately.
export function submit(encoderHandle) {
    const encoder = get(encoderHandle);
    queue.submit([encoder.finish()]);
    release(encoderHandle);
}

// WebGPU has no fence to block on, and the browser main thread cannot block regardless. Completion is
// a promise, so C# awaits it or polls the flag it sets rather than waiting.
export function onSubmittedWorkDone() {
    return queue.onSubmittedWorkDone();
}
