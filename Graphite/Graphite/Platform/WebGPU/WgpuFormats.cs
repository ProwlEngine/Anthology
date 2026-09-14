using System;

namespace Prowl.Graphite.Wgpu;


/// <summary>
/// Translates Graphite's enums into the strings and bit flags WebGPU uses.
/// </summary>
/// <remarks>
/// WebGPU names its formats and states with strings rather than integers, so these are the exact
/// spellings from the WebGPU specification. A format Graphite can express but WebGPU cannot throws
/// here rather than producing a plausible-looking wrong answer further down.
/// </remarks>
internal static class WgpuFormats
{
    // GPUBufferUsage. WebGPU exposes these only on a JS global, so the values live here as well to
    // keep the two halves of the boundary independent of each other.
    public const int BufferMapRead = 0x0001;
    public const int BufferMapWrite = 0x0002;
    public const int BufferCopySrc = 0x0004;
    public const int BufferCopyDst = 0x0008;
    public const int BufferIndex = 0x0010;
    public const int BufferVertex = 0x0020;
    public const int BufferUniform = 0x0040;
    public const int BufferStorage = 0x0080;
    public const int BufferIndirect = 0x0100;
    public const int BufferQueryResolve = 0x0200;

    // GPUTextureUsage.
    public const int TextureCopySrc = 0x01;
    public const int TextureCopyDst = 0x02;
    public const int TextureBinding = 0x04;
    public const int TextureStorageBinding = 0x08;
    public const int TextureRenderAttachment = 0x10;

    // GPUShaderStage.
    public const int StageVertex = 0x1;
    public const int StageFragment = 0x2;
    public const int StageCompute = 0x4;


    /// <summary>Shader stage mask as WebGPU visibility bits.</summary>
    public static int Visibility(ShaderStages stages)
    {
        int v = 0;
        if ((stages & ShaderStages.Vertex) != 0) v |= StageVertex;
        if ((stages & ShaderStages.Fragment) != 0) v |= StageFragment;
        if ((stages & ShaderStages.Compute) != 0) v |= StageCompute;
        return v;
    }


    /// <summary>Buffer usage flags, always including the copy destination bit so queue writes work.</summary>
    public static int BufferUsage(BufferUsage usage)
    {
        int u = BufferCopyDst | BufferCopySrc;

        if ((usage & Prowl.Graphite.BufferUsage.VertexBuffer) != 0) u |= BufferVertex;
        if ((usage & Prowl.Graphite.BufferUsage.IndexBuffer) != 0) u |= BufferIndex;
        if ((usage & Prowl.Graphite.BufferUsage.UniformBuffer) != 0) u |= BufferUniform;
        if ((usage & Prowl.Graphite.BufferUsage.StructuredBufferReadOnly) != 0) u |= BufferStorage;
        if ((usage & Prowl.Graphite.BufferUsage.StructuredBufferReadWrite) != 0) u |= BufferStorage;
        if ((usage & Prowl.Graphite.BufferUsage.IndirectBuffer) != 0) u |= BufferIndirect;

        // Staging buffers exist to be read back, which is the one case that needs a map flag.
        if ((usage & Prowl.Graphite.BufferUsage.Staging) != 0) u |= BufferMapRead;

        return u;
    }


    /// <summary>Texture usage flags.</summary>
    public static int TextureUsage(TextureUsage usage, PixelFormat format)
    {
        int u = TextureCopyDst | TextureCopySrc;

        if ((usage & Prowl.Graphite.TextureUsage.Sampled) != 0) u |= TextureBinding;
        if ((usage & Prowl.Graphite.TextureUsage.Storage) != 0) u |= TextureStorageBinding;
        if ((usage & Prowl.Graphite.TextureUsage.RenderTarget) != 0) u |= TextureRenderAttachment;
        if ((usage & Prowl.Graphite.TextureUsage.DepthStencil) != 0) u |= TextureRenderAttachment;

        // A depth format is only ever usable as an attachment, and WebGPU rejects the combination
        // with storage binding outright.
        if (IsDepthFormat(format))
            u &= ~TextureStorageBinding;

        return u;
    }


    /// <summary>Whether this format carries depth, and so must bind as a depth-stencil attachment.</summary>
    public static bool IsDepthFormat(PixelFormat format) => format switch
    {
        PixelFormat.D24_UNorm_S8_UInt => true,
        PixelFormat.D32_Float_S8_UInt => true,
        PixelFormat.R16_UNorm => false,
        PixelFormat.R32_Float => false,
        _ => false
    };


