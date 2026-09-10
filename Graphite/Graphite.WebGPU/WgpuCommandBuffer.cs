using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Prowl.Vector;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// Records commands into a WebGPU command encoder.
/// </summary>
/// <remarks>
/// Two differences from the Vulkan recorder shape the design.
///
/// A render pass is opened lazily, at the first draw after a framebuffer is bound, because WebGPU
/// bakes the clear behaviour into the pass rather than offering a clear command. That is the same
/// deferral the Vulkan backend already does when no pass is active; here it is the only option, and
/// a clear issued after a draw has to start a new pass instead.
///
/// Bind groups are immutable, so resources resolve into a cached group per draw rather than being
/// written into a descriptor set. Uniform buffers bind with a dynamic offset so the cached group
/// survives the transient arena handing out a different range each execution.
/// </remarks>
internal sealed unsafe class WgpuCommandBuffer : CommandBuffer
{
    private readonly WgpuGraphicsDevice _device;
    private readonly WgpuBindGroupCache _bindGroups;

    private int _encoder;
    private int _pass;

    // Clears queued by ClearColorTarget / ClearDepthStencil, folded into the next pass begin.
    private readonly List<(uint Index, Color Value)> _queuedColorClears = [];
    private float? _queuedDepthClear;
    private byte _queuedStencilClear;

    private PrimitiveTopology _topology = PrimitiveTopology.TriangleList;
    private int _boundPipeline;

    // Scratch reused every draw so binding allocates nothing per call.
    private readonly List<WgpuDescriptors.BindEntry> _entryScratch = [];
    private readonly List<int> _dynamicOffsets = [];
    private byte[] _uniformScratch = new byte[256];


    public WgpuCommandBuffer(WgpuGraphicsDevice device, GraphicsDeviceFeatures features)
        : base(features, device.UniformBufferMinOffsetAlignment, device.StructuredBufferMinOffsetAlignment)
    {
        _device = device;
        _bindGroups = new WgpuBindGroupCache(device);
    }


    /// <summary>
    /// The execution this buffer records into. CommandBuffer.Execution is assigned by the render
    /// context when it rents the buffer; this only narrows it to the backend's type.
    /// </summary>
    private WgpuExecutionTask? WgpuExecution => (WgpuExecutionTask?)Execution;


    internal override void Begin()
    {
        ClearCachedState();
        _encoder = WgpuInterop.CreateCommandEncoder(Name);
        _pass = WgpuInterop.NullHandle;
        _boundPipeline = WgpuInterop.NullHandle;
        _queuedColorClears.Clear();
        _queuedDepthClear = null;
        HasEnded = false;
    }


    internal override void End()
    {
        EndPass();
        HasEnded = true;
    }


    /// <summary>Finishes the encoder and submits it. The shim releases the encoder handle.</summary>
    public void Submit()
    {
        if (_encoder == WgpuInterop.NullHandle)
            return;

        EndPass();
        WgpuInterop.Submit(_encoder);
        _encoder = WgpuInterop.NullHandle;
    }


    // -- render pass --------------------------------------------------------

    private void EndPass()
    {
        if (_pass == WgpuInterop.NullHandle)
            return;

        WgpuInterop.EndRenderPass(_pass);
        _pass = WgpuInterop.NullHandle;
        _boundPipeline = WgpuInterop.NullHandle;
    }


    private void EnsurePass()
    {
        if (_pass != WgpuInterop.NullHandle)
            return;

        if (_framebuffer is not WgpuFramebuffer framebuffer)
            throw new RenderException("A draw needs a framebuffer; call SetFramebuffer first.");

        ReadOnlySpan<int> colorViews = framebuffer.ColorViews;
        WgpuDescriptors.ColorAttachment[] attachments = new WgpuDescriptors.ColorAttachment[colorViews.Length];

        for (int i = 0; i < colorViews.Length; i++)
        {
            bool clear = false;
            Color value = default;

            foreach ((uint index, Color queued) in _queuedColorClears)
            {
                if (index != i)
                    continue;

                clear = true;
                value = queued;
            }

            attachments[i] = new WgpuDescriptors.ColorAttachment(colorViews[i], clear, value);
        }

        int depthView = framebuffer.DepthView;
        bool hasStencil = depthView != 0
            && framebuffer.DepthTarget.HasValue
            && WgpuFormats.HasStencil(framebuffer.DepthTarget.Value.Target.Format);

        _pass = WgpuInterop.BeginRenderPass(_encoder, WgpuDescriptors.RenderPass(
            attachments,
            depthView,
            _queuedDepthClear.HasValue,
            _queuedDepthClear ?? 1.0f,
            hasStencil,
            _queuedStencilClear,
            Name));

        _queuedColorClears.Clear();
        _queuedDepthClear = null;
        _boundPipeline = WgpuInterop.NullHandle;
    }


