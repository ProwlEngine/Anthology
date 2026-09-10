using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace Prowl.Graphite.ShaderDef.Precompiled;


/// <summary>
/// A <see cref="ShaderDescription"/> flattened to data, so a target that cannot run the Slang
/// compiler can still load a shader that was compiled ahead of time.
/// </summary>
/// <remarks>
/// The WebGPU backend is the reason this exists: Slang is a native library and cannot run inside
/// WebAssembly, so shaders are compiled on the build machine and the browser loads the generated
/// source plus this manifest instead. Nothing here is WebGPU-specific, and the stage source is kept
/// in separate sibling files so generated WGSL stays readable and diffable.
/// </remarks>
public sealed class ShaderManifest
{
    /// <summary>Manifest format version, bumped when the shape below changes incompatibly.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Format version this manifest was written with.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Backend the stage source was generated for.</summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary>Shader stages, each pointing at its own source file.</summary>
    public List<StageEntry> Stages { get; set; } = [];

    /// <summary>Vertex input layouts, one per vertex buffer.</summary>
    public List<VertexLayoutEntry> VertexLayouts { get; set; } = [];

    /// <summary>Resource layouts, one per descriptor set / bind group.</summary>
    public List<ResourceLayoutEntry> ResourceLayouts { get; set; } = [];


    /// <summary>One compiled stage and the file holding its source.</summary>
    public sealed class StageEntry
    {
        /// <summary>Which stage this is.</summary>
        public ShaderStages Stage { get; set; }

        /// <summary>Entry point function name within the source file.</summary>
        public string EntryPoint { get; set; } = string.Empty;

        /// <summary>Source file name, relative to the manifest.</summary>
        public string File { get; set; } = string.Empty;
    }


    /// <summary>One vertex buffer's layout.</summary>
    public sealed class VertexLayoutEntry
    {
        /// <summary>Buffer binding slot.</summary>
        public uint Location { get; set; }

        /// <summary>Byte stride between vertices, 0 to derive from the elements.</summary>
        public uint Stride { get; set; }

        /// <summary>Instance step rate, 0 for per-vertex data.</summary>
        public uint InstanceStepRate { get; set; }

        /// <summary>Attributes in this buffer.</summary>
        public List<VertexElementEntry> Elements { get; set; } = [];
    }


    /// <summary>One vertex attribute.</summary>
    public sealed class VertexElementEntry
    {
        /// <summary>Attribute name, re-interned on load.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Attribute format.</summary>
        public VertexElementFormat Format { get; set; }

        /// <summary>Byte offset within the vertex.</summary>
        public uint Offset { get; set; }
    }


    /// <summary>One descriptor set, which becomes a WebGPU bind group.</summary>
    public sealed class ResourceLayoutEntry
    {
        /// <summary>Set index, the "@group" in WGSL.</summary>
        public uint Set { get; set; }

        /// <summary>Bindings in this set.</summary>
        public List<ResourceElementEntry> Elements { get; set; } = [];
    }


    /// <summary>One binding within a set.</summary>
    public sealed class ResourceElementEntry
    {
        /// <summary>Binding name, re-interned on load.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>What kind of resource binds here.</summary>
        public ResourceKind Kind { get; set; }

        /// <summary>Stages that read this binding.</summary>
        public ShaderStages Stages { get; set; }

        /// <summary>Binding index, the "@binding" in WGSL.</summary>
        public int Binding { get; set; }

        /// <summary>Loose uniform fields, when this binding is a uniform buffer.</summary>
        public List<UniformFieldEntry> Fields { get; set; } = [];
    }


    /// <summary>One field inside a uniform buffer.</summary>
    public sealed class UniformFieldEntry
    {
        /// <summary>Field name, re-interned on load.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Byte offset within the buffer.</summary>
        public uint Offset { get; set; }

        /// <summary>Byte size.</summary>
        public uint Size { get; set; }

