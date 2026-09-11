using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Threading.Tasks;

using Prowl.Graphite;
using Prowl.Vector;
using Prowl.Graphite.ShaderDef.Precompiled;
using Prowl.Graphite.Wgpu;

namespace InteropProof;


// Draws one triangle from C# in the browser, through WgpuInterop, with no Graphite device involved.
//
// The point is to exercise the interop boundary itself before a backend is built on top of it: every
// argument shape it uses (int handles, JSON descriptors, Span<byte> uploads, Span<int> offsets, an
// awaited Promise) and the ahead-of-time shader path that feeds it.
internal static partial class Program
{
    private const string CanvasSelector = "#graphite-canvas";

    // Mirrors GPUBufferUsage / GPUShaderStage. WebGPU has no C# enum for these, so the values are
    // spelled out once here rather than smuggled through the shim.
    private const int UsageVertex = 0x0020 | 0x0008;   // VERTEX | COPY_DST
    private const int UsageUniform = 0x0040 | 0x0008;  // UNIFORM | COPY_DST
    private const int VisibilityVertexFragment = 0x1 | 0x2;

    private static readonly List<string> s_log = [];


    [JSImport("report", "proof")]
    internal static partial void Report(bool ok, string summary, string log);

    [JSImport("baseUrl", "proof")]
    internal static partial string BaseUrl();


    static void Log(string message)
    {
        s_log.Add(message);
        Console.WriteLine(message);
    }


    static async Task Main()
    {
        try
        {
            // Module paths resolve against the runtime module inside _framework, not against the
            // page, so these step up to the app root where the two files are deployed.
            await JSHost.ImportAsync("proof", "../proof.js");
            await WgpuInterop.LoadAsync();
            Log("shim module loaded");

            await RunAsync();
            Report(true, "triangle drawn from C#, no device errors", string.Join("\n", s_log));
        }
        catch (Exception e)
        {
            Log("EXCEPTION: " + e.Message);
            Report(false, e.Message, string.Join("\n", s_log));
        }
    }


    static async Task RunAsync()
    {
        if (!WgpuInterop.IsSupported())
            throw new InvalidOperationException("navigator.gpu is undefined; this browser has no WebGPU.");

        Log("isSupported: true");

        // The manifest and its WGSL were produced on the build machine by Tools/ShaderPrecompile,
        // because Slang is native code and cannot run here.
        using HttpClient http = new() { BaseAddress = new Uri(BaseUrl()) };
        ShaderManifest manifest = ShaderManifest.FromJson(await http.GetStringAsync("Shader.shader.json"));

        Dictionary<string, byte[]> sources = [];
        foreach (ShaderManifest.StageEntry stage in manifest.Stages)
            sources[stage.File] = await http.GetByteArrayAsync(stage.File);

        ShaderDescription description = manifest.ToDescription(file => sources[file]);
        Log($"manifest rebuilt a description: {description.Stages.Length} stages, "
            + $"{description.VertexLayouts.Length} vertex layouts, {description.ResourceLayouts.Length} bind groups");

        int device = await WgpuInterop.InitializeAsync("");
        if (device == WgpuInterop.NullHandle)
            throw new InvalidOperationException("device creation failed: " + WgpuInterop.TakeError());

        Log($"device handle: {device}");

        using (JsonDocument info = JsonDocument.Parse(WgpuInterop.GetDeviceInfo()))
        {
            string vendor = info.RootElement.GetProperty("vendor").GetString() ?? "?";
            int maxBindGroups = info.RootElement.GetProperty("limits").GetProperty("maxBindGroups").GetInt32();
            Log($"adapter: {vendor}, maxBindGroups={maxBindGroups}");
        }

        string format = WgpuInterop.GetPreferredCanvasFormat();
        int context = WgpuInterop.ConfigureCanvas(CanvasSelector, format, "opaque", 400, 400);
        if (context == WgpuInterop.NullHandle)
            throw new InvalidOperationException("configureCanvas failed: " + WgpuInterop.TakeError());

        Log($"canvas context {context}, format {format}");

        // -- shader modules, from the ahead-of-time WGSL --------------------

        ShaderStageDescription vertexStage = description.Stages.First(s => s.Stage == ShaderStages.Vertex);
        ShaderStageDescription fragmentStage = description.Stages.First(s => s.Stage == ShaderStages.Fragment);

        int vs = WgpuInterop.CreateShaderModule(System.Text.Encoding.UTF8.GetString(vertexStage.ShaderBytes), "vertex");
        int fs = WgpuInterop.CreateShaderModule(System.Text.Encoding.UTF8.GetString(fragmentStage.ShaderBytes), "fragment");
        Log($"shader modules {vs}, {fs} (entry points '{vertexStage.EntryPoint}', '{fragmentStage.EntryPoint}')");

        // -- vertex buffers, one per layout --------------------------------

        float[][] streams =
        [
            [0.0f, 0.7f, 0.0f, -0.7f, -0.6f, 0.0f, 0.7f, -0.6f, 0.0f],  // POSITION0
            [0.5f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f],                        // UV0
            [1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1]                         // COLOR0
        ];

        int[] vertexBuffers = new int[streams.Length];
        for (int i = 0; i < streams.Length; i++)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(streams[i].AsSpan());
            vertexBuffers[i] = WgpuInterop.CreateBuffer(Descriptors.Buffer(bytes.Length, UsageVertex));
            WgpuInterop.WriteBuffer(vertexBuffers[i], 0, bytes);
        }

