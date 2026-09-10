using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// The C# face of graphite-webgpu.js. Every browser object is addressed by an integer handle from
/// the shim's table; 0 always means "nothing", like a null pointer.
/// </summary>
/// <remarks>
/// Resource and pipeline creation passes a JSON descriptor, matching the shape the WebGPU spec
/// documents. Per-draw command recording passes flat integers, and bulk data passes as a
/// <see cref="Span{T}"/> that marshals to a MemoryView over the same memory without copying.
/// Nothing here allocates a JSObject proxy, which would cost a GCHandle on every call.
/// </remarks>
internal static partial class WgpuInterop
{
    /// <summary>Module name the shim is registered under; must match the JSHost import.</summary>
    public const string ModuleName = "graphite-webgpu";

    /// <summary>A handle that refers to nothing.</summary>
    public const int NullHandle = 0;

    private static bool s_loaded;


    /// <summary>
    /// Loads the shim module. Must complete before any other member here is called.
    /// </summary>
    /// <param name="modulePath">URL the shim is served from.</param>
    public static async Task LoadAsync(string modulePath = "./graphite-webgpu.js")
    {
        if (s_loaded)
            return;

        await JSHost.ImportAsync(ModuleName, modulePath);
        s_loaded = true;
    }


    // -- lifetime -----------------------------------------------------------

    /// <summary>Releases a handle, destroying the underlying object where WebGPU allows it.</summary>
    [JSImport("release", ModuleName)]
    internal static partial void Release(int handle);

    /// <summary>Live handle count, for leak checks in tests and the validation layer.</summary>
    [JSImport("liveHandleCount", ModuleName)]
    internal static partial int LiveHandleCount();


    // -- device -------------------------------------------------------------

    /// <summary>Whether the browser exposes WebGPU at all.</summary>
    [JSImport("isSupported", ModuleName)]
    internal static partial bool IsSupported();

    /// <summary>
    /// Requests an adapter and device. Returns the device handle, or 0 on failure with the reason
    /// available from <see cref="TakeError"/>.
    /// </summary>
    /// <param name="powerPreference">"low-power", "high-performance", or empty for no preference.</param>
    [JSImport("initialize", ModuleName)]
    internal static partial Task<int> InitializeAsync(string powerPreference);

    /// <summary>
    /// Takes the next queued error message, or an empty string when there are none. WebGPU reports
    /// validation failures asynchronously, so they cannot surface from the call that caused them.
    /// </summary>
    [JSImport("takeError", ModuleName)]
    internal static partial string TakeError();

    /// <summary>Adapter and device information as JSON: vendor, architecture, limits, features.</summary>
    [JSImport("getDeviceInfo", ModuleName)]
    internal static partial string GetDeviceInfo();


    // -- presentation -------------------------------------------------------

    /// <summary>The format a canvas should be configured with on this device.</summary>
    [JSImport("getPreferredCanvasFormat", ModuleName)]
    internal static partial string GetPreferredCanvasFormat();

    /// <summary>Configures a canvas for WebGPU and returns its context handle, or 0 on failure.</summary>
    [JSImport("configureCanvas", ModuleName)]
    internal static partial int ConfigureCanvas(string selector, string format, string alphaMode, int width, int height);

    /// <summary>Resizes the canvas drawing buffer. No-op when the size already matches.</summary>
    [JSImport("resizeCanvas", ModuleName)]
    internal static partial void ResizeCanvas(int contextHandle, int width, int height);

    /// <summary>
    /// A view of this frame's swapchain texture. Valid for one frame only and never cached, so the
    /// caller releases the returned handle once the frame is submitted.
    /// </summary>
    [JSImport("getCurrentTextureView", ModuleName)]
    internal static partial int GetCurrentTextureView(int contextHandle);


    // -- resource creation --------------------------------------------------

    /// <summary>Creates a buffer from a GPUBufferDescriptor as JSON.</summary>
    [JSImport("createBuffer", ModuleName)]
    internal static partial int CreateBuffer(string descriptorJson);

    /// <summary>Creates a texture from a GPUTextureDescriptor as JSON.</summary>
    [JSImport("createTexture", ModuleName)]
    internal static partial int CreateTexture(string descriptorJson);

    /// <summary>Creates a texture view; pass an empty descriptor for the whole texture.</summary>
    [JSImport("createTextureView", ModuleName)]
    internal static partial int CreateTextureView(int textureHandle, string descriptorJson);

    /// <summary>Creates a sampler from a GPUSamplerDescriptor as JSON.</summary>
    [JSImport("createSampler", ModuleName)]
    internal static partial int CreateSampler(string descriptorJson);

    /// <summary>Creates a shader module from WGSL source.</summary>
    [JSImport("createShaderModule", ModuleName)]
    internal static partial int CreateShaderModule(string wgsl, string label);

    /// <summary>Creates a bind group layout from a GPUBindGroupLayoutDescriptor as JSON.</summary>
    [JSImport("createBindGroupLayout", ModuleName)]
    internal static partial int CreateBindGroupLayout(string descriptorJson);

