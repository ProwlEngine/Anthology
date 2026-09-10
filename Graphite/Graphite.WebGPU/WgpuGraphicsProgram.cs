using System;
using System.Collections.Generic;
using System.Text;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// A graphics program: shader modules, bind group layouts, and the pipelines built from them.
/// </summary>
/// <remarks>
/// A WebGPU render pipeline bakes in the formats of the targets it draws to, which Graphite does not
/// know until a framebuffer is bound. So pipelines are built lazily, one per output description this
/// program is drawn with, and cached. That mirrors what the Vulkan backend does for render passes;
/// what it does not have is a serializable pipeline cache, because the web exposes none.
/// </remarks>
internal sealed class WgpuGraphicsProgram : GraphicsProgram
{
    private readonly WgpuGraphicsDevice _device;
    private readonly Dictionary<PipelineKey, int> _pipelines = [];
    private readonly int[] _bindGroupLayouts;
    private readonly int _pipelineLayout;

    private readonly int _vertexModule;
    private readonly int _fragmentModule;
    private readonly string _vertexEntryPoint;
    private readonly string _fragmentEntryPoint;


    public WgpuGraphicsProgram(WgpuGraphicsDevice device, ref ShaderDescription description)
        : base(ref description)
    {
        _device = device;

        ResourceLayoutDescription[] layouts = ResourceLayoutsArray;

        if (layouts.Length > device.Limits.MaxBindGroups)
        {
            throw new RenderException(
                $"Shader declares {layouts.Length} descriptor sets, but this device allows {device.Limits.MaxBindGroups} bind groups.");
        }

        _bindGroupLayouts = new int[layouts.Length];
        for (int i = 0; i < layouts.Length; i++)
            _bindGroupLayouts[i] = WgpuInterop.CreateBindGroupLayout(WgpuDescriptors.BindGroupLayout(layouts[i], Name));

        _pipelineLayout = WgpuInterop.CreatePipelineLayout(WgpuDescriptors.PipelineLayout(_bindGroupLayouts, Name));

        (_vertexModule, _vertexEntryPoint) = CreateModule(description, ShaderStages.Vertex);
        (_fragmentModule, _fragmentEntryPoint) = CreateModule(description, ShaderStages.Fragment);

        if (_vertexModule == WgpuInterop.NullHandle)
            throw new RenderException("A WebGPU graphics program needs a vertex stage.");
    }


    /// <summary>Bind group layout handles, one per descriptor set, indexed by set number.</summary>
    public ReadOnlySpan<int> BindGroupLayouts => _bindGroupLayouts;


    static (int Handle, string EntryPoint) CreateModule(in ShaderDescription description, ShaderStages stage)
    {
        foreach (ShaderStageDescription candidate in description.Stages ?? [])
        {
            if (candidate.Stage != stage)
                continue;

            // The bytes are WGSL text, produced ahead of time by Tools/ShaderPrecompile, because the
            // Slang compiler is native and cannot run in WebAssembly.
            string wgsl = Encoding.UTF8.GetString(candidate.ShaderBytes);
            return (WgpuInterop.CreateShaderModule(wgsl, stage.ToString()), candidate.EntryPoint);
        }

        return (WgpuInterop.NullHandle, string.Empty);
    }


