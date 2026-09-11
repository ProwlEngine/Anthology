using System.Threading.Tasks;

using Prowl.Graphite.RenderGraph;
using Prowl.Graphite.Samples;
using Prowl.Graphite.Samples.Web;
using Prowl.Vector;

namespace Prowl.Graphite.Samples.Web.TexturedQuad;


// The browser port of Samples/TexturedQuad.
//
// The pass and pipeline are the desktop sample's, unchanged. The entry point differs in the same
// three ways every port does, plus one more that is specific to textures: WGSL has no combined
// texture-sampler, so the shader declares the texture and the sampler separately and each is bound
// by its own name.


internal readonly struct SceneView : IRenderView
{
    public SceneView(uint width, uint height)
    {
        PixelWidth = width;
        PixelHeight = height;
    }

    public uint PixelWidth { get; }
    public uint PixelHeight { get; }
}


// Three draws sharing one shader but switching PropertySets, no dependencies between passes: present
// clears and draws straight into the swapchain.
internal sealed class TexturedQuadPresentPass : IPresentPass<SceneView>
{
    private readonly GraphicsProgram _shader;
    private readonly Mesh _leftQuad;
    private readonly Mesh _rightQuad;
    private readonly Mesh _midQuad;
    private readonly PropertySet _leftProperties;
    private readonly PropertySet _rightProperties;
    private readonly PropertySet _midProperties;

    public TexturedQuadPresentPass(
        GraphicsProgram shader,
        Mesh leftQuad, Mesh rightQuad, Mesh midQuad,
        PropertySet leftProperties, PropertySet rightProperties, PropertySet midProperties)
    {
        _shader = shader;
        _leftQuad = leftQuad;
        _rightQuad = rightQuad;
        _midQuad = midQuad;
        _leftProperties = leftProperties;
        _rightProperties = rightProperties;
        _midProperties = midProperties;
    }

    public string Name => "Present";

    public void Setup(PresentContextBuilder builder) => builder.RequestSwapchain();

    public void Present(RenderContext<SceneView> context)
    {
        Framebuffer? target = context.SwapchainTarget;
        if (target == null)
            return;

        CommandBuffer cmd = context.GetCommandBuffer("TexturedQuad");
        cmd.SetFramebuffer(target);
        cmd.ClearDepthStencil(1, 0);
        cmd.ClearColorTarget(0, new Color(0.10f, 0.12f, 0.16f, 1.0f));
        cmd.SetShader(_shader);

        cmd.SetProperties(_leftProperties);
        cmd.SetVertexSource(_leftQuad);
        cmd.DrawIndexed();

        cmd.SetProperties(_rightProperties);
        cmd.SetVertexSource(_rightQuad);
        cmd.DrawIndexed();

        cmd.SetProperties(_midProperties);
        cmd.SetVertexSource(_midQuad);
        cmd.DrawIndexed();

        context.SubmitCommandBuffer(cmd);
        context.Present();
    }
}


internal sealed class TexturedQuadPipeline : RenderPipeline<SceneView>
{
    private readonly IPresentPass<SceneView> _present;

    public TexturedQuadPipeline(IPresentPass<SceneView> present) => _present = present;

    protected override void InitializePasses() => SetPresentPass(_present);
}


public static class Program
{
    static GraphicsDevice device = null!;
    static Mesh leftQuad = null!, rightQuad = null!, midQuad = null!;
    static GraphicsProgram shader = null!;
    static PropertySet leftProperties = null!, rightProperties = null!, midProperties = null!;
    static Texture leftTexture = null!, rightTexture = null!, midTexture = null!;
    static Sampler leftSampler = null!, rightSampler = null!, midSampler = null!;
    static WebFrameTimer timer = null!;
    static TexturedQuadPipeline pipeline = null!;
    static SceneView[] views = null!;


    private static async Task Main()
    {
        GraphicsDeviceOptions options = new()
        {
            Debug = false,
            SwapchainDepthFormat = PixelFormat.D24_UNorm_S8_UInt,
            SyncToVerticalBlank = false,
            PreferStandardClipSpaceYDirection = true
        };

        await WebHost.RunAsync(Load, Render, Close, options);
    }


    public static async Task Load(GraphicsDevice newDevice)
    {
        device = newDevice;
        timer = new WebFrameTimer(newDevice);

        ShaderDescription description = await WebAssets.LoadShaderAsync(device, "Shader.shader.json");
        description.BlendState = BlendStateDescription.SingleDisabled;
        description.DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual;
        description.RasterizerState = new(FaceCullMode.Back, FrontFace.Clockwise, true, false);
        shader = device.ResourceFactory.CreateGraphicsProgram(description);

        // Two side-by-side quads, each bound to its own texture through its own PropertySet, to
        // exercise switching textures / resource sets between draws.
        leftQuad = ModelLoader.CreateQuad(device, -0.75f, -0.25f, -0.45f, 0.45f);
        rightQuad = ModelLoader.CreateQuad(device, 0.25f, 0.75f, -0.45f, 0.45f);
        midQuad = ModelLoader.CreateQuad(device, -0.25f, 0.25f, -0.45f, 0.45f);

        (leftTexture, leftSampler) = await WebImageLoader.LoadAsync(device, "Cat_cat.png");
        (rightTexture, rightSampler) = await WebImageLoader.LoadAsync(device, "Cat_cat2.png");
        (midTexture, midSampler) = await WebImageLoader.LoadAsync(device, "Cat_cat3.jpg");

        leftProperties = Bind(leftTexture, leftSampler);
        rightProperties = Bind(rightTexture, rightSampler);
        midProperties = Bind(midTexture, midSampler);

        pipeline = new TexturedQuadPipeline(new TexturedQuadPresentPass(
            shader, leftQuad, rightQuad, midQuad, leftProperties, rightProperties, midProperties));
        views = [new SceneView(600, 600)];
    }


    // The desktop sample binds one combined "MainTexture". WGSL has no such thing, so the shader
    // declares a texture and a sampler separately and both names are bound here.
    static PropertySet Bind(Texture texture, Sampler sampler)
    {
        PropertySet properties = new();
        properties.SetTexture("MainTexture", texture, sampler);
        properties.SetSampler("MainSampler", sampler);
        return properties;
    }


    public static void Render(double dt)
    {
        device.DispatchGraph(pipeline, views);
        timer.Frame(dt);
    }


    public static void Close()
    {
        pipeline.Dispose();
        leftQuad.Dispose();
        rightQuad.Dispose();
        midQuad.Dispose();
        leftTexture.Dispose();
        rightTexture.Dispose();
        midTexture.Dispose();
        leftSampler.Dispose();
        rightSampler.Dispose();
        midSampler.Dispose();
        shader.Dispose();
        device.Dispose();
    }
}
