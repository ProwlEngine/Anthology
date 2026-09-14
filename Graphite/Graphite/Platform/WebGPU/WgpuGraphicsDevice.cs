using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// A <see cref="GraphicsDevice"/> backed by the browser's WebGPU implementation.
/// </summary>
/// <remarks>
/// Two things separate this from the Vulkan device. Creation is asynchronous, because requesting an
/// adapter and a device are both promises, so the device is built by
/// <see cref="WebGpuDevice.CreateAsync"/> rather than a constructor. And nothing can block: the
/// browser thread must not, and WebGPU offers no fence to block on, so every wait here either
/// resolves immediately or reports what has already completed.
/// </remarks>
internal sealed partial class WgpuGraphicsDevice : GraphicsDevice
{
    private readonly int _deviceHandle;
    private readonly WgpuResourceFactory _factory;
    private readonly WgpuSwapchain? _mainSwapchain;
    private readonly GraphicsDeviceFeatures _features;
    private readonly WgpuLimits _limits;

    private readonly string _deviceName;
    private readonly string _vendorName;

    // Handles whose owner has been disposed but whose GPU work may still be in flight. WebGPU keeps
    // a submitted resource alive until its work retires, so these could be released immediately;
    // deferring one execution keeps the ownership story the same as the Vulkan backend's.
    private readonly List<(ulong ExecutionId, int Handle)> _pendingReleases = [];

    // Device-wide, so a bind group survives the command buffer that first built it. Per-buffer it
    // was rebuilt every frame, which is the opposite of what caching it was for.
    private readonly WgpuBindGroupCache _bindGroups;


    internal WgpuGraphicsDevice(
        GraphicsDeviceOptions options,
        SwapchainDescription? swapchainDescription,
        int deviceHandle,
        WgpuLimits limits,
        string vendor,
        string architecture)
    {
        _deviceHandle = deviceHandle;
        _limits = limits;
        _vendorName = string.IsNullOrEmpty(vendor) ? "Unknown" : vendor;
        _deviceName = string.IsNullOrEmpty(architecture) ? "WebGPU Device" : $"WebGPU ({architecture})";

        _features = new GraphicsDeviceFeatures(
            computeShader: true,
            geometryShader: false,          // WebGPU has neither geometry nor tessellation stages.
            tessellationShaders: false,
            multipleViewports: false,       // One viewport per render pass.
            samplerLodBias: false,          // No LOD bias on a WebGPU sampler.
            drawBaseVertex: true,
            drawBaseInstance: true,
            drawIndirect: true,
            drawIndirectBaseInstance: false, // Needs the indirect-first-instance feature.
            samplerAnisotropy: true,
            depthClipDisable: false,        // Needs the depth-clip-control feature.
            texture1D: true,
            independentBlend: true,
            structuredBuffer: true,
            subsetTextureView: true,
            commandBufferDebugMarkers: true,
            bufferRangeBinding: true,
            shaderFloat64: false);          // WGSL has no f64.

        InitializeFrameOptions(options);

        _bindGroups = new WgpuBindGroupCache(this);
        _factory = new WgpuResourceFactory(this, _features);

        if (swapchainDescription.HasValue)
            _mainSwapchain = new WgpuSwapchain(this, swapchainDescription.Value);

        PostDeviceCreated();
    }


    /// <summary>The shim handle for the underlying GPUDevice.</summary>
    public int DeviceHandle => _deviceHandle;

    /// <summary>Device limits, read once at creation.</summary>
    public WgpuLimits Limits => _limits;

    /// <summary>The shared bind group cache every command buffer binds through.</summary>
    public WgpuBindGroupCache BindGroups => _bindGroups;

    /// <inheritdoc/>
    public override string DeviceName => _deviceName;

    /// <inheritdoc/>
    public override string VendorName => _vendorName;

    /// <inheritdoc/>
    public override GraphicsApiVersion ApiVersion => new(1, 0, 0, 0);

    /// <inheritdoc/>
    public override GraphicsBackend BackendType => GraphicsBackend.WebGPU;

    /// <inheritdoc/>
    public override bool IsUvOriginTopLeft => true;

    /// <inheritdoc/>
    public override bool IsDepthRangeZeroToOne => true;

    /// <inheritdoc/>
    public override bool IsClipSpaceYInverted => false;

    /// <inheritdoc/>
    public override ResourceFactory ResourceFactory => _factory;

    /// <inheritdoc/>
    public override Swapchain MainSwapchain => _mainSwapchain!;

    /// <inheritdoc/>
    public override GraphicsDeviceFeatures Features => _features;

    internal override uint GetUniformBufferMinOffsetAlignmentCore() => _limits.MinUniformBufferOffsetAlignment;

    internal override uint GetStructuredBufferMinOffsetAlignmentCore() => _limits.MinStorageBufferOffsetAlignment;


    /// <summary>
    /// Drains whatever validation errors the device has reported and routes them to the warning
    /// handler. WebGPU surfaces these asynchronously, so they arrive after the call that caused them.
    /// </summary>
    internal void PumpErrors()
    {
        while (true)
        {
            string message = WgpuInterop.TakeError();
            if (message.Length == 0)
                return;

            OnWarning?.Invoke("WebGPU: " + message);
        }
    }