    /// <summary>Whether this format carries a stencil aspect.</summary>
    public static bool HasStencil(PixelFormat format)
        => format is PixelFormat.D24_UNorm_S8_UInt or PixelFormat.D32_Float_S8_UInt;


    /// <summary>
    /// The WebGPU name for a pixel format.
    /// </summary>
    /// <exception cref="NotSupportedException">The format has no WebGPU equivalent.</exception>
    public static string Texture(PixelFormat format) => format switch
    {
        PixelFormat.R8_UNorm => "r8unorm",
        PixelFormat.R8_SNorm => "r8snorm",
        PixelFormat.R8_UInt => "r8uint",
        PixelFormat.R8_SInt => "r8sint",

        PixelFormat.R8_G8_UNorm => "rg8unorm",
        PixelFormat.R8_G8_SNorm => "rg8snorm",
        PixelFormat.R8_G8_UInt => "rg8uint",
        PixelFormat.R8_G8_SInt => "rg8sint",

        PixelFormat.R8_G8_B8_A8_UNorm => "rgba8unorm",
        PixelFormat.R8_G8_B8_A8_UNorm_SRgb => "rgba8unorm-srgb",
        PixelFormat.R8_G8_B8_A8_SNorm => "rgba8snorm",
        PixelFormat.R8_G8_B8_A8_UInt => "rgba8uint",
        PixelFormat.R8_G8_B8_A8_SInt => "rgba8sint",

        PixelFormat.B8_G8_R8_A8_UNorm => "bgra8unorm",
        PixelFormat.B8_G8_R8_A8_UNorm_SRgb => "bgra8unorm-srgb",

        PixelFormat.R16_UInt => "r16uint",
        PixelFormat.R16_SInt => "r16sint",
        PixelFormat.R16_Float => "r16float",
        PixelFormat.R16_G16_UInt => "rg16uint",
        PixelFormat.R16_G16_SInt => "rg16sint",
        PixelFormat.R16_G16_Float => "rg16float",
        PixelFormat.R16_G16_B16_A16_UInt => "rgba16uint",
        PixelFormat.R16_G16_B16_A16_SInt => "rgba16sint",
        PixelFormat.R16_G16_B16_A16_Float => "rgba16float",

        PixelFormat.R32_UInt => "r32uint",
        PixelFormat.R32_SInt => "r32sint",
        PixelFormat.R32_Float => "r32float",
        PixelFormat.R32_G32_UInt => "rg32uint",
        PixelFormat.R32_G32_SInt => "rg32sint",
        PixelFormat.R32_G32_Float => "rg32float",
        PixelFormat.R32_G32_B32_A32_UInt => "rgba32uint",
        PixelFormat.R32_G32_B32_A32_SInt => "rgba32sint",
        PixelFormat.R32_G32_B32_A32_Float => "rgba32float",

        PixelFormat.R10_G10_B10_A2_UNorm => "rgb10a2unorm",
        PixelFormat.R10_G10_B10_A2_UInt => "rgb10a2uint",
        PixelFormat.R11_G11_B10_Float => "rg11b10ufloat",

        // WebGPU has no 24-bit depth without stencil, and depth24plus-stencil8 is the closest match
        // to what Graphite asks for here.
        PixelFormat.D24_UNorm_S8_UInt => "depth24plus-stencil8",
        PixelFormat.D32_Float_S8_UInt => "depth32float-stencil8",

        // Block-compressed formats need the texture-compression-bc feature, which the device
        // requests only when available; the names are still the spec's.
        PixelFormat.BC1_Rgba_UNorm => "bc1-rgba-unorm",
        PixelFormat.BC1_Rgba_UNorm_SRgb => "bc1-rgba-unorm-srgb",
        PixelFormat.BC2_UNorm => "bc2-rgba-unorm",
        PixelFormat.BC2_UNorm_SRgb => "bc2-rgba-unorm-srgb",
        PixelFormat.BC3_UNorm => "bc3-rgba-unorm",
        PixelFormat.BC3_UNorm_SRgb => "bc3-rgba-unorm-srgb",
        PixelFormat.BC4_UNorm => "bc4-r-unorm",
        PixelFormat.BC4_SNorm => "bc4-r-snorm",
        PixelFormat.BC5_UNorm => "bc5-rg-unorm",
        PixelFormat.BC5_SNorm => "bc5-rg-snorm",
        PixelFormat.BC7_UNorm => "bc7-rgba-unorm",
        PixelFormat.BC7_UNorm_SRgb => "bc7-rgba-unorm-srgb",

        // Everything left over has no WebGPU spelling: the 16-bit normalized formats, BC1 without
        // alpha, and the ETC2 family, which needs a feature Graphite does not model.
        _ => throw new NotSupportedException($"PixelFormat.{format} has no WebGPU equivalent.")
    };


