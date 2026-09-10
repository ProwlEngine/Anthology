using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Prowl.Graphite.Wgpu;


// The execution ring, and the concession the browser forces.
//
// Graphite's ring exists so a slot's transient memory is never rewritten while the GPU is still
// reading it, and Vulkan enforces that by blocking on a fence. Neither half of that works here: the
// browser thread must not block, and WebGPU has no fence to block on. What replaces it is that
// WebGPU tracks resource lifetimes itself. A buffer written through the queue is copied into a
// staging allocation the browser owns, and a submitted resource stays alive until its work retires,
// so the hazard the ring guards against is the implementation's problem rather than ours.
//
// So the ring still turns, still hands out slots, and still reports completion, but a wait for a
// slot cannot stall. Completion arrives from the queue's promise instead.
internal sealed partial class WgpuGraphicsDevice
{
    private readonly Dictionary<uint, WgpuUniformArena> _arenas = [];


    /// <summary>Frees every ring slot's transient buffers. Called when the device shuts down.</summary>
    private void DisposeArenas()
    {
        foreach (WgpuUniformArena arena in _arenas.Values)
            arena.Dispose();

        _arenas.Clear();
    }


    private protected override ExecutionTask BeginExecutionCore(ulong executionId, uint ringSlot)
    {
        if (!_arenas.TryGetValue(ringSlot, out WgpuUniformArena? arena))
            _arenas[ringSlot] = arena = new WgpuUniformArena(this, _transientInitialSize);

        arena.Reset();

        FlushPendingReleases();

        return new WgpuExecutionTask(this, executionId, ringSlot, arena);
    }


    private protected override void CompleteExecutionCore(ExecutionTask task)
    {
        WgpuExecutionTask wgpuTask = (WgpuExecutionTask)task;
        wgpuTask.Flush();

        // The only completion signal WebGPU offers. The continuation runs on the browser's task
        // queue, so the fence flips some time after this returns and IsExecutionComplete sees it.
        _ = WgpuInterop.OnSubmittedWorkDoneAsync().ContinueWith(
            _ => wgpuTask.SignalCompletion(),
            System.Threading.Tasks.TaskScheduler.Default);

        PumpErrors();
    }


    private protected override bool IsExecutionCompleteCore(ExecutionTask task)
        => ((WgpuExecutionTask)task).CompletionFence.Signaled;


    private protected override bool WaitForExecutionCore(ExecutionTask task, ulong nanosecondTimeout)
    {
        // Cannot block, so this reports rather than waits. The ring reclaims the slot either way,
        // which is safe because WebGPU will not let us overwrite memory it is still reading.
        PumpErrors();
        return true;
    }


    private protected override void WaitForIdleCore()
    {
        // Same concession. Everything submitted stays alive until the browser retires it.
        PumpErrors();
        FlushPendingReleases();
    }


    private protected override void SubmitAndWaitCore(TransferCommandBuffer commandBuffer)
    {
        ((WgpuTransferCommandBuffer)commandBuffer).Submit();
        PumpErrors();
    }


    private protected override void SubmitTransferCore(TransferCommandBuffer commandBuffer)
    {
        ((WgpuTransferCommandBuffer)commandBuffer).Submit();
        PumpErrors();
    }


    // -- uploads ------------------------------------------------------------

    private protected override unsafe void UpdateBufferCore(
        DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
    {
        WgpuBuffer target = (WgpuBuffer)buffer;

        // queue.writeBuffer copies out of the span synchronously, so a view over unmanaged memory is
        // safe for the duration of the call and nothing needs staging on this side.
        Span<byte> data = new((void*)source, (int)sizeInBytes);

        // WebGPU requires both the offset and the size to be 4-byte aligned. A tail that is not a
        // multiple of four is padded out of a scratch copy rather than rejected.
        if ((sizeInBytes & 3) == 0)
        {
            WgpuInterop.WriteBuffer(target.Handle, (int)bufferOffsetInBytes, data);
            return;
        }

        uint padded = (sizeInBytes + 3u) & ~3u;
        byte[] scratch = new byte[padded];
        data.CopyTo(scratch);
        WgpuInterop.WriteBuffer(target.Handle, (int)bufferOffsetInBytes, scratch.AsSpan());
    }


    private protected override unsafe void UpdateTextureCore(
        Texture texture,
        IntPtr source,
        uint sizeInBytes,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer)
    {
        WgpuTexture target = (WgpuTexture)texture;

        uint bytesPerPixel = FormatSizeHelpers.GetSizeInBytes(texture.Format);
        uint bytesPerRow = width * bytesPerPixel;

        Span<byte> data = new((void*)source, (int)sizeInBytes);

        WgpuInterop.WriteTexture(
            target.Handle,
            WgpuDescriptors.TextureDestination(x, y, z, mipLevel, arrayLayer),
            WgpuDescriptors.TextureDataLayout(0, bytesPerRow, height),
            (int)width, (int)height, (int)depth,
            data);
    }
}


/// <summary>
/// One dispatched graph. Owns a ring slot, its transient uniform arena, and the fence the queue
/// signals when the work retires.
/// </summary>
internal sealed class WgpuExecutionTask : ExecutionTask
{
    private readonly WgpuGraphicsDevice _device;
    private readonly WgpuUniformArena _arena;
    private readonly WgpuFence _fence = new(signaled: false);
    private readonly List<WgpuCommandBuffer> _submitted = [];