    /// <summary>
    /// A pipeline is identified by everything WebGPU bakes into it that Graphite can vary at draw
    /// time: the formats it writes to, and the topology it assembles.
    /// </summary>
    private readonly struct PipelineKey : IEquatable<PipelineKey>
    {
        private readonly OutputDescription _outputs;
        private readonly PrimitiveTopology _topology;
        private readonly IndexFormat? _stripIndexFormat;

        public PipelineKey(in OutputDescription outputs, PrimitiveTopology topology, IndexFormat? stripIndexFormat)
        {
            _outputs = outputs;
            _topology = topology;
            _stripIndexFormat = stripIndexFormat;
        }

        public bool Equals(PipelineKey other)
            => _topology == other._topology
               && _stripIndexFormat == other._stripIndexFormat
               && _outputs.Equals(other._outputs);

        public override bool Equals(object? obj) => obj is PipelineKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_outputs, (int)_topology, _stripIndexFormat);
    }


    /// <summary>
    /// The pipeline for these output formats, topology and index format, built on first use.
    /// </summary>
    /// <param name="outputs">Formats the pipeline writes to.</param>
    /// <param name="topology">Primitive topology to assemble.</param>
    /// <param name="stripIndexFormat">
    /// Index format for an indexed draw with a strip topology, which WebGPU bakes into the pipeline
    /// so it knows the primitive-restart value. Null for anything else.
    /// </param>
    public int GetPipeline(in OutputDescription outputs, PrimitiveTopology topology, IndexFormat? stripIndexFormat)
    {
        PipelineKey key = new(outputs, topology, stripIndexFormat);

        if (_pipelines.TryGetValue(key, out int existing))
            return existing;

        int pipeline = WgpuInterop.CreateRenderPipeline(BuildPipelineDescriptor(outputs, topology, stripIndexFormat));

        if (pipeline == WgpuInterop.NullHandle)
            throw new RenderException($"Failed to create a WebGPU render pipeline for '{Name}': {WgpuInterop.TakeError()}");

        _pipelines[key] = pipeline;
        return pipeline;
    }


    private string BuildPipelineDescriptor(
        in OutputDescription outputs, PrimitiveTopology topology, IndexFormat? stripIndexFormat)
    {
        return WgpuDescriptors.RenderPipeline(
            _pipelineLayout,
            _vertexModule, _vertexEntryPoint,
            _fragmentModule, _fragmentEntryPoint,
            VertexLayoutsArray,
            outputs,
            topology,
            stripIndexFormat,
            in BlendStateRef,
            in DepthStencilStateRef,
            in RasterizerStateRef,
            Name);
    }


    private protected override void DisposeCore()
    {
        foreach (int pipeline in _pipelines.Values)
            _device.ReleaseWhenRetired(pipeline);

        _pipelines.Clear();

        _device.ReleaseWhenRetired(_pipelineLayout);

        foreach (int layout in _bindGroupLayouts)
            _device.ReleaseWhenRetired(layout);

        _device.ReleaseWhenRetired(_vertexModule);
        _device.ReleaseWhenRetired(_fragmentModule);
    }
}


/// <summary>
/// Caches bind groups by what they bind.
/// </summary>
/// <remarks>
/// A WebGPU bind group is immutable once created, so a draw that binds the same resources must reuse
/// one rather than rebuild it. Uniform buffers are bound with a dynamic offset, which keeps a bind
/// group valid across executions even though the transient arena hands out a different range each
/// time; without that, every frame would rebuild every bind group.
/// </remarks>
internal sealed class WgpuBindGroupCache
{
    private readonly WgpuGraphicsDevice _device;
    private readonly Dictionary<Key, int> _groups = [];


    public WgpuBindGroupCache(WgpuGraphicsDevice device) => _device = device;


    private readonly struct Key : IEquatable<Key>
    {
        private readonly int _layout;
        private readonly int[] _handles;
        private readonly int _hash;

        public Key(int layout, int[] handles)
        {
            _layout = layout;
            _handles = handles;

            HashCode hash = new();
            hash.Add(layout);
            foreach (int handle in handles)
                hash.Add(handle);

            _hash = hash.ToHashCode();
        }

        public bool Equals(Key other)
        {
            if (_layout != other._layout || _handles.Length != other._handles.Length)
                return false;

            for (int i = 0; i < _handles.Length; i++)
                if (_handles[i] != other._handles[i])
                    return false;

            return true;
        }

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => _hash;
    }


    /// <summary>Returns a cached bind group for these entries, creating one on first use.</summary>
    public int Get(int layout, ReadOnlySpan<WgpuDescriptors.BindEntry> entries)
    {
        int[] handles = new int[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            handles[i] = entries[i].Handle;

        Key key = new(layout, handles);

        if (_groups.TryGetValue(key, out int existing))
            return existing;

        int group = WgpuInterop.CreateBindGroup(WgpuDescriptors.BindGroup(layout, entries));
        _groups[key] = group;
        return group;
    }


    /// <summary>Drops every cached group. Called when the device shuts down.</summary>
    public void Clear()
    {
        foreach (int group in _groups.Values)
            WgpuInterop.Release(group);

        _groups.Clear();
    }
}
