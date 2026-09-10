using System;
using System.Collections.Generic;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// A framebuffer over ordinary textures.
/// </summary>
/// <remarks>
/// WebGPU has no framebuffer object: attachments are named when a render pass begins. So this only
/// holds the views the command buffer will reference, and creates them once rather than per pass.
/// </remarks>
internal class WgpuFramebuffer : Framebuffer
{
    private readonly WgpuGraphicsDevice _device;
    private readonly List<WgpuTextureView> _owned = [];

    private int[] _colorViews = [];
    private int _depthView;


    public WgpuFramebuffer(WgpuGraphicsDevice device, in FramebufferDescription description)
        : base(description.DepthTarget, description.ColorTargets)
    {
        _device = device;
        BuildViews();
    }


    /// <summary>Used by the swapchain framebuffer, which builds its views per frame instead.</summary>
    private protected WgpuFramebuffer(WgpuGraphicsDevice device)
    {
        _device = device;
    }


    /// <summary>View handles for each colour attachment, in order.</summary>
    public virtual ReadOnlySpan<int> ColorViews => _colorViews;

    /// <summary>View handle for the depth attachment, or 0 when there is none.</summary>
    public virtual int DepthView => _depthView;


    private void BuildViews()
    {
        _colorViews = new int[ColorTargets.Count];

        for (int i = 0; i < ColorTargets.Count; i++)
            _colorViews[i] = CreateView(ColorTargets[i]);

        _depthView = DepthTarget.HasValue ? CreateView(DepthTarget.Value) : 0;
    }


    private int CreateView(in FramebufferAttachment attachment)
    {
        TextureViewDescription description = new(
            attachment.Target,
            baseMipLevel: attachment.MipLevel,
            mipLevels: 1,
            baseArrayLayer: attachment.ArrayLayer,
            arrayLayers: 1);

        WgpuTextureView view = new(_device, ref description, forAttachment: true);
        _owned.Add(view);
        return view.Handle;
    }


    private protected override void DisposeCore()
    {
        foreach (WgpuTextureView view in _owned)
            view.Dispose();

        _owned.Clear();
    }
}


/// <summary>
/// The framebuffer a swapchain presents through.
/// </summary>
/// <remarks>
/// The colour attachment is not a texture the backend owns. WebGPU hands out a view of the canvas
/// texture that is valid for exactly one frame and must not be cached, so this asks for a fresh one
/// at the start of each frame and releases it at the end. The depth attachment, by contrast, is an
/// ordinary texture that lives across frames and is only recreated on resize.
/// </remarks>
internal sealed class WgpuSwapchainFramebuffer : WgpuFramebuffer
{
    private readonly WgpuSwapchain _swapchain;
    private readonly int[] _colorView = new int[1];

    private WgpuTexture? _depthTexture;
    private WgpuTextureView? _depthView;
    private FramebufferAttachment[] _colorTargets = [];


    public WgpuSwapchainFramebuffer(WgpuGraphicsDevice device, WgpuSwapchain swapchain)
        : base(device)
    {
        _swapchain = swapchain;
    }


    /// <inheritdoc/>
    public override uint Width => _swapchain.Width;

    /// <inheritdoc/>
    public override uint Height => _swapchain.Height;

    /// <inheritdoc/>
    public override IReadOnlyList<FramebufferAttachment> ColorTargets => _colorTargets;

    /// <inheritdoc/>
    public override FramebufferAttachment? DepthTarget
        => _depthTexture is null ? null : new FramebufferAttachment(_depthTexture, 0, 0);

    /// <inheritdoc/>
    public override OutputDescription OutputDescription => _swapchain.OutputDescription;

    /// <inheritdoc/>
    public override ReadOnlySpan<int> ColorViews => _colorView;

    /// <inheritdoc/>
    public override int DepthView => _depthView?.Handle ?? 0;


    /// <summary>Points the colour attachment at this frame's canvas view.</summary>
    public void SetFrameView(int handle) => _colorView[0] = handle;


    /// <summary>
    /// Recreates the depth texture for a new size. The colour side needs nothing: it comes from the
    /// canvas, which the shim resized already.
    /// </summary>
    public void Resize(WgpuGraphicsDevice device, uint width, uint height, PixelFormat? depthFormat, WgpuTexture colorPlaceholder)
    {
        _colorTargets = [new FramebufferAttachment(colorPlaceholder, 0, 0)];

        _depthView?.Dispose();
        _depthTexture?.Dispose();
        _depthView = null;
        _depthTexture = null;

        if (!depthFormat.HasValue)
            return;

        TextureDescription description = TextureDescription.Texture2D(
            width, height, 1, 1, depthFormat.Value, TextureUsage.DepthStencil);

        _depthTexture = new WgpuTexture(device, description);
        TextureViewDescription depthViewDescription = new(_depthTexture);
        _depthView = new WgpuTextureView(device, ref depthViewDescription, forAttachment: true);
    }


    private protected override void DisposeCore()
    {
        _depthView?.Dispose();
        _depthTexture?.Dispose();
    }
}