    /// <summary>
    /// The WebGPU name for a vertex attribute format.
    /// </summary>
    /// <exception cref="NotSupportedException">The format has no WebGPU equivalent.</exception>
    public static string Vertex(VertexElementFormat format) => format switch
    {
        VertexElementFormat.Float1 => "float32",
        VertexElementFormat.Float2 => "float32x2",
        VertexElementFormat.Float3 => "float32x3",
        VertexElementFormat.Float4 => "float32x4",

        VertexElementFormat.Byte2_Norm => "unorm8x2",
        VertexElementFormat.Byte2 => "uint8x2",
        VertexElementFormat.Byte4_Norm => "unorm8x4",
        VertexElementFormat.Byte4 => "uint8x4",
        VertexElementFormat.SByte2_Norm => "snorm8x2",
        VertexElementFormat.SByte2 => "sint8x2",
        VertexElementFormat.SByte4_Norm => "snorm8x4",
        VertexElementFormat.SByte4 => "sint8x4",

        VertexElementFormat.UShort2_Norm => "unorm16x2",
        VertexElementFormat.UShort2 => "uint16x2",
        VertexElementFormat.UShort4_Norm => "unorm16x4",
        VertexElementFormat.UShort4 => "uint16x4",
        VertexElementFormat.Short2_Norm => "snorm16x2",
        VertexElementFormat.Short2 => "sint16x2",
        VertexElementFormat.Short4_Norm => "snorm16x4",
        VertexElementFormat.Short4 => "sint16x4",

        VertexElementFormat.UInt1 => "uint32",
        VertexElementFormat.UInt2 => "uint32x2",
        VertexElementFormat.UInt3 => "uint32x3",
        VertexElementFormat.UInt4 => "uint32x4",
        VertexElementFormat.Int1 => "sint32",
        VertexElementFormat.Int2 => "sint32x2",
        VertexElementFormat.Int3 => "sint32x3",
        VertexElementFormat.Int4 => "sint32x4",

        VertexElementFormat.Half1 => "float16",
        VertexElementFormat.Half2 => "float16x2",
        VertexElementFormat.Half4 => "float16x4",

        _ => throw new NotSupportedException($"VertexElementFormat.{format} has no WebGPU equivalent.")
    };


    /// <summary>Primitive topology name.</summary>
    public static string Topology(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.TriangleList => "triangle-list",
        PrimitiveTopology.TriangleStrip => "triangle-strip",
        PrimitiveTopology.LineList => "line-list",
        PrimitiveTopology.LineStrip => "line-strip",
        PrimitiveTopology.PointList => "point-list",
        _ => throw new NotSupportedException($"PrimitiveTopology.{topology} has no WebGPU equivalent.")
    };


    /// <summary>Index format name.</summary>
    public static string IndexFormat(IndexFormat format) => format switch
    {
        Prowl.Graphite.IndexFormat.UInt16 => "uint16",
        Prowl.Graphite.IndexFormat.UInt32 => "uint32",
        _ => throw new NotSupportedException($"IndexFormat.{format} has no WebGPU equivalent.")
    };


    /// <summary>Comparison function name, used by both depth tests and comparison samplers.</summary>
    public static string Compare(ComparisonKind kind) => kind switch
    {
        ComparisonKind.Never => "never",
        ComparisonKind.Less => "less",
        ComparisonKind.Equal => "equal",
        ComparisonKind.LessEqual => "less-equal",
        ComparisonKind.Greater => "greater",
        ComparisonKind.NotEqual => "not-equal",
        ComparisonKind.GreaterEqual => "greater-equal",
        ComparisonKind.Always => "always",
        _ => throw new NotSupportedException($"ComparisonKind.{kind} has no WebGPU equivalent.")
    };


