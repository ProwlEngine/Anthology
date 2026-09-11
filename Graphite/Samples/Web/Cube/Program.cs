using System;
using System.Threading.Tasks;

using Prowl.Graphite.RenderGraph;
using Prowl.Graphite.Samples;
using Prowl.Graphite.Samples.Web;
using Prowl.Vector;

namespace Prowl.Graphite.Samples.Web.Cube;


// The browser port of Samples/Cube.
//
// This is the first sample with a depth target and per-frame uniforms, so it is what actually
// exercises the transient uniform arena and the dynamic offsets the bind groups are built around.


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


// Holds the cube's shared resources and the one draw that uses them, matching Samples/Cube/Cube.cs.
public static class CubeScene
{
    static Mesh sharedMesh = null!;
    static GraphicsProgram sharedPass = null!;
    static Texture texture = null!;
    static Sampler sampler = null!;

    static Float4x4 view;
    static Float4x4 projection;
    static PropertySet properties = null!;


    public static async Task CreateAsync(GraphicsDevice device)
    {
        (texture, sampler) = await WebImageLoader.LoadAsync(device, "Cat_cat.png");
        sharedMesh = ModelLoader.CreateCube(device);

        ShaderDescription description = await WebAssets.LoadShaderAsync(device, "Shader.shader.json");
        description.BlendState = BlendStateDescription.SingleDisabled;
        description.DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual;
        description.RasterizerState = new(FaceCullMode.Back, FrontFace.Clockwise, true, false);
        sharedPass = device.ResourceFactory.CreateGraphicsProgram(description);

        Random rng = new(1337);

        properties = new();
        properties.SetTexture("MainTexture", texture, sampler);
        // WGSL has no combined texture-sampler, so the sampler is bound under its own name.
        properties.SetSampler("MainSampler", sampler);
        properties.SetFloat4("Color", new Float4(
            (float)rng.NextDouble(),
            (float)rng.NextDouble(),
            (float)rng.NextDouble(),
            1.0f));

        float cameraDistance = 2f;

        projection = Float4x4.CreatePerspectiveFov(1.0472f, 1, 1f, 100);

        Float3 cameraPosition = new(cameraDistance, cameraDistance, cameraDistance);
        view = Float4x4.CreateLookAt(cameraPosition, Float3.Zero, Float3.UnitY);
    }


    public static void Draw(CommandBuffer buffer)
    {
        Float4x4 viewProj = projection * view;

        buffer.SetShader(sharedPass);
        buffer.SetVertexSource(sharedMesh);

        Float4x4 model = Float4x4.CreateTRS(Float3.Zero, Quaternion.Identity, Float3.One);
        Float4x4 mvp = viewProj * model;

        properties.SetMatrix("MatrixMVP", mvp);

        buffer.SetProperties(properties);
        buffer.DrawIndexed();
    }


    public static void Dispose()
    {
        sharedPass.Dispose();
        sharedMesh.Dispose();
        texture.Dispose();
        sampler.Dispose();
    }
}


// One draw, no dependencies between passes: present clears and draws straight into the swapchain.
internal sealed class CubePresentPass : IPresentPass<SceneView>
{
    public string Name => "Present";

    public void Setup(PresentContextBuilder builder) => builder.RequestSwapchain();

    public void Present(RenderContext<SceneView> context)
    {
        Framebuffer? target = context.SwapchainTarget;
        if (target == null)
            return;

        CommandBuffer cmd = context.GetCommandBuffer("Cube");
        cmd.SetFramebuffer(target);
        cmd.ClearDepthStencil(1, 0);
        cmd.ClearColorTarget(0, new Color(0.10f, 0.12f, 0.16f, 1.0f));
        CubeScene.Draw(cmd);
        context.SubmitCommandBuffer(cmd);
        context.Present();
    }
}


internal sealed class CubePipeline : RenderPipeline<SceneView>
{
    protected override void InitializePasses() => SetPresentPass(new CubePresentPass());
}


public static class Program
{
    static GraphicsDevice device = null!;
    static WebFrameTimer timer = null!;
    static CubePipeline pipeline = null!;
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

        await CubeScene.CreateAsync(device);

        pipeline = new CubePipeline();
        views = [new SceneView(600, 600)];
    }


    public static void Render(double dt)
    {
        device.DispatchGraph(pipeline, views);
        timer.Frame(dt);
    }


    public static void Close()
    {
        pipeline.Dispose();
        CubeScene.Dispose();
        device.Dispose();
    }
}