/// <summary>
/// Presents to an HTML canvas.
/// </summary>
/// <remarks>
/// There is no swapchain object in WebGPU and no present call. A canvas context is configured once,
/// then each frame asks it for the texture to draw into, and the browser composites whatever was
/// drawn once the frame's work retires.
/// </remarks>
internal sealed class WgpuSwapchain : Swapchain
{
    private readonly WgpuGraphicsDevice _device;
    private readonly WgpuSwapchainFramebuffer _framebuffer;
    private readonly PixelFormat? _depthFormat;
    private readonly string _canvasSelector;

    private int _contextHandle;
    private int _frameViewHandle;
    private WgpuTexture _colorPlaceholder;

    private uint _width;
    private uint _height;


    public WgpuSwapchain(WgpuGraphicsDevice device, in SwapchainDescription description)
    {
        _device = device;
        _depthFormat = description.DepthFormat;
        _width = Math.Max(1u, description.Width);
        _height = Math.Max(1u, description.Height);

        _canvasSelector = description.Source is WgpuCanvasSwapchainSource canvas
            ? canvas.CanvasSelector
            : throw new RenderException(
                "A WebGPU swapchain needs a canvas source; build one with SwapchainSource.CreateCanvas.");

        Format = WgpuInterop.GetPreferredCanvasFormat();

        _contextHandle = WgpuInterop.ConfigureCanvas(
            _canvasSelector, Format, "opaque", (int)_width, (int)_height);

        if (_contextHandle == WgpuInterop.NullHandle)
        {
            throw new RenderException(
                $"Could not configure canvas '{_canvasSelector}' for WebGPU: {WgpuInterop.TakeError()}");
        }

        _framebuffer = new WgpuSwapchainFramebuffer(device, this);
        _colorPlaceholder = CreateColorPlaceholder();
        _framebuffer.Resize(device, _width, _height, _depthFormat, _colorPlaceholder);

        // Must be published here as well as on resize: pipelines are keyed on it, and without this
        // the first frame builds every pipeline against an empty output description.
        OutputDescription = OutputDescription.CreateFromFramebuffer(_framebuffer);
    }


    /// <summary>The WebGPU format string the canvas was configured with.</summary>
    public string Format { get; }

    /// <summary>Current drawing-buffer width.</summary>
    public uint Width => _width;

    /// <summary>Current drawing-buffer height.</summary>
    public uint Height => _height;

    /// <inheritdoc/>
    public override Framebuffer Framebuffer => _framebuffer;

    /// <summary>Output formats, for pipeline creation.</summary>
    public OutputDescription OutputDescription { get; private set; }


    /// <inheritdoc/>
    public override bool SyncToVerticalBlank
    {
        // The browser paces presentation through requestAnimationFrame; a page cannot opt out.
        get => true;
        set { }
    }


    /// <summary>
    /// Acquires this frame's canvas view. Called once per frame before anything targets the
    /// swapchain, because the view is only valid until the frame's work is submitted.
    /// </summary>
    public void BeginFrame()
    {
        if (_frameViewHandle != WgpuInterop.NullHandle)
            return;

        _frameViewHandle = WgpuInterop.GetCurrentTextureView(_contextHandle);
        _framebuffer.SetFrameView(_frameViewHandle);
    }


    /// <summary>Releases this frame's canvas view. The next frame acquires a new one.</summary>
    public void EndFrame()
    {
        if (_frameViewHandle == WgpuInterop.NullHandle)
            return;

        WgpuInterop.Release(_frameViewHandle);
        _frameViewHandle = WgpuInterop.NullHandle;
        _framebuffer.SetFrameView(WgpuInterop.NullHandle);
    }


    /// <inheritdoc/>
    public override void Resize(uint width, uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (width == _width && height == _height)
            return;

        EndFrame();

        _width = width;
        _height = height;

        WgpuInterop.ResizeCanvas(_contextHandle, (int)width, (int)height);

        _colorPlaceholder.Dispose();
        _colorPlaceholder = CreateColorPlaceholder();
        _framebuffer.Resize(_device, width, height, _depthFormat, _colorPlaceholder);
        OutputDescription = OutputDescription.CreateFromFramebuffer(_framebuffer);
    }


    // Graphite describes a framebuffer's outputs in terms of Texture objects, but the canvas texture
    // is not one the backend owns and changes every frame. This stands in for it: it carries the
    // right format and size for pipeline creation and is never rendered into.
    private WgpuTexture CreateColorPlaceholder()
    {
        PixelFormat format = Format switch
        {
            "bgra8unorm" => PixelFormat.B8_G8_R8_A8_UNorm,
            "bgra8unorm-srgb" => PixelFormat.B8_G8_R8_A8_UNorm_SRgb,
            "rgba8unorm" => PixelFormat.R8_G8_B8_A8_UNorm,
            "rgba8unorm-srgb" => PixelFormat.R8_G8_B8_A8_UNorm_SRgb,
            _ => PixelFormat.B8_G8_R8_A8_UNorm
        };

        TextureDescription description = TextureDescription.Texture2D(
            _width, _height, 1, 1, format, TextureUsage.RenderTarget);

        // Created without a backing texture: the handle is 0 and never bound, because the real
        // attachment is whatever view the canvas hands out this frame.
        return new WgpuTexture(_device, WgpuInterop.NullHandle, description);
    }


    private protected override void DisposeCore()
    {
        EndFrame();
        _framebuffer.Dispose();
        _colorPlaceholder.Dispose();
        WgpuInterop.Release(_contextHandle);
        _contextHandle = WgpuInterop.NullHandle;
    }
}