    /// <summary>Creates a pipeline layout; bind group layouts appear as handles in the JSON.</summary>
    [JSImport("createPipelineLayout", ModuleName)]
    internal static partial int CreatePipelineLayout(string descriptorJson);

    /// <summary>Creates a bind group; the layout and every bound resource appear as handles.</summary>
    [JSImport("createBindGroup", ModuleName)]
    internal static partial int CreateBindGroup(string descriptorJson);

    /// <summary>Creates a render pipeline; layout and shader modules appear as handles.</summary>
    [JSImport("createRenderPipeline", ModuleName)]
    internal static partial int CreateRenderPipeline(string descriptorJson);

    /// <summary>Creates a compute pipeline; layout and shader module appear as handles.</summary>
    [JSImport("createComputePipeline", ModuleName)]
    internal static partial int CreateComputePipeline(string descriptorJson);


    // -- uploads ------------------------------------------------------------

    /// <summary>
    /// Writes into a buffer through the queue. The span marshals as a view over the same memory and
    /// is copied out synchronously, so nothing is retained past the call.
    /// </summary>
    [JSImport("writeBuffer", ModuleName)]
    internal static partial void WriteBuffer(
        int bufferHandle,
        int bufferOffset,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> data);

    /// <summary>Writes into a texture through the queue.</summary>
    [JSImport("writeTexture", ModuleName)]
    internal static partial void WriteTexture(
        int textureHandle,
        string destinationJson,
        string dataLayoutJson,
        int width,
        int height,
        int depth,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> data);


    // -- command recording --------------------------------------------------

    /// <summary>Creates a command encoder.</summary>
    [JSImport("createCommandEncoder", ModuleName)]
    internal static partial int CreateCommandEncoder(string label);

    /// <summary>Begins a render pass from a GPURenderPassDescriptor as JSON.</summary>
    [JSImport("beginRenderPass", ModuleName)]
    internal static partial int BeginRenderPass(int encoderHandle, string descriptorJson);

    /// <summary>Binds a render pipeline.</summary>
    [JSImport("setPipeline", ModuleName)]
    internal static partial void SetPipeline(int passHandle, int pipelineHandle);

    /// <summary>Binds a bind group; pass an empty span when it declares no dynamic offsets.</summary>
    [JSImport("setBindGroup", ModuleName)]
    internal static partial void SetBindGroup(
        int passHandle,
        int index,
        int bindGroupHandle,
        [JSMarshalAs<JSType.MemoryView>] Span<int> dynamicOffsets);

    /// <summary>Binds a vertex buffer; size 0 means the rest of the buffer.</summary>
    [JSImport("setVertexBuffer", ModuleName)]
    internal static partial void SetVertexBuffer(int passHandle, int slot, int bufferHandle, int offset, int size);

    /// <summary>Binds an index buffer; format is "uint16" or "uint32", size 0 means the rest.</summary>
    [JSImport("setIndexBuffer", ModuleName)]
    internal static partial void SetIndexBuffer(int passHandle, int bufferHandle, string format, int offset, int size);

    /// <summary>Sets the viewport rectangle and depth range.</summary>
    [JSImport("setViewport", ModuleName)]
    internal static partial void SetViewport(
        int passHandle, float x, float y, float width, float height, float minDepth, float maxDepth);

    /// <summary>Sets the scissor rectangle.</summary>
    [JSImport("setScissorRect", ModuleName)]
    internal static partial void SetScissorRect(int passHandle, int x, int y, int width, int height);

    /// <summary>Draws non-indexed.</summary>
    [JSImport("draw", ModuleName)]
    internal static partial void Draw(int passHandle, int vertexCount, int instanceCount, int firstVertex, int firstInstance);

    /// <summary>Draws indexed.</summary>
    [JSImport("drawIndexed", ModuleName)]
    internal static partial void DrawIndexed(
        int passHandle, int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance);

    /// <summary>Ends a render pass and releases its handle.</summary>
    [JSImport("endRenderPass", ModuleName)]
    internal static partial void EndRenderPass(int passHandle);

    /// <summary>Records a buffer-to-buffer copy.</summary>
    [JSImport("copyBufferToBuffer", ModuleName)]
    internal static partial void CopyBufferToBuffer(
        int encoderHandle, int sourceHandle, int sourceOffset, int destinationHandle, int destinationOffset, int size);


    // -- submission ---------------------------------------------------------

    /// <summary>Finishes the encoder, submits it, and releases its handle.</summary>
    [JSImport("submit", ModuleName)]
    internal static partial void Submit(int encoderHandle);

    /// <summary>
    /// Completes once the queue has finished the work submitted so far. This is the only completion
    /// signal WebGPU offers; there is no fence to block on, and the browser thread cannot block.
    /// </summary>
    [JSImport("onSubmittedWorkDone", ModuleName)]
    internal static partial Task OnSubmittedWorkDoneAsync();
}
