# Known defects in the WebGPU backend

The backend compiles and has never executed. This is the list of things believed wrong, written down
while they were fresh rather than discovered later by a confusing frame.

Nothing here is speculative about *what the code does* — each entry names the line and what it will
do. What is uncertain is only whether some of them matter in practice, and that is marked.

Six defects found in the same review are already fixed and are listed at the bottom, because the way
some of them were missed is worth remembering.

---

## Will leak or misbehave over time

### 1. Uniform arenas are never freed

`WgpuGraphicsDevice.Execution.cs:21` holds one arena per ring slot, each owning a growing list of
buffers. `WgpuGraphicsDevice.cs:247` disposes the swapchain and pending releases but never the
arenas, so every buffer they allocated outlives the device.

`WgpuUniformArena.Dispose` exists and nothing calls it.

### 2. Bind groups are cached per command buffer

`WgpuCommandBuffer.cs:51` gives every command buffer its own `WgpuBindGroupCache`. Two problems.

The cache is thrown away with the buffer, so a pooled or per-frame command buffer rebuilds every bind
group it ever used. The point of caching them was to avoid exactly that.

Worse, the cache keys on resource *handles*, and handles are recycled by the shim's free list. A
disposed texture's handle can be reissued to a new texture, and a stale cache entry would then bind
the wrong resource rather than fail. The cache belongs on the device, keyed on something that
survives, and it has to be invalidated when a resource is disposed.

### 3. Orphaning destroys a buffer that may still be in use

`WgpuResources.cs:56` creates the replacement buffer, then calls `WgpuInterop.Release` on the old
handle. The shim's `release` calls `destroy()`, which is immediate.

The comment above it claims the browser defers the free. That is true of dropping a reference and not
of `destroy()`, so the comment and the code disagree and at most one of them is right. The WebGPU
specification does say already-submitted work keeps its own reference, which would make this safe,
but that has not been confirmed against an implementation. The conservative fix is
`ReleaseWhenRetired` rather than `Release`, which is what every other disposal path here uses.

---

## Ordering hazards

### 4. Setting a viewport or scissor opens the render pass

`WgpuCommandBuffer.cs:204` and `:213` both call `EnsurePass()`, because WebGPU has no way to set
either outside a pass. But opening the pass is what consumes the queued clears.

So a caller that sets a viewport before clearing gets a pass opened with no clear, and the clear that
follows then has to close that pass and open another. The frame still renders, but the first pass is
wasted and any drawing in it is discarded.

The Vulkan backend does not have this problem because it can clear inside a pass.

### 5. The swapchain frame is acquired through `MainSwapchain`

`WgpuCommandBuffer.cs:162` reaches for `_device.MainSwapchain` when it sees a swapchain framebuffer,
rather than asking the framebuffer which swapchain it belongs to. With one swapchain, which is every
current sample, this is correct. With two it acquires a frame from the wrong one.

---

## Unverified, lower confidence

### 6. Uniform payloads larger than 128 bytes over-read

`WgpuCommandBuffer.cs:387` copies `field.Size` bytes out of `PropertyEntry.Uniform`, which is a fixed
128-byte inline buffer. Every scalar type Graphite can write fits — a `double4x4` is exactly 128 — so
this is safe for anything `PropertySet` can produce today.

It is listed because the copy trusts a reflected size against a fixed-size source with no bound
check, which is the kind of thing that stops being safe quietly.

### 7. Validation errors surface late

`PumpErrors` runs on submit and on swap. WebGPU reports validation failures asynchronously, so an
error caused by a resource creation call is attributed to whatever frame happened to drain it. Not
wrong, but it will make the first debugging session harder than it needs to be.

---

## Already fixed, and how they were missed

These six came out of the same review and are corrected in the tree.

**The pipeline cache ignored topology.** `GetPipeline` took a topology and then keyed the cache on
`OutputDescription` alone, so a program drawn first as a triangle list and then as a strip got the
list pipeline for both and drew the wrong primitives with no error anywhere. The key is now a
composite of output description and topology. Strip topologies additionally bake in an index format,
which the key still does not carry; that only matters once something draws strips.

**A depth-stencil view was always depth-only.** The view descriptor wrote `aspect: "depth-only"` for
any format carrying both aspects. That is right for sampling one of them and wrong for the case the
samples actually hit, because a depth-stencil attachment has to cover both and WebGPU rejects a
depth-only view used as one. Views now say whether they are for an attachment.

The remaining four were avoidable.

**`WgpuCommandBuffer.Execution` shadowed the base property.** `CommandBuffer` already has an
`Execution` that `RenderContext` assigns when it rents the buffer. Declaring another one meant the
backend read its own, always-null copy, so every uniform pack would have thrown. The compiler said so
— `warning CS0108` — and it was missed because the build checks filtered output down to errors only.

**`WgpuBuffer.Usage` shadowed `DeviceBuffer.Usage`,** which is a different type entirely: Graphite's
enum versus WebGPU's bit flags. Same `CS0108` warning, missed the same way. Renamed to `WgpuUsage`.

**The swapchain never published its output formats until a resize.** `OutputDescription` was assigned
only inside `Resize`, which early-returns when the size has not changed and which the constructor
never called. Every pipeline built for the first frame would have used an empty output description.

**An indexed draw ignored `indexStart` in its count.** `indexStart` is how many indices to skip, so it
comes off the count as well as being the first index. Drawing the full count from a non-zero start
runs past the end of the buffer.

The two shadowing bugs share a cause worth remembering: build output was being grepped down to
`error` lines, so `CS0108` never appeared. The compiler had already found both.
