using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Prowl.Graphite.Wgpu;


/// <summary>Creates WebGPU-backed resources.</summary>
internal sealed class WgpuResourceFactory : ResourceFactory
{
    private readonly WgpuGraphicsDevice _device;


    public WgpuResourceFactory(WgpuGraphicsDevice device, GraphicsDeviceFeatures features)
        : base(device, features)
    {
        _device = device;
    }


    /// <inheritdoc/>
    public override GraphicsBackend BackendType => GraphicsBackend.WebGPU;

    /// <inheritdoc/>
    public override Framebuffer CreateFramebuffer(ref FramebufferDescription description)
        => new WgpuFramebuffer(_device, description);

    /// <inheritdoc/>
    protected override Texture CreateTextureCore(ref TextureDescription description)
        => new WgpuTexture(_device, description);

    /// <inheritdoc/>
    public override Texture CreateTexture(ulong nativeTexture, ref TextureDescription description)
        => new WgpuTexture(_device, (int)nativeTexture, in description);

    /// <inheritdoc/>
    protected override TextureView CreateTextureViewCore(ref TextureViewDescription description)
        => new WgpuTextureView(_device, ref description);

    /// <inheritdoc/>
    protected override DeviceBuffer CreateBufferCore(ref BufferDescription description)
        => new WgpuBuffer(_device, description);

    /// <inheritdoc/>
    protected override Sampler CreateSamplerCore(ref SamplerDescription description)
        => new WgpuSampler(_device, description);

    /// <inheritdoc/>
    protected override GraphicsProgram CreateGraphicsProgramCore(ref ShaderDescription description)
        => new WgpuGraphicsProgram(_device, ref description);

    /// <inheritdoc/>
    protected override ComputeProgram CreateComputeProgramCore(ref ComputeDescription description)
        => throw new NotImplementedException("Compute is not yet implemented in the WebGPU backend.");

    /// <inheritdoc/>
    public override CommandBuffer CreateCommandBuffer(ref CommandBufferDescription description)
        => new WgpuCommandBuffer(_device, Features);

    /// <inheritdoc/>
    public override TransferCommandBuffer CreateTransferCommandBuffer()
        => new WgpuTransferCommandBuffer(_device);

    /// <inheritdoc/>
    public override Fence CreateFence(bool signaled) => new WgpuFence(signaled);

    /// <inheritdoc/>
    public override Swapchain CreateSwapchain(ref SwapchainDescription description)
        => new WgpuSwapchain(_device, description);
}


/// <summary>
/// One-off transfers outside the execution ring.
/// </summary>
/// <remarks>
/// Everything here goes through the queue rather than an encoder, because WebGPU's queue writes are
/// already ordered against submitted work. That leaves nothing to record and nothing to submit, so
/// the begin and end pair exists only to satisfy the shared contract.
/// </remarks>
internal sealed class WgpuTransferCommandBuffer : TransferCommandBuffer
{
    private readonly WgpuGraphicsDevice _device;
    private int _encoder;


    public WgpuTransferCommandBuffer(WgpuGraphicsDevice device) => _device = device;


    /// <inheritdoc/>
    public override GraphicsDevice Device => _device;


    /// <inheritdoc/>
    public override void Begin()
    {
        _encoder = WgpuInterop.CreateCommandEncoder(Name);
        HasEnded = false;
    }


    /// <inheritdoc/>
    public override void End() => HasEnded = true;


    /// <summary>Submits any encoded copies. Queue writes have already landed.</summary>
    public void Submit()
    {
        if (_encoder == WgpuInterop.NullHandle)
            return;

        WgpuInterop.Submit(_encoder);
        _encoder = WgpuInterop.NullHandle;
    }


