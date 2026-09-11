using System;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// Anything that owns a shim handle. Keeping it in one place lets the command buffer and the bind
/// group cache resolve a bound resource to its handle without knowing which concrete type it is.
/// </summary>
internal interface IWgpuHandle
{
    /// <summary>Handle in the shim's table, or 0 once disposed.</summary>
    int Handle { get; }
}


/// <summary>A GPU buffer.</summary>
internal sealed class WgpuBuffer : DeviceBuffer, IWgpuHandle
{
    private readonly WgpuGraphicsDevice _device;

    public int Handle { get; private set; }

    /// <summary>
    /// WebGPU usage bits this buffer was created with, needed to recreate it on orphan. Distinct
    /// from DeviceBuffer.Usage, which is Graphite's own enum.
    /// </summary>
    public int WgpuUsage { get; }


    public WgpuBuffer(WgpuGraphicsDevice device, in BufferDescription description)
        : base(description)
    {
        _device = device;
        WgpuUsage = WgpuFormats.BufferUsage(description.Usage);
        Handle = Create(description.SizeInBytes, WgpuUsage, string.Empty);
    }


    static int Create(uint size, int usage, string label)
    {
        // WebGPU requires buffer sizes to be a multiple of 4, and rejects zero-sized buffers.
        uint aligned = Math.Max(4u, (size + 3u) & ~3u);
        return WgpuInterop.CreateBuffer(WgpuDescriptors.Buffer(aligned, usage, label));
    }


    /// <summary>
    /// Replaces the underlying buffer because the old one is still being read by the GPU.
    /// </summary>
    /// <remarks>
    /// WebGPU keeps a submitted buffer alive until its work retires, so the old handle can simply be
    /// released; the browser defers the actual free. That is the whole implementation of orphaning
    /// here, where Vulkan has to track the in-flight execution and defer the destroy itself.
    /// </remarks>
    protected internal override void OrphanCore(GraphicsDevice device, ulong inFlightExecutionId)
    {
        int old = Handle;
        Handle = Create(SizeInBytes, WgpuUsage, Name);
        WgpuInterop.Release(old);
    }


    private protected override void NameChanged(string name) { }


    private protected override void DisposeCore()
    {
        _device.ReleaseWhenRetired(Handle);
        Handle = 0;
    }
}


/// <summary>A GPU texture.</summary>
internal sealed class WgpuTexture : Texture, IWgpuHandle
{
    private readonly WgpuGraphicsDevice _device;
    private readonly bool _owned;

    public int Handle { get; private set; }

    /// <summary>The WebGPU format name, cached because pipelines and views both need it.</summary>
    public string WgpuFormat { get; }


    public WgpuTexture(WgpuGraphicsDevice device, in TextureDescription description)
        : base(description)
    {
        _device = device;
        _owned = true;
        WgpuFormat = WgpuFormats.Texture(description.Format);

        Handle = WgpuInterop.CreateTexture(WgpuDescriptors.Texture(description, WgpuFormat, string.Empty));
    }


    /// <summary>
    /// Wraps a texture the shim already owns, such as a swapchain image. Disposal does not free it.
    /// </summary>
    public WgpuTexture(WgpuGraphicsDevice device, int handle, in TextureDescription description)
        : base(description)
    {
        _device = device;
        _owned = false;
        Handle = handle;
        WgpuFormat = WgpuFormats.Texture(description.Format);
    }


    private protected override void DisposeCore()
    {
        if (_owned)
            _device.ReleaseWhenRetired(Handle);

        Handle = 0;
    }
}


/// <summary>A view onto a texture, which is what actually binds and attaches.</summary>
internal sealed class WgpuTextureView : TextureView, IWgpuHandle
{
    private readonly WgpuGraphicsDevice _device;
    private readonly bool _owned;

    public int Handle { get; private set; }


    /// <param name="device">Owning device.</param>
    /// <param name="description">View properties.</param>
    /// <param name="forAttachment">
    /// True when this view will be a render pass attachment. It changes which aspects of a
    /// depth-stencil format the view covers, and WebGPU rejects the wrong choice.
    /// </param>
    public WgpuTextureView(WgpuGraphicsDevice device, ref TextureViewDescription description, bool forAttachment = false)
        : base(ref description)
    {
        _device = device;
        _owned = true;

        WgpuTexture target = (WgpuTexture)description.Target;
        Handle = WgpuInterop.CreateTextureView(target.Handle, WgpuDescriptors.TextureView(this, target, forAttachment));
    }


    /// <summary>
    /// Wraps a view the shim already owns. Swapchain views are the case: they last one frame and the
    /// swapchain releases them itself, so this must not.
    /// </summary>
    public WgpuTextureView(WgpuGraphicsDevice device, int handle, ref TextureViewDescription description)
        : base(ref description)
    {
        _device = device;
        _owned = false;
        Handle = handle;
    }


    /// <summary>Repoints a non-owning view at this frame's swapchain texture view.</summary>
    public void Retarget(int handle) => Handle = handle;


    private protected override void DisposeCore()
    {
        if (_owned)
            _device.ReleaseWhenRetired(Handle);

        Handle = 0;
    }
}


/// <summary>A sampler.</summary>
internal sealed class WgpuSampler : Sampler, IWgpuHandle
{
    private readonly WgpuGraphicsDevice _device;

    public int Handle { get; private set; }


    public WgpuSampler(WgpuGraphicsDevice device, in SamplerDescription description)
    {
        _device = device;
        Handle = WgpuInterop.CreateSampler(WgpuDescriptors.Sampler(description));
    }


    private protected override void DisposeCore()
    {
        _device.ReleaseWhenRetired(Handle);
        Handle = 0;
    }
}


/// <summary>
/// Completion signal for one submission.
/// </summary>
/// <remarks>
/// WebGPU has no fence object. The queue reports completion through a promise, so this flips a flag
/// from that promise's continuation and <see cref="Signaled"/> reports what has arrived so far.
/// Nothing here can block: the browser thread must not, and there is no primitive that would.
/// </remarks>
internal sealed class WgpuFence : Fence
{
    private bool _signaled;

    public WgpuFence(bool signaled) => _signaled = signaled;

    public override bool Signaled => _signaled;

    public override void Reset() => _signaled = false;

    /// <summary>Marks this fence signalled; called from the queue completion continuation.</summary>
    public void Signal() => _signaled = true;

    private protected override void DisposeCore() { }
}