    public WgpuExecutionTask(WgpuGraphicsDevice device, ulong id, uint ringSlot, WgpuUniformArena arena)
    {
        _device = device;
        _arena = arena;
        Id = id;
        RingSlot = ringSlot;
    }


    /// <inheritdoc/>
    public override ulong Id { get; }

    /// <inheritdoc/>
    public override uint RingSlot { get; }

    /// <inheritdoc/>
    public override Fence CompletionFence => _fence;

    /// <inheritdoc/>
    public override GraphicsDevice Device => _device;

    /// <summary>The arena backing this execution's transient uniform allocations.</summary>
    public WgpuUniformArena Arena => _arena;


    internal override void SubmitCommandsInternal(CommandBuffer commandList)
        => _submitted.Add((WgpuCommandBuffer)commandList);


    internal override void FlushSubmissions() => Flush();


    /// <summary>Submits everything recorded so far, in the order it was queued.</summary>
    public void Flush()
    {
        foreach (WgpuCommandBuffer buffer in _submitted)
            buffer.Submit();

        _submitted.Clear();
    }


    /// <summary>Marks the queue's work for this execution as retired.</summary>
    public void SignalCompletion() => _fence.Signal();


    internal override DeviceBufferRange AllocateTransientInternal(uint sizeInBytes)
        => _arena.Allocate(sizeInBytes);
}


/// <summary>
/// Per-slot bump allocator for transient uniform data.
/// </summary>
/// <remarks>
/// Vulkan keeps this memory persistently mapped and writes straight into it. The browser has no
/// persistent mapping, so writes go through the queue instead, which copies immediately. That makes
/// the allocator simpler than the Vulkan one: there is no in-flight window to respect, because the
/// browser will not let a write land on memory the GPU is still reading.
/// </remarks>
internal sealed class WgpuUniformArena
{
    private readonly WgpuGraphicsDevice _device;
    private readonly List<WgpuBuffer> _blocks = [];
    private readonly uint _blockSize;

    private int _blockIndex;
    private uint _head;


    public WgpuUniformArena(WgpuGraphicsDevice device, uint blockSize)
    {
        _device = device;
        _blockSize = Math.Max(64 * 1024u, blockSize);
    }


    /// <summary>Rewinds to the start of the arena for a new execution.</summary>
    public void Reset()
    {
        _blockIndex = 0;
        _head = 0;
    }


    /// <summary>Carves out a uniform range, growing the arena by a block when the current one is full.</summary>
    public DeviceBufferRange Allocate(uint sizeInBytes)
    {
        uint alignment = _device.Limits.MinUniformBufferOffsetAlignment;
        uint size = (sizeInBytes + 3u) & ~3u;

        if (_blocks.Count == 0)
            Grow();

        uint offset = (_head + alignment - 1) & ~(alignment - 1);

        if (offset + size > _blocks[_blockIndex].SizeInBytes)
        {
            _blockIndex++;
            if (_blockIndex == _blocks.Count)
                Grow();

            offset = 0;
        }

        _head = offset + size;
        return new DeviceBufferRange(_blocks[_blockIndex], offset, size);
    }


    private void Grow()
    {
        BufferDescription description = new(_blockSize, BufferUsage.UniformBuffer | BufferUsage.Dynamic)
        {
            TransientWrites = true
        };

        _blocks.Add(new WgpuBuffer(_device, description));
    }


    /// <summary>Frees every block. Called when the device shuts down.</summary>
    public void Dispose()
    {
        foreach (WgpuBuffer block in _blocks)
            block.Dispose();

        _blocks.Clear();
    }
}