        /// <summary>Scalar type used for writes.</summary>
        public UniformScalarType Type { get; set; }
    }


    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };


    /// <summary>
    /// Flattens a compiled description into a manifest. Stage source is returned separately so the
    /// caller decides where it lands.
    /// </summary>
    /// <param name="description">The compiled description to flatten.</param>
    /// <param name="backend">Backend the stage source was generated for.</param>
    /// <param name="shaderName">Base name used to build each stage's source file name.</param>
    /// <param name="sourceExtension">Extension for the generated source files, "wgsl" for WebGPU.</param>
    /// <returns>The manifest, plus each stage's file name and bytes.</returns>
    public static (ShaderManifest Manifest, List<(string File, byte[] Bytes)> Sources) FromDescription(
        in ShaderDescription description,
        GraphicsBackend backend,
        string shaderName,
        string sourceExtension)
    {
        ShaderManifest manifest = new() { Backend = backend.ToString() };
        List<(string, byte[])> sources = [];

        foreach (ShaderStageDescription stage in description.Stages)
        {
            string file = $"{shaderName}.{stage.Stage.ToString().ToLowerInvariant()}.{sourceExtension}";
            manifest.Stages.Add(new StageEntry { Stage = stage.Stage, EntryPoint = stage.EntryPoint, File = file });
            sources.Add((file, stage.ShaderBytes));
        }

        foreach (VertexLayoutDescription layout in description.VertexLayouts ?? [])
        {
            manifest.VertexLayouts.Add(new VertexLayoutEntry
            {
                Location = layout.Location,
                Stride = layout.Stride,
                InstanceStepRate = layout.InstanceStepRate,
                Elements = [.. (layout.Elements ?? []).Select(e => new VertexElementEntry
                {
                    Name = VertexAttributeID.ToString(e.Name) ?? string.Empty,
                    Format = e.Format,
                    Offset = e.Offset
                })]
            });
        }

        foreach (ResourceLayoutDescription layout in description.ResourceLayouts ?? [])
        {
            manifest.ResourceLayouts.Add(new ResourceLayoutEntry
            {
                Set = layout.Set,
                Elements = [.. (layout.Elements ?? []).Select(e => new ResourceElementEntry
                {
                    Name = PropertyID.ToString(e.Name) ?? string.Empty,
                    Kind = e.Kind,
                    Stages = e.Stages,
                    Binding = e.BindingIndex,
                    Fields = [.. (e.UniformFields ?? []).Select(f => new UniformFieldEntry
                    {
                        Name = PropertyID.ToString(f.Name) ?? string.Empty,
                        Offset = f.Offset,
                        Size = f.Size,
                        Type = f.Type
                    })]
                })]
            });
        }

        return (manifest, sources);
    }


    /// <summary>
    /// Rebuilds a description from this manifest. Fixed-function pipeline state is not stored here,
    /// so the caller still sets blend, depth-stencil and rasterizer state as it does after compiling.
    /// </summary>
    /// <param name="sourceLoader">Resolves a stage's file name to its source bytes.</param>
    /// <returns>The rebuilt description.</returns>
    public ShaderDescription ToDescription(Func<string, byte[]> sourceLoader)
    {
        ArgumentNullException.ThrowIfNull(sourceLoader);

        if (Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Shader manifest is version {Version}, but this build reads version {CurrentVersion}. Recompile the shader.");
        }

        ShaderStageDescription[] stages = [.. Stages.Select(s => new ShaderStageDescription
        {
            Stage = s.Stage,
            EntryPoint = s.EntryPoint,
            ShaderBytes = sourceLoader(s.File),
            Debug = false
        })];

        VertexLayoutDescription[] vertexLayouts = [.. VertexLayouts.Select(l => new VertexLayoutDescription
        {
            Location = l.Location,
            Stride = l.Stride,
            InstanceStepRate = l.InstanceStepRate,
            Elements = [.. l.Elements.Select(e => new VertexElementDescription(e.Name, e.Format, e.Offset))]
        })];

        ResourceLayoutDescription[] resourceLayouts = [.. ResourceLayouts.Select(l => new ResourceLayoutDescription(
            l.Set,
            [.. l.Elements.Select(e => new ResourceLayoutElementDescription(
                e.Name,
                e.Kind,
                e.Stages,
                e.Binding,
                ResourceLayoutElementOptions.None,
                e.Name,
                [.. e.Fields.Select(f => new UniformBlockField(f.Name, f.Offset, f.Size, f.Type))]))]))];

        return new ShaderDescription
        {
            Stages = stages,
            VertexLayouts = vertexLayouts,
            ResourceLayouts = resourceLayouts
        };
    }


    /// <summary>Serializes this manifest to indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_json);


    /// <summary>Deserializes a manifest from JSON.</summary>
    /// <param name="json">JSON produced by <see cref="ToJson"/>.</param>
    public static ShaderManifest FromJson(string json)
        => JsonSerializer.Deserialize<ShaderManifest>(json, s_json)
           ?? throw new InvalidOperationException("Shader manifest JSON deserialized to null.");
}
