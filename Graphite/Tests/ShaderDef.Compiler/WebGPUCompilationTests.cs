using System;
using System.Linq;
using System.Text;

using Prowl.Graphite.ShaderDef.Precompiled;

using Xunit;

namespace Prowl.Graphite.ShaderDef.Compiler.Tests;


// Compiles the shared shader suite to WGSL and asserts the pieces the WebGPU backend depends on:
// entry point names survive (unlike SPIR-V, which renames everything to "main"), descriptor sets map
// onto @group and bindings onto @binding, and constructs WGSL cannot express fail with a message
// rather than taking the process down with them.
public class WebGPUCompilationTests
{
    const ShaderStages VF = ShaderStages.Vertex | ShaderStages.Fragment;

    static ShaderDescription Compile(string module) =>
        CompilerTestHarness.CompileShared(module, () => new WebGPUCompiler()).Backends[0].Description;

    static string StageText(ShaderDescription d, ShaderStages stage)
        => Encoding.UTF8.GetString(CompilerTestHarness.StageOf(d, stage).ShaderBytes);


    [Fact]
    public void Graphics_KeepsSlangEntryPointNames()
    {
        ReflectionTestbed.AssertStages(Compile("Graphics"),
            (ShaderStages.Vertex, "vertex"), (ShaderStages.Fragment, "fragment"));
    }


    [Fact]
    public void Graphics_VertexInputsAtExpectedLocations()
    {
        ReflectionTestbed.AssertVertexLocations(Compile("Graphics"),
            (0, VertexElementFormat.Float3), (1, VertexElementFormat.Float2), (2, VertexElementFormat.Float4));
    }


    [Theory]
    [InlineData("Graphics")]
    [InlineData("Modules")]
    [InlineData("ConstantBuffers")]
    [InlineData("ParameterBlocks")]
    [InlineData("UVOriginUsage")]
    public void EmitsWgslForBothStages(string module)
    {
        ShaderDescription d = Compile(module);

        foreach (ShaderStages stage in new[] { ShaderStages.Vertex, ShaderStages.Fragment })
        {
            string wgsl = StageText(d, stage);
            Assert.False(string.IsNullOrWhiteSpace(wgsl));
            Assert.Contains(stage == ShaderStages.Vertex ? "@vertex" : "@fragment", wgsl);
        }
    }


    // Every binding the reflection reports has to exist in the emitted WGSL at the same group and
    // binding, or the bind group layouts the backend builds will not match the shader.
    [Theory]
    [InlineData("ConstantBuffers")]
    [InlineData("ParameterBlocks")]
    public void ReflectedBindingsAppearInWgsl(string module)
    {
        ShaderDescription d = Compile(module);
        string all = StageText(d, ShaderStages.Vertex) + StageText(d, ShaderStages.Fragment);

        Assert.NotEmpty(d.ResourceLayouts);

        foreach (ResourceLayoutDescription set in d.ResourceLayouts)
        {
            Assert.True(set.Set < WebGPUCompiler.MaxBindGroups);

            foreach (ResourceLayoutElementDescription element in set.Elements)
            {
                Assert.Contains($"@binding({element.BindingIndex}) @group({set.Set})", all);

                // WGSL has no combined texture-sampler, so reflection must never claim one.
                Assert.NotEqual(ResourceLayoutElementOptions.CombinedImageSampler, element.Options);
            }
        }
    }


    [Fact]
    public void ParameterBlocks_EachBlockGetsItsOwnGroup()
    {
        ShaderDescription d = Compile("ParameterBlocks");

        uint[] sets = [.. d.ResourceLayouts.Select(l => l.Set).OrderBy(s => s)];

        Assert.Equal([0u, 1u, 2u], sets);
    }


    // Slang 2026.12 segfaults when it emits a combined Sampler2D to WGSL, so the compiler module
    // rejects the declaration during reflection instead. Losing the test process is the failure this
    // guards against, which is why it is worth a test of its own.
    [Fact]
    public void CombinedSampler_IsRejectedWithAName()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Compile("CombinedSampler"));

        Assert.Contains("Combined", e.Message);
        Assert.Contains("Texture2D", e.Message);
    }


    [Theory]
    [InlineData("Graphics")]
    [InlineData("ParameterBlocks")]
    public void ManifestRoundTripsTheDescription(string module)
    {
        ShaderDescription original = Compile(module);

        (ShaderManifest manifest, var sources) =
            ShaderManifest.FromDescription(original, GraphicsBackend.WebGPU, module, "wgsl");

        // Go through JSON so the test covers what actually lands on disk, not just the object graph.
        ShaderManifest reloaded = ShaderManifest.FromJson(manifest.ToJson());
        ShaderDescription rebuilt = reloaded.ToDescription(
            file => sources.First(s => s.File == file).Bytes);

        ReflectionTestbed.AssertStages(rebuilt,
            [.. original.Stages.Select(s => (s.Stage, s.EntryPoint))]);

        Assert.Equal(original.ResourceLayouts, rebuilt.ResourceLayouts);
        Assert.Equal(original.VertexLayouts, rebuilt.VertexLayouts);

        for (int i = 0; i < original.Stages.Length; i++)
            Assert.Equal(original.Stages[i].ShaderBytes, rebuilt.Stages[i].ShaderBytes);
    }
}