        Log("vertex buffers: " + string.Join(", ", vertexBuffers));

        // -- uniform buffer, sized from the reflected fields ----------------

        ResourceLayoutDescription set = description.ResourceLayouts[0];
        ResourceLayoutElementDescription uniformElement = set.Elements[0];
        int uniformBytes = uniformElement.UniformFields.Max(f => (int)(f.Offset + f.Size));

        float[] uniform = new float[uniformBytes / 4];
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        identity.CopyTo(uniform, 0);
        uniform[16] = uniform[17] = uniform[18] = uniform[19] = 1.0f;   // Color, white

        int uniformBuffer = WgpuInterop.CreateBuffer(Descriptors.Buffer(uniformBytes, UsageUniform));
        WgpuInterop.WriteBuffer(uniformBuffer, 0, MemoryMarshal.AsBytes(uniform.AsSpan()));
        Log($"uniform buffer {uniformBuffer}, {uniformBytes} bytes from {uniformElement.UniformFields.Length} reflected fields");

        int bindGroupLayout = WgpuInterop.CreateBindGroupLayout(
            Descriptors.BindGroupLayout(set, VisibilityVertexFragment));

        int bindGroup = WgpuInterop.CreateBindGroup(
            Descriptors.BindGroup(bindGroupLayout, uniformElement.BindingIndex, uniformBuffer));

        int pipelineLayout = WgpuInterop.CreatePipelineLayout(Descriptors.PipelineLayout(bindGroupLayout));

        Log($"bind group layout {bindGroupLayout}, bind group {bindGroup}, pipeline layout {pipelineLayout}");

        // -- pipeline -------------------------------------------------------

        int pipeline = WgpuInterop.CreateRenderPipeline(Descriptors.RenderPipeline(
            pipelineLayout,
            vs, vertexStage.EntryPoint,
            fs, fragmentStage.EntryPoint,
            description.VertexLayouts,
            format));

        Log($"render pipeline {pipeline}");

        // -- one frame ------------------------------------------------------

        int encoder = WgpuInterop.CreateCommandEncoder("frame");
        int view = WgpuInterop.GetCurrentTextureView(context);

        int pass = WgpuInterop.BeginRenderPass(encoder,
            Descriptors.RenderPass(view, new Color(0.10f, 0.12f, 0.16f, 1.0f)));

        WgpuInterop.SetPipeline(pass, pipeline);
        WgpuInterop.SetBindGroup(pass, 0, bindGroup, Span<int>.Empty);

        for (int slot = 0; slot < vertexBuffers.Length; slot++)
            WgpuInterop.SetVertexBuffer(pass, slot, vertexBuffers[slot], 0, 0);

        WgpuInterop.Draw(pass, 3, 1, 0, 0);
        WgpuInterop.EndRenderPass(pass);
        WgpuInterop.Submit(encoder);

        await WgpuInterop.OnSubmittedWorkDoneAsync();
        Log("frame submitted, queue reported completion");

        string error = WgpuInterop.TakeError();
        if (error.Length > 0)
            throw new InvalidOperationException("device reported: " + error);

        // EndRenderPass and Submit release the pass and encoder themselves; the frame view is ours.
        WgpuInterop.Release(view);
        Log($"live handles after frame: {WgpuInterop.LiveHandleCount()}");
    }
}