    private protected override void SetFramebufferCore(Framebuffer fb)
    {
        // A new target means a new pass. Anything queued for the old one is dropped along with it.
        EndPass();
        _queuedColorClears.Clear();
        _queuedDepthClear = null;

        if (fb is WgpuSwapchainFramebuffer)
            ((WgpuSwapchain)_device.MainSwapchain).BeginFrame();
    }


    private protected override void ClearColorTargetCore(uint index, Color clearColor)
    {
        // WebGPU has no mid-pass clear command. Before the first draw the clear folds into the pass
        // begin; after one, the only way to honour it is to close the pass and open another.
        if (_pass != WgpuInterop.NullHandle)
            EndPass();

        _queuedColorClears.Add((index, clearColor));
    }


    private protected override void ClearDepthStencilCore(float depth, byte stencil)
    {
        if (_pass != WgpuInterop.NullHandle)
            EndPass();

        _queuedDepthClear = depth;
        _queuedStencilClear = stencil;
    }


    // -- state --------------------------------------------------------------

    private protected override void SetShaderCore(GraphicsProgram program) => _boundPipeline = WgpuInterop.NullHandle;

    private protected override void SetComputeShaderCore(ComputeProgram program)
        => throw new NotImplementedException("Compute is not yet implemented in the WebGPU backend.");

    private protected override void SetVertexSourceCore(IVertexSource source) { }

    private protected override void SetPropertiesCore(PropertySet properties) { }

    private protected override void ClearPropertiesCore() { }


    /// <inheritdoc/>
    public override void SetViewport(uint index, ref Viewport viewport)
    {
        EnsurePass();
        WgpuInterop.SetViewport(_pass, viewport.X, viewport.Y, viewport.Width, viewport.Height,
            viewport.MinDepth, viewport.MaxDepth);
    }


    /// <inheritdoc/>
    public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
    {
        EnsurePass();
        WgpuInterop.SetScissorRect(_pass, (int)x, (int)y, (int)width, (int)height);
    }


    // -- draw ---------------------------------------------------------------

    private void PrepareDraw()
    {
        if (_shaderProgram is not WgpuGraphicsProgram program)
            throw new RenderException("A draw needs a graphics program; call SetShader first.");

        if (_currentVertexSource is null)
            throw new RenderException("A draw needs a vertex source; call SetVertexSource first.");

        _topology = _currentVertexSource.Topology;

        EnsurePass();

        OutputDescription outputs = _framebuffer!.OutputDescription;
        int pipeline = program.GetPipeline(outputs, _topology);

        if (pipeline != _boundPipeline)
        {
            WgpuInterop.SetPipeline(_pass, pipeline);
            _boundPipeline = pipeline;
        }

        BindResources(program);
        BindVertexBuffers(program);
    }


    private void BindVertexBuffers(WgpuGraphicsProgram program)
    {
        VertexLayoutDescription[] layouts = program.VertexLayoutsArray;

        for (uint slot = 0; slot < layouts.Length; slot++)
        {
            _currentVertexSource!.ResolveSlot(slot, in layouts[slot], out VertexBinding binding);

            WgpuBuffer buffer = (WgpuBuffer)binding.Buffer;
            buffer.MarkInFlight(_device, ExecutionId);

            WgpuInterop.SetVertexBuffer(_pass, (int)slot, buffer.Handle, (int)binding.Offset, 0);
        }
    }