    private protected override void UpdateBufferCore(
        DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        => _device.UpdateBuffer(buffer, bufferOffsetInBytes, source, sizeInBytes);


    private protected override void UpdateTextureCore(
        Texture texture, IntPtr source, uint sizeInBytes,
        uint x, uint y, uint z, uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer)
        => _device.UpdateTexture(texture, source, sizeInBytes, x, y, z, width, height, depth, mipLevel, arrayLayer);


    private protected override void CopyBufferCore(
        DeviceBuffer source, uint sourceOffset, DeviceBuffer destination, uint destinationOffset, uint sizeInBytes)
    {
        WgpuInterop.CopyBufferToBuffer(
            _encoder,
            ((WgpuBuffer)source).Handle, (int)sourceOffset,
            ((WgpuBuffer)destination).Handle, (int)destinationOffset,
            (int)sizeInBytes);
    }


    private protected override void CopyTextureCore(
        Texture source,
        uint srcX, uint srcY, uint srcZ, uint srcMipLevel, uint srcBaseArrayLayer,
        Texture destination,
        uint dstX, uint dstY, uint dstZ, uint dstMipLevel, uint dstBaseArrayLayer,
        uint width, uint height, uint depth, uint layerCount)
        => throw new NotImplementedException("Texture-to-texture copies are not yet implemented in the WebGPU backend.");


    private protected override void GenerateMipmapsCore(Texture texture)
        => throw new NotImplementedException("Mipmap generation is not yet implemented in the WebGPU backend.");


    private protected override void DisposeCore()
    {
        if (_encoder != WgpuInterop.NullHandle)
        {
            WgpuInterop.Release(_encoder);
            _encoder = WgpuInterop.NullHandle;
        }
    }
}


/// <summary>
/// Entry point for the browser WebGPU backend.
/// </summary>
public static class WebGpuDevice
{
    /// <summary>
    /// Whether this browser exposes WebGPU. The shim must be loaded first.
    /// </summary>
    public static bool IsSupported() => WgpuInterop.IsSupported();


    /// <summary>
    /// Loads the JavaScript shim. Call once before anything else here.
    /// </summary>
    /// <param name="modulePath">
    /// Where the shim is served from, resolved against the .NET runtime module rather than the page.
    /// The default reaches a copy sitting beside index.html.
    /// </param>
    public static Task LoadAsync(string modulePath = "../graphite-webgpu.js")
        => WgpuInterop.LoadAsync(modulePath);


    /// <summary>
    /// Creates a device against the browser's WebGPU implementation.
    /// </summary>
    /// <remarks>
    /// Asynchronous because requesting an adapter and a device are both promises, and the browser
    /// thread cannot wait on either. This is why there is no <c>GraphicsDevice.CreateWebGPU</c> to
    /// match <c>CreateVulkan</c>.
    /// </remarks>
    /// <param name="options">Common device options.</param>
    /// <param name="swapchainDescription">Main swapchain to create, or null for none.</param>
    /// <param name="powerPreference">"low-power", "high-performance", or empty for no preference.</param>
    /// <returns>The new device.</returns>
    public static async Task<GraphicsDevice> CreateAsync(
        GraphicsDeviceOptions options,
        SwapchainDescription? swapchainDescription = null,
        string powerPreference = "")
    {
        await LoadAsync();

        int handle = await WgpuInterop.InitializeAsync(powerPreference);

        if (handle == WgpuInterop.NullHandle)
            throw new RenderException("Could not create a WebGPU device: " + WgpuInterop.TakeError());

        using JsonDocument info = JsonDocument.Parse(WgpuInterop.GetDeviceInfo());
        JsonElement root = info.RootElement;

        WgpuLimits limits = root.TryGetProperty("limits", out JsonElement limitsElement)
            ? WgpuLimits.FromJson(limitsElement)
            : WgpuLimits.FromJson(default);

        string vendor = root.TryGetProperty("vendor", out JsonElement v) ? v.GetString() ?? "" : "";
        string architecture = root.TryGetProperty("architecture", out JsonElement a) ? a.GetString() ?? "" : "";

        return new WgpuGraphicsDevice(options, swapchainDescription, handle, limits, vendor, architecture);
    }
}