    /// <summary>
    /// Releases a shim handle once the work in flight when it was disposed has retired.
    /// </summary>
    internal void ReleaseWhenRetired(int handle)
    {
        if (handle == WgpuInterop.NullHandle)
            return;

        _pendingReleases.Add((_executionIdCounter, handle));

        // The shim reuses handle slots, so a released handle can come back attached to a different
        // resource. Any cached bind group naming it would then bind the wrong thing rather than
        // fail, so the cache is dropped. Releases are rare; draws are not.
        _bindGroups.Clear();
    }


    private void FlushPendingReleases()
    {
        ulong completed = LastCompletedExecutionId;

        for (int i = _pendingReleases.Count - 1; i >= 0; i--)
        {
            if (_pendingReleases[i].ExecutionId > completed)
                continue;

            WgpuInterop.Release(_pendingReleases[i].Handle);
            _pendingReleases.RemoveAt(i);
        }
    }


    /// <inheritdoc/>
    public override bool WaitForFence(Fence fence, ulong nanosecondTimeout = ulong.MaxValue)
    {
        // Nothing here can block. Report whether the queue has already told us it finished.
        PumpErrors();
        return fence.Signaled;
    }


    /// <inheritdoc/>
    public override void ResetFence(Fence fence) => fence.Reset();


    private protected override void SwapBuffersCore(Swapchain swapchain)
    {
        // WebGPU has no present call. The canvas shows whatever was drawn into the texture obtained
        // for this frame as soon as the browser composites, and the next frame asks for a new one.
        ((WgpuSwapchain)swapchain).EndFrame();
        PumpErrors();
    }


    /// <inheritdoc/>
    public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
    {
        // WebGPU permits exactly one and four samples, with no query to distinguish per format.
        return TextureSampleCount.Count4;
    }


    private protected override bool GetPixelFormatSupportCore(
        PixelFormat format,
        TextureType type,
        TextureUsage usage,
        out PixelFormatProperties properties)
    {
        properties = default;

        // The format table is the authority: anything it can name, WebGPU can express.
        try
        {
            _ = WgpuFormats.Texture(format);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        uint maxDimension = type == TextureType.Texture3D
            ? _limits.MaxTextureDimension3D
            : _limits.MaxTextureDimension2D;

        // The sample-count field is a bitmask indexed by TextureSampleCount, and WebGPU allows
        // exactly one and four samples.
        uint sampleCounts = (1u << (int)TextureSampleCount.Count1) | (1u << (int)TextureSampleCount.Count4);

        properties = new PixelFormatProperties(
            maxWidth: maxDimension,
            maxHeight: type == TextureType.Texture1D ? 1 : maxDimension,
            maxDepth: type == TextureType.Texture3D ? _limits.MaxTextureDimension3D : 1,
            maxMipLevels: 32,
            maxArrayLayers: _limits.MaxTextureArrayLayers,
            sampleCounts: sampleCounts);

        return true;
    }


    protected override MappedResource MapCore(MappableResource resource, MapMode mode, uint subresource)
    {
        // WebGPU maps asynchronously: mapAsync returns a promise, and the browser thread cannot wait
        // on it. There is no honest synchronous answer to give here.
        throw new NotSupportedException(
            "WebGPU cannot map a resource synchronously; mapping is promise-based and the browser thread cannot block. " +
            "Upload with UpdateBuffer or UpdateTexture, which go through the queue instead.");
    }


    protected override void UnmapCore(MappableResource resource, uint subresource)
        => throw new NotSupportedException("WebGPU resources are never synchronously mapped, so there is nothing to unmap.");


    protected override void PlatformDispose()
    {
        _mainSwapchain?.Dispose();
        _bindGroups.Clear();
        DisposeArenas();

        foreach ((_, int handle) in _pendingReleases)
            WgpuInterop.Release(handle);

        _pendingReleases.Clear();
        WgpuInterop.Release(_deviceHandle);
    }
}


/// <summary>
/// The device limits the backend actually consults, read once from the adapter.
/// </summary>
internal readonly struct WgpuLimits
{
    public uint MaxTextureDimension2D { get; init; }
    public uint MaxTextureDimension3D { get; init; }
    public uint MaxTextureArrayLayers { get; init; }
    public uint MaxBindGroups { get; init; }
    public uint MinUniformBufferOffsetAlignment { get; init; }
    public uint MinStorageBufferOffsetAlignment { get; init; }
    public uint MaxVertexBuffers { get; init; }
    public uint MaxColorAttachments { get; init; }


    /// <summary>Reads the limits out of the shim's device info JSON, falling back to spec defaults.</summary>
    public static WgpuLimits FromJson(JsonElement limits)
    {
        uint Get(string name, uint fallback)
            => limits.TryGetProperty(name, out JsonElement v) && v.TryGetUInt32(out uint n) ? n : fallback;

        return new WgpuLimits
        {
            MaxTextureDimension2D = Get("maxTextureDimension2D", 8192),
            MaxTextureDimension3D = Get("maxTextureDimension3D", 2048),
            MaxTextureArrayLayers = Get("maxTextureArrayLayers", 256),
            MaxBindGroups = Get("maxBindGroups", 4),
            MinUniformBufferOffsetAlignment = Get("minUniformBufferOffsetAlignment", 256),
            MinStorageBufferOffsetAlignment = Get("minStorageBufferOffsetAlignment", 256),
            MaxVertexBuffers = Get("maxVertexBuffers", 8),
            MaxColorAttachments = Get("maxColorAttachments", 8)
        };
    }
}
