using System;
using System.Buffers;
using System.Text.Json;

using Prowl.Graphite;
using Prowl.Vector;

namespace InteropProof;


// Builds the JSON descriptors the shim's cold path takes.
//
// Written with Utf8JsonWriter rather than JsonSerializer over anonymous types, because .NET
// WebAssembly disables reflection-based JSON by default: serializing an anonymous type throws
// JsonSerializerIsReflectionDisabled at runtime. A real backend wants to avoid the reflection cost
// regardless, so this is the shape it should use too.
internal static class Descriptors
{
    static string Write(Action<Utf8JsonWriter> body)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    public static string Buffer(int size, int usage) => Write(w =>
    {
        w.WriteNumber("size", size);
        w.WriteNumber("usage", usage);
    });


    public static string BindGroupLayout(ResourceLayoutDescription set, int visibility) => Write(w =>
    {
        w.WriteStartArray("entries");
        foreach (ResourceLayoutElementDescription element in set.Elements)
        {
            w.WriteStartObject();
            w.WriteNumber("binding", element.BindingIndex);
            w.WriteNumber("visibility", visibility);
            w.WriteStartObject("buffer");
            w.WriteString("type", "uniform");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });


    public static string BindGroup(int layout, int binding, int buffer) => Write(w =>
    {
        w.WriteNumber("layout", layout);
        w.WriteStartArray("entries");
        w.WriteStartObject();
        w.WriteNumber("binding", binding);
        w.WriteNumber("buffer", buffer);
        w.WriteEndObject();
        w.WriteEndArray();
    });


    public static string PipelineLayout(int bindGroupLayout) => Write(w =>
    {
        w.WriteStartArray("bindGroupLayouts");
        w.WriteNumberValue(bindGroupLayout);
        w.WriteEndArray();
    });


    public static string RenderPipeline(
        int layout,
        int vertexModule, string vertexEntryPoint,
        int fragmentModule, string fragmentEntryPoint,
        VertexLayoutDescription[] vertexLayouts,
        string colorFormat) => Write(w =>
    {
        w.WriteNumber("layout", layout);

        w.WriteStartObject("vertex");
        w.WriteNumber("module", vertexModule);
        w.WriteString("entryPoint", vertexEntryPoint);
        w.WriteStartArray("buffers");
        foreach (VertexLayoutDescription vertexLayout in vertexLayouts)
        {
            w.WriteStartObject();
            w.WriteNumber("arrayStride", vertexLayout.Stride);
            w.WriteStartArray("attributes");
            foreach (VertexElementDescription element in vertexLayout.Elements)
            {
                w.WriteStartObject();
                // Each stream carries one attribute here, and the buffer slot happens to match the
                // WGSL @location. A backend cannot assume that in general.
                w.WriteNumber("shaderLocation", vertexLayout.Location);
                w.WriteNumber("offset", element.Offset);
                w.WriteString("format", WgslVertexFormat(element.Format));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject("fragment");
        w.WriteNumber("module", fragmentModule);
        w.WriteString("entryPoint", fragmentEntryPoint);
        w.WriteStartArray("targets");
        w.WriteStartObject();
        w.WriteString("format", colorFormat);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject("primitive");
        w.WriteString("topology", "triangle-list");
        w.WriteEndObject();
    });


    public static string RenderPass(int view, Color clearColor) => Write(w =>
    {
        w.WriteStartArray("colorAttachments");
        w.WriteStartObject();
        w.WriteNumber("view", view);
        w.WriteString("loadOp", "clear");
        w.WriteString("storeOp", "store");
        w.WriteStartObject("clearValue");
        w.WriteNumber("r", clearColor.R);
        w.WriteNumber("g", clearColor.G);
        w.WriteNumber("b", clearColor.B);
        w.WriteNumber("a", clearColor.A);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    });


    public static string WgslVertexFormat(VertexElementFormat format) => format switch
    {
        VertexElementFormat.Float1 => "float32",
        VertexElementFormat.Float2 => "float32x2",
        VertexElementFormat.Float3 => "float32x3",
        VertexElementFormat.Float4 => "float32x4",
        _ => throw new NotSupportedException($"No WGSL vertex format mapped for {format}.")
    };
}