    private void BindResources(WgpuGraphicsProgram program)
    {
        ResourceLayoutDescription[] layouts = program.ResourceLayoutsArray;
        ReadOnlySpan<int> bindGroupLayouts = program.BindGroupLayouts;

        for (int set = 0; set < layouts.Length; set++)
        {
            _entryScratch.Clear();
            _dynamicOffsets.Clear();

            foreach (ResourceLayoutElementDescription element in layouts[set].Elements ?? [])
                ResolveElement(in element, program, layouts[set].Set);

            int group = _bindGroups.Get(bindGroupLayouts[set], System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_entryScratch));

            WgpuInterop.SetBindGroup(
                _pass, set, group,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_dynamicOffsets));
        }
    }


    private void ResolveElement(in ResourceLayoutElementDescription element, WgpuGraphicsProgram program, uint set)
    {
        _activeProperties.Entries.TryGetValue(element.Name, out PropertyEntry? entry);

        switch (element.Kind)
        {
            case ResourceKind.UniformBuffer:
            {
                // A block of loose fields is packed into the execution's arena each draw; an
                // explicitly bound buffer is used as given.
                DeviceBufferRange range = element.UniformFields is { Length: > 0 }
                    ? PackUniformBlock(in element)
                    : entry?.Buffer ?? throw MissingBinding(element, program, set);

                WgpuBuffer buffer = (WgpuBuffer)range.Buffer;
                buffer.MarkInFlight(_device, ExecutionId);

                // The bind group covers the whole buffer at offset zero; the range's offset travels
                // as a dynamic offset so the group stays cacheable across executions.
                _entryScratch.Add(WgpuDescriptors.BindEntry.Buffer(
                    element.BindingIndex, buffer.Handle, 0, range.SizeInBytes));
                _dynamicOffsets.Add((int)range.Offset);
                break;
            }

            case ResourceKind.StructuredBufferReadOnly:
            case ResourceKind.StructuredBufferReadWrite:
            {
                DeviceBufferRange range = entry?.Buffer ?? throw MissingBinding(element, program, set);
                WgpuBuffer buffer = (WgpuBuffer)range.Buffer;
                buffer.MarkInFlight(_device, ExecutionId);

                _entryScratch.Add(WgpuDescriptors.BindEntry.Buffer(
                    element.BindingIndex, buffer.Handle, range.Offset, range.SizeInBytes));
                break;
            }

            case ResourceKind.TextureReadOnly:
            case ResourceKind.TextureReadWrite:
            {
                TextureView? view = entry?.TextureView
                    ?? entry?.Texture?.GetFullTextureView(_device);

                if (view is null)
                    throw MissingBinding(element, program, set);

                _entryScratch.Add(WgpuDescriptors.BindEntry.Resource(
                    element.BindingIndex, ((WgpuTextureView)view).Handle));
                break;
            }

            case ResourceKind.Sampler:
            {
                Sampler sampler = entry?.Sampler ?? throw MissingBinding(element, program, set);
                _entryScratch.Add(WgpuDescriptors.BindEntry.Resource(
                    element.BindingIndex, ((WgpuSampler)sampler).Handle));
                break;
            }

            default:
                throw new NotSupportedException($"ResourceKind.{element.Kind} cannot be bound on WebGPU.");
        }
    }


    private RenderException MissingBinding(in ResourceLayoutElementDescription element, GraphicsProgram program, uint set)
    {
        _device.OnMissingProperty?.Invoke(program, null, element.Name, element.Kind, set, element.BindingIndex);
        return new RenderException(
            $"Shader '{program.Name}' expects '{element.GLUniformName}' at set binding {element.BindingIndex}, but nothing was bound.");
    }


    /// <summary>
    /// Packs a uniform block's loose fields into a transient range from this execution's arena.
    /// </summary>
    private DeviceBufferRange PackUniformBlock(in ResourceLayoutElementDescription element)
    {
        if (WgpuExecution is not WgpuExecutionTask execution)
            throw new RenderException("Uniform packing needs an execution; the command buffer was not rented from one.");

        UniformBlockField[] fields = element.UniformFields;

        uint size = 0;
        foreach (UniformBlockField field in fields)
            size = Math.Max(size, field.Offset + field.Size);

        if (_uniformScratch.Length < size)
            _uniformScratch = new byte[Math.Max(size, (uint)_uniformScratch.Length * 2)];

        Span<byte> packed = _uniformScratch.AsSpan(0, (int)size);
        packed.Clear();

        foreach (UniformBlockField field in fields)
        {
            if (!_activeProperties.Entries.TryGetValue(field.Name, out PropertyEntry? entry))
                continue;

            // The payload is a fixed inline buffer holding the value tightly packed; the field's
            // offset and size come from reflection and already carry the std140 layout.
            fixed (byte* source = entry.Uniform._e0)
            {
                new ReadOnlySpan<byte>(source, (int)field.Size)
                    .CopyTo(packed.Slice((int)field.Offset, (int)field.Size));
            }
        }

        DeviceBufferRange range = execution.Arena.Allocate(size);
        _device.UpdateBuffer(range.Buffer, range.Offset, packed);
        return range;
    }


    private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
    {
        PrepareDraw();
        WgpuInterop.Draw(_pass, (int)vertexCount, (int)instanceCount, (int)vertexStart, (int)instanceStart);
    }


    private protected override void DrawIndexedCore(uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
    {
        PrepareDraw();

        if (!_currentVertexSource!.TryGetIndexBuffer(out DeviceBuffer indexBuffer, out IndexFormat format, out uint indexCount))
            throw new RenderException("An indexed draw needs an index buffer on the bound vertex source.");

        WgpuBuffer buffer = (WgpuBuffer)indexBuffer;
        buffer.MarkInFlight(_device, ExecutionId);

        // indexStart is how many indices to skip, so it comes off the count as well as being the
        // first index; drawing the full count from a non-zero start would run past the buffer.
        uint drawCount = indexCount > indexStart ? indexCount - indexStart : 0;

        WgpuInterop.SetIndexBuffer(_pass, buffer.Handle, WgpuFormats.IndexFormat(format), 0, 0);
        WgpuInterop.DrawIndexed(_pass, (int)drawCount, (int)instanceCount, (int)indexStart, vertexOffset, (int)instanceStart);
    }


    private protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        => throw new NotImplementedException("Indirect draws are not yet implemented in the WebGPU backend.");

    private protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        => throw new NotImplementedException("Indirect draws are not yet implemented in the WebGPU backend.");

    private protected override void DispatchCore(uint groupCountX, uint groupCountY, uint groupCountZ)
        => throw new NotImplementedException("Compute is not yet implemented in the WebGPU backend.");

    private protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
        => throw new NotImplementedException("Compute is not yet implemented in the WebGPU backend.");


    // -- transfers ----------------------------------------------------------

    private protected override unsafe void UpdateBufferCore(
        DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
    {
        // WebGPU's queue writes are ordered against submitted work, so an update recorded here can
        // go straight to the queue rather than being encoded into this buffer.
        _device.UpdateBuffer(buffer, bufferOffsetInBytes, source, sizeInBytes);
    }


    private protected override void CopyBufferCore(
        DeviceBuffer source, uint sourceOffset, DeviceBuffer destination, uint destinationOffset, uint sizeInBytes)
    {
        // A copy is encoder work, so it cannot happen inside a render pass.
        EndPass();

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
        => throw new NotImplementedException(
            "Mipmap generation is not yet implemented in the WebGPU backend; WebGPU has no built-in blit, so it needs a downsample pass.");


    protected override void ResolveTextureCore(Texture source, Texture destination)
        => throw new NotImplementedException(
            "Multisample resolve is not yet implemented in the WebGPU backend; it belongs in the render pass's resolveTarget.");


    // -- debug --------------------------------------------------------------

    private protected override void PushDebugGroupCore(string name) { }

    private protected override void PopDebugGroupCore() { }

    private protected override void InsertDebugMarkerCore(string name) { }


    private protected override void DisposeCore()
    {
        EndPass();
        _bindGroups.Clear();

        if (_encoder != WgpuInterop.NullHandle)
        {
            WgpuInterop.Release(_encoder);
            _encoder = WgpuInterop.NullHandle;
        }
    }
}