    /// <summary>Stencil operation name.</summary>
    public static string StencilOp(StencilOperation op) => op switch
    {
        StencilOperation.Keep => "keep",
        StencilOperation.Zero => "zero",
        StencilOperation.Replace => "replace",
        StencilOperation.IncrementAndClamp => "increment-clamp",
        StencilOperation.DecrementAndClamp => "decrement-clamp",
        StencilOperation.Invert => "invert",
        StencilOperation.IncrementAndWrap => "increment-wrap",
        StencilOperation.DecrementAndWrap => "decrement-wrap",
        _ => throw new NotSupportedException($"StencilOperation.{op} has no WebGPU equivalent.")
    };


    /// <summary>Blend factor name.</summary>
    public static string BlendFactor(BlendFactor factor) => factor switch
    {
        Prowl.Graphite.BlendFactor.Zero => "zero",
        Prowl.Graphite.BlendFactor.One => "one",
        Prowl.Graphite.BlendFactor.SourceAlpha => "src-alpha",
        Prowl.Graphite.BlendFactor.InverseSourceAlpha => "one-minus-src-alpha",
        Prowl.Graphite.BlendFactor.DestinationAlpha => "dst-alpha",
        Prowl.Graphite.BlendFactor.InverseDestinationAlpha => "one-minus-dst-alpha",
        Prowl.Graphite.BlendFactor.SourceColor => "src",
        Prowl.Graphite.BlendFactor.InverseSourceColor => "one-minus-src",
        Prowl.Graphite.BlendFactor.DestinationColor => "dst",
        Prowl.Graphite.BlendFactor.InverseDestinationColor => "one-minus-dst",
        Prowl.Graphite.BlendFactor.BlendFactor => "constant",
        Prowl.Graphite.BlendFactor.InverseBlendFactor => "one-minus-constant",
        _ => throw new NotSupportedException($"BlendFactor.{factor} has no WebGPU equivalent.")
    };


    /// <summary>Blend equation name.</summary>
    public static string BlendOp(BlendFunction function) => function switch
    {
        BlendFunction.Add => "add",
        BlendFunction.Subtract => "subtract",
        BlendFunction.ReverseSubtract => "reverse-subtract",
        BlendFunction.Minimum => "min",
        BlendFunction.Maximum => "max",
        _ => throw new NotSupportedException($"BlendFunction.{function} has no WebGPU equivalent.")
    };


    /// <summary>Cull mode name.</summary>
    public static string CullMode(FaceCullMode mode) => mode switch
    {
        FaceCullMode.Back => "back",
        FaceCullMode.Front => "front",
        FaceCullMode.None => "none",
        _ => throw new NotSupportedException($"FaceCullMode.{mode} has no WebGPU equivalent.")
    };


    /// <summary>Front face winding name.</summary>
    public static string FrontFace(FrontFace face)
        => face == Prowl.Graphite.FrontFace.Clockwise ? "cw" : "ccw";


    /// <summary>Sampler address mode name.</summary>
    public static string AddressMode(SamplerAddressMode mode) => mode switch
    {
        SamplerAddressMode.Wrap => "repeat",
        SamplerAddressMode.Mirror => "mirror-repeat",
        SamplerAddressMode.Clamp => "clamp-to-edge",
        // WebGPU has no border colour mode; clamping to the edge is the nearest behaviour.
        SamplerAddressMode.Border => "clamp-to-edge",
        _ => throw new NotSupportedException($"SamplerAddressMode.{mode} has no WebGPU equivalent.")
    };


    /// <summary>Texture dimension name for a view or texture descriptor.</summary>
    public static string TextureDimension(TextureType type, uint arrayLayers, bool cubemap) => type switch
    {
        TextureType.Texture1D => "1d",
        TextureType.Texture3D => "3d",
        TextureType.Texture2D when cubemap => arrayLayers > 6 ? "cube-array" : "cube",
        TextureType.Texture2D => arrayLayers > 1 ? "2d-array" : "2d",
        _ => throw new NotSupportedException($"TextureType.{type} has no WebGPU equivalent.")
    };


    /// <summary>Colour write mask bits; WebGPU uses the same bit order Graphite does.</summary>
    public static int ColorWrite(ColorWriteMask mask)
    {
        int m = 0;
        if ((mask & ColorWriteMask.Red) != 0) m |= 0x1;
        if ((mask & ColorWriteMask.Green) != 0) m |= 0x2;
        if ((mask & ColorWriteMask.Blue) != 0) m |= 0x4;
        if ((mask & ColorWriteMask.Alpha) != 0) m |= 0x8;
        return m;
    }
}
