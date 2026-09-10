using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

using Prowl.Vector;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// Builds the JSON descriptors the shim's creation calls take.
/// </summary>
/// <remarks>
/// Written with <see cref="Utf8JsonWriter"/> rather than a serializer over object graphs. .NET
/// WebAssembly disables reflection-based JSON by default, so serializing an anonymous type throws at
/// runtime, and reflection would be the wrong cost here regardless: these run on resource creation,
/// and the pipeline descriptor is rebuilt whenever a program meets an output format it has not seen.
/// </remarks>
internal static class WgpuDescriptors
{
    [ThreadStatic] private static ArrayBufferWriter<byte>? t_buffer;


    static string Write(Action<Utf8JsonWriter> body)
    {
        ArrayBufferWriter<byte> buffer = t_buffer ??= new ArrayBufferWriter<byte>(1024);
        buffer.ResetWrittenCount();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    static void WriteLabel(Utf8JsonWriter w, string label)
    {
        if (!string.IsNullOrEmpty(label))
            w.WriteString("label", label);
    }


    // -- resources ----------------------------------------------------------

    public static string Buffer(uint size, int usage, string label) => Write(w =>
    {
        w.WriteNumber("size", size);
        w.WriteNumber("usage", usage);
        WriteLabel(w, label);
    });


    public static string Texture(in TextureDescription d, string format, string label)
    {
        // Captured because a lambda cannot close over a by-ref parameter.
        uint width = d.Width, height = d.Height, depth = d.Depth;
        uint mips = d.MipLevels, layers = d.ArrayLayers;
        int usage = WgpuFormats.TextureUsage(d.Usage, d.Format);
        int samples = (int)d.SampleCount switch { 0 => 1, int s => 1 << s };
        string dimension = d.Type switch
        {
            TextureType.Texture1D => "1d",
            TextureType.Texture3D => "3d",
            _ => "2d"
        };

        return Write(w =>
        {
            w.WriteStartObject("size");
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            // A 2D array's layers travel in the same slot a 3D texture uses for depth.
            w.WriteNumber("depthOrArrayLayers", dimension == "3d" ? depth : layers);
            w.WriteEndObject();

            w.WriteNumber("mipLevelCount", mips);
            w.WriteNumber("sampleCount", samples);
            w.WriteString("dimension", dimension);
            w.WriteString("format", format);
            w.WriteNumber("usage", usage);
            WriteLabel(w, label);
        });
    }


    public static string TextureView(TextureView view, WgpuTexture target, bool forAttachment)
    {
        uint baseMip = view.BaseMipLevel, mips = view.MipLevels;
        uint baseLayer = view.BaseArrayLayer, layers = view.ArrayLayers;
        string format = WgpuFormats.Texture(view.Format);
        string dimension = WgpuFormats.TextureDimension(target.Type, layers, cubemap: false);
        bool depth = WgpuFormats.IsDepthFormat(view.Format);
        bool stencil = WgpuFormats.HasStencil(view.Format);

        return Write(w =>
        {
            w.WriteString("format", format);
            w.WriteString("dimension", dimension);
            w.WriteNumber("baseMipLevel", baseMip);
            w.WriteNumber("mipLevelCount", mips);
            w.WriteNumber("baseArrayLayer", baseLayer);
            w.WriteNumber("arrayLayerCount", layers);

            // A depth-stencil attachment has to cover both aspects, and WebGPU rejects a depth-only
            // view used as one. Only a view that will be sampled names a single aspect, because a
            // shader cannot read both at once.
            if (depth && stencil && !forAttachment)
                w.WriteString("aspect", "depth-only");
        });
    }


    public static string Sampler(in SamplerDescription d)
    {
        string u = WgpuFormats.AddressMode(d.AddressModeU);
        string v = WgpuFormats.AddressMode(d.AddressModeV);
        string wrapW = WgpuFormats.AddressMode(d.AddressModeW);
        bool linear = d.Filter != SamplerFilter.MinPoint_MagPoint_MipPoint;
        uint minLod = d.MinimumLod, maxLod = d.MaximumLod;
        uint aniso = d.MaximumAnisotropy;
        string? compare = d.ComparisonKind.HasValue ? WgpuFormats.Compare(d.ComparisonKind.Value) : null;

        return Write(w =>
        {
            w.WriteString("addressModeU", u);
            w.WriteString("addressModeV", v);
            w.WriteString("addressModeW", wrapW);
            w.WriteString("magFilter", linear ? "linear" : "nearest");
            w.WriteString("minFilter", linear ? "linear" : "nearest");
            w.WriteString("mipmapFilter", linear ? "linear" : "nearest");
            w.WriteNumber("lodMinClamp", minLod);
            w.WriteNumber("lodMaxClamp", maxLod == 0 ? 32u : maxLod);

            // WebGPU only permits anisotropy above 1 when every filter is linear.
            if (aniso > 1 && linear)
                w.WriteNumber("maxAnisotropy", Math.Min(aniso, 16u));

            if (compare != null)
                w.WriteString("compare", compare);
        });
    }


    // -- binding ------------------------------------------------------------

    public static string BindGroupLayout(in ResourceLayoutDescription set, string label)
    {
        ResourceLayoutElementDescription[] elements = set.Elements ?? [];

        return Write(w =>
        {
            WriteLabel(w, label);
            w.WriteStartArray("entries");

            foreach (ResourceLayoutElementDescription element in elements)
            {
                w.WriteStartObject();
                w.WriteNumber("binding", element.BindingIndex);
                w.WriteNumber("visibility", WgpuFormats.Visibility(element.Stages));

                switch (element.Kind)
                {
                    case ResourceKind.UniformBuffer:
                        w.WriteStartObject("buffer");
                        w.WriteString("type", "uniform");
                        // Every uniform binding takes a dynamic offset, because the transient arena
                        // hands out a different range each execution and rebuilding the bind group
                        // for each one would defeat the point of caching them.
                        w.WriteBoolean("hasDynamicOffset", true);
                        w.WriteEndObject();
                        break;

                    case ResourceKind.StructuredBufferReadOnly:
                        w.WriteStartObject("buffer");
                        w.WriteString("type", "read-only-storage");
                        w.WriteEndObject();
                        break;

                    case ResourceKind.StructuredBufferReadWrite:
                        w.WriteStartObject("buffer");
                        w.WriteString("type", "storage");
                        w.WriteEndObject();
                        break;

                    case ResourceKind.TextureReadOnly:
                        w.WriteStartObject("texture");
                        w.WriteString("sampleType", "float");
                        w.WriteString("viewDimension", "2d");
                        w.WriteEndObject();
                        break;

                    case ResourceKind.TextureReadWrite:
                        w.WriteStartObject("storageTexture");
                        w.WriteString("access", "write-only");
                        w.WriteString("format", "rgba8unorm");
                        w.WriteString("viewDimension", "2d");
                        w.WriteEndObject();
                        break;

                    case ResourceKind.Sampler:
                        w.WriteStartObject("sampler");
                        w.WriteString("type", "filtering");
                        w.WriteEndObject();
                        break;

                    default:
                        throw new NotSupportedException($"ResourceKind.{element.Kind} has no WebGPU binding type.");
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        });
    }


    public static string PipelineLayout(ReadOnlySpan<int> bindGroupLayouts, string label)
    {
        int[] copy = bindGroupLayouts.ToArray();

        return Write(w =>
        {
            WriteLabel(w, label);
            w.WriteStartArray("bindGroupLayouts");
            foreach (int handle in copy)
                w.WriteNumberValue(handle);
            w.WriteEndArray();
        });
    }


    /// <summary>One entry in a bind group: a buffer range, or a texture view or sampler by handle.</summary>
    internal readonly struct BindEntry
    {
        public readonly int Binding;
        public readonly int Handle;
        public readonly bool IsBuffer;
        public readonly uint Offset;
        public readonly uint Size;

        private BindEntry(int binding, int handle, bool isBuffer, uint offset, uint size)
        {
            Binding = binding;
            Handle = handle;
            IsBuffer = isBuffer;
            Offset = offset;
            Size = size;
        }

        public static BindEntry Buffer(int binding, int handle, uint offset, uint size)
            => new(binding, handle, true, offset, size);

        public static BindEntry Resource(int binding, int handle)
            => new(binding, handle, false, 0, 0);
    }


    public static string BindGroup(int layout, ReadOnlySpan<BindEntry> entries)
    {
        BindEntry[] copy = entries.ToArray();

        return Write(w =>
        {
            w.WriteNumber("layout", layout);
            w.WriteStartArray("entries");

            foreach (BindEntry entry in copy)
            {
                w.WriteStartObject();
                w.WriteNumber("binding", entry.Binding);

                if (entry.IsBuffer)
                {
                    w.WriteNumber("buffer", entry.Handle);
                    // A dynamic-offset binding always starts at zero here; the real offset is passed
                    // to setBindGroup at draw time, and the size bounds one slice of the arena.
                    w.WriteNumber("offset", entry.Offset);
                    if (entry.Size > 0)
                        w.WriteNumber("size", entry.Size);
                }
                else
                {
                    w.WriteNumber("resource", entry.Handle);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        });
    }


    /// <summary>Where a texture write lands: mip level, array layer and origin.</summary>
    public static string TextureDestination(uint x, uint y, uint z, uint mipLevel, uint arrayLayer) => Write(w =>
    {
        w.WriteNumber("mipLevel", mipLevel);
        w.WriteStartObject("origin");
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        // A 2D array's layer travels in the z slot of the origin, the same slot a 3D texture uses
        // for depth, so only one of the two is ever non-zero.
        w.WriteNumber("z", arrayLayer != 0 ? arrayLayer : z);
        w.WriteEndObject();
    });


    /// <summary>How the source bytes are laid out for a texture write.</summary>
    public static string TextureDataLayout(uint offset, uint bytesPerRow, uint rowsPerImage) => Write(w =>
    {
        w.WriteNumber("offset", offset);
        w.WriteNumber("bytesPerRow", bytesPerRow);
        w.WriteNumber("rowsPerImage", rowsPerImage);
    });


    // -- commands -----------------------------------------------------------

    public static string RenderPipeline(
        int layout,
        int vertexModule, string vertexEntryPoint,
        int fragmentModule, string fragmentEntryPoint,
        VertexLayoutDescription[] vertexLayouts,
        in OutputDescription outputs,
        PrimitiveTopology topology,
        IndexFormat? stripIndexFormat,
        in BlendStateDescription blend,
        in DepthStencilStateDescription depthStencil,
        in RasterizerStateDescription rasterizer,
        string label)
    {
        // Copied out because a lambda cannot close over by-ref parameters.
        BlendStateDescription blendCopy = blend;
        DepthStencilStateDescription depthCopy = depthStencil;
        RasterizerStateDescription rasterCopy = rasterizer;
        OutputAttachmentDescription[] colorTargets = outputs.ColorAttachments ?? [];
        PixelFormat? depthFormat = outputs.DepthAttachment?.Format;
        string topologyName = WgpuFormats.Topology(topology);
        string? stripFormat = stripIndexFormat.HasValue ? WgpuFormats.IndexFormat(stripIndexFormat.Value) : null;
        int sampleCount = (int)outputs.SampleCount switch { 0 => 1, int s => 1 << s };

        return Write(w =>
        {
            WriteLabel(w, label);
            w.WriteNumber("layout", layout);

            w.WriteStartObject("vertex");
            w.WriteNumber("module", vertexModule);
            w.WriteString("entryPoint", vertexEntryPoint);
            w.WriteStartArray("buffers");
            foreach (VertexLayoutDescription vertexLayout in vertexLayouts)
            {
                w.WriteStartObject();
                w.WriteNumber("arrayStride", vertexLayout.Stride);
                w.WriteString("stepMode", vertexLayout.InstanceStepRate > 0 ? "instance" : "vertex");
                w.WriteStartArray("attributes");
                foreach (VertexElementDescription element in vertexLayout.Elements ?? [])
                {
                    w.WriteStartObject();
                    // The shader location is the buffer slot: Graphite gives each layout its own
                    // slot, and the reflection assigns WGSL locations in the same order.
                    w.WriteNumber("shaderLocation", vertexLayout.Location);
                    w.WriteNumber("offset", element.Offset);
                    w.WriteString("format", WgpuFormats.Vertex(element.Format));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            if (fragmentModule != 0 && colorTargets.Length > 0)
            {
                w.WriteStartObject("fragment");
                w.WriteNumber("module", fragmentModule);
                w.WriteString("entryPoint", fragmentEntryPoint);
                w.WriteStartArray("targets");

                for (int i = 0; i < colorTargets.Length; i++)
                {
                    // Independent blend gives each target its own state; otherwise they share the
                    // first attachment's, which is what Graphite's single-attachment presets mean.
                    BlendAttachmentDescription attachment = blendCopy.AttachmentStates is { Length: > 0 } states
                        ? states[Math.Min(i, states.Length - 1)]
                        : default;

                    w.WriteStartObject();
                    w.WriteString("format", WgpuFormats.Texture(colorTargets[i].Format));
                    w.WriteNumber("writeMask", WgpuFormats.ColorWrite(attachment.ColorWriteMask ?? ColorWriteMask.All));

                    if (attachment.BlendEnabled)
                    {
                        w.WriteStartObject("blend");
                        w.WriteStartObject("color");
                        w.WriteString("operation", WgpuFormats.BlendOp(attachment.ColorFunction));
                        w.WriteString("srcFactor", WgpuFormats.BlendFactor(attachment.SourceColorFactor));
                        w.WriteString("dstFactor", WgpuFormats.BlendFactor(attachment.DestinationColorFactor));
                        w.WriteEndObject();
                        w.WriteStartObject("alpha");
                        w.WriteString("operation", WgpuFormats.BlendOp(attachment.AlphaFunction));
                        w.WriteString("srcFactor", WgpuFormats.BlendFactor(attachment.SourceAlphaFactor));
                        w.WriteString("dstFactor", WgpuFormats.BlendFactor(attachment.DestinationAlphaFactor));
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteStartObject("primitive");
            w.WriteString("topology", topologyName);
            w.WriteString("frontFace", WgpuFormats.FrontFace(rasterCopy.FrontFace));
            w.WriteString("cullMode", WgpuFormats.CullMode(rasterCopy.CullMode));

            // A strip topology drawn indexed needs its index format here, because the primitive
            // restart value depends on the index width. WebGPU rejects the draw without it.
            if (stripFormat != null)
                w.WriteString("stripIndexFormat", stripFormat);

            w.WriteEndObject();

            if (depthFormat.HasValue)
            {
                w.WriteStartObject("depthStencil");
                w.WriteString("format", WgpuFormats.Texture(depthFormat.Value));
                w.WriteBoolean("depthWriteEnabled", depthCopy.DepthWriteEnabled);
                w.WriteString("depthCompare", depthCopy.DepthTestEnabled
                    ? WgpuFormats.Compare(depthCopy.DepthComparison)
                    : "always");

                if (depthCopy.StencilTestEnabled && WgpuFormats.HasStencil(depthFormat.Value))
                {
                    w.WriteStartObject("stencilFront");
                    WriteStencil(w, depthCopy.StencilFront);
                    w.WriteEndObject();
                    w.WriteStartObject("stencilBack");
                    WriteStencil(w, depthCopy.StencilBack);
                    w.WriteEndObject();
                    w.WriteNumber("stencilReadMask", depthCopy.StencilReadMask);
                    w.WriteNumber("stencilWriteMask", depthCopy.StencilWriteMask);
                }

                w.WriteEndObject();
            }

            w.WriteStartObject("multisample");
            w.WriteNumber("count", sampleCount);
            w.WriteEndObject();
        });
    }


    static void WriteStencil(Utf8JsonWriter w, StencilBehaviorDescription behavior)
    {
        w.WriteString("compare", WgpuFormats.Compare(behavior.Comparison));
        w.WriteString("failOp", WgpuFormats.StencilOp(behavior.Fail));
        w.WriteString("depthFailOp", WgpuFormats.StencilOp(behavior.DepthFail));
        w.WriteString("passOp", WgpuFormats.StencilOp(behavior.Pass));
    }


    /// <summary>One colour attachment for a render pass.</summary>
    internal readonly struct ColorAttachment
    {
        public readonly int View;
        public readonly bool Clear;
        public readonly Color ClearValue;

        public ColorAttachment(int view, bool clear, Color clearValue)
        {
            View = view;
            Clear = clear;
            ClearValue = clearValue;
        }
    }


    public static string RenderPass(
        ReadOnlySpan<ColorAttachment> color,
        int depthView,
        bool clearDepth,
        float depthValue,
        bool hasStencil,
        byte stencilValue,
        string label)
    {
        ColorAttachment[] copy = color.ToArray();

        return Write(w =>
        {
            WriteLabel(w, label);
            w.WriteStartArray("colorAttachments");

            foreach (ColorAttachment attachment in copy)
            {
                w.WriteStartObject();
                w.WriteNumber("view", attachment.View);
                // Anything not being cleared has to be loaded: discarding would throw away whatever
                // an earlier pass in the same frame drew into this target.
                w.WriteString("loadOp", attachment.Clear ? "clear" : "load");
                w.WriteString("storeOp", "store");

                if (attachment.Clear)
                {
                    w.WriteStartObject("clearValue");
                    w.WriteNumber("r", attachment.ClearValue.R);
                    w.WriteNumber("g", attachment.ClearValue.G);
                    w.WriteNumber("b", attachment.ClearValue.B);
                    w.WriteNumber("a", attachment.ClearValue.A);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            if (depthView != 0)
            {
                w.WriteStartObject("depthStencilAttachment");
                w.WriteNumber("view", depthView);
                w.WriteString("depthLoadOp", clearDepth ? "clear" : "load");
                w.WriteString("depthStoreOp", "store");
                if (clearDepth)
                    w.WriteNumber("depthClearValue", depthValue);

                if (hasStencil)
                {
                    w.WriteString("stencilLoadOp", clearDepth ? "clear" : "load");
                    w.WriteString("stencilStoreOp", "store");
                    if (clearDepth)
                        w.WriteNumber("stencilClearValue", stencilValue);
                }

                w.WriteEndObject();
            }
        });
    }
}
