using System.Threading.Tasks;

using Prowl.Graphite.RenderGraph;
using Prowl.Graphite.Samples;
using Prowl.Graphite.Samples.Web;
using Prowl.Vector;

namespace Prowl.Graphite.Samples.Web.HelloTriangle;


// The browser port of Samples/HelloTriangle.
//
// Everything from SceneView down to TrianglePipeline is copied from the desktop sample without a
// change, because the render graph does not know or care which backend is under it. Only the entry
// point differs, and only where the browser forces it: the device arrives asynchronously, the shader
// was compiled during the build rather than at startup, and the browser owns the frame loop.


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


// The whole demo is one draw, so it needs no offscreen passes to order or textures to share between
// passes: the present pass alone clears and draws straight into the swapchain target.
internal sealed class TrianglePresentPass : IPresentPass<SceneView>
{
    private readonly Mesh _triangle;
    private readonly GraphicsProgram _shader;

    public TrianglePresentPass(Mesh triangle, GraphicsProgram shader)
    {
        _triangle = triangle;
        _shader = shader;
    }

    public string Name => "Present";

    public void Setup(PresentContextBuilder builder) => builder.RequestSwapchain();

    public void Present(RenderContext<SceneView> context)
    {
        Framebuffer? target = context.SwapchainTarget;
        if (target == null)
            return;

        CommandBuffer cmd = context.GetCommandBuffer("Triangle");
        cmd.SetFramebuffer(target);
        cmd.ClearDepthStencil(1, 0);
        cmd.ClearColorTarget(0, new Color(0.10f, 0.12f, 0.16f, 1.0f));
        cmd.SetShader(_shader);
        cmd.SetVertexSource(_triangle);
        cmd.DrawIndexed();
        context.SubmitCommandBuffer(cmd);
        context.Present();
    }
}


internal sealed class TrianglePipeline : RenderPipeline<SceneView>
{
    private readonly IPresentPass<SceneView> _present;

    public TrianglePipeline(IPresentPass<SceneView> present) => _present = present;

    protected override void InitializePasses() => SetPresentPass(_present);
}


public static class Program
{
    static GraphicsDevice device = null!;
    static Mesh triangle = null!;
    static GraphicsProgram shader = null!;
    static WebFrameTimer timer = null!;
    static TrianglePipeline pipeline = null!;
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

        // The desktop sample compiles Shader.slang here. In the browser the build already did it, so
        // this loads the generated WGSL and its reflected bindings instead. The fixed-function state
        // below is exactly what the desktop loader applies after compiling.
        ShaderDescription description = await WebAssets.LoadShaderAsync(device, "Shader.shader.json");
        description.BlendState = BlendStateDescription.SingleDisabled;
        description.DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual;
        description.RasterizerState = new(FaceCullMode.Back, FrontFace.Clockwise, true, false);

        shader = device.ResourceFactory.CreateGraphicsProgram(description);
        triangle = ModelLoader.CreateTriangle(device);

        pipeline = new TrianglePipeline(new TrianglePresentPass(triangle, shader));
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
        triangle.Dispose();
        shader.Dispose();
        device.Dispose();
    }
}
