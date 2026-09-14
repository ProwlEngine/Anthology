using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Prowl.Graphite.RenderGraph;
using Prowl.Graphite.Samples;
using Prowl.Graphite.Samples.Web;
using Prowl.Vector;

namespace Prowl.Graphite.Samples.Web.CubeGrid;


// The browser port of Samples/CubeGrid.
//
// This is the throughput sample: many draws per frame, each with its own PropertySet, so every draw
// crosses the interop boundary several times. It is the one that shows whether that cost is
// tolerable. The cube count is a URL parameter here rather than a constant, because what a browser
// can carry varies far more than what a desktop can.


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


public static class CubeGridScene
{
    public const float SphereRadius = 10f;


    struct CubeInstance
    {
        public PropertySet Properties;
        public Float3 Position;
        public Float3 Axis;
        public float Speed;
        public float PhaseOffset;
    }


    static Mesh sharedMesh = null!;
    static GraphicsProgram sharedPass = null!;
    static Texture texture = null!;
    static Sampler sampler = null!;

    static Float4x4 view;
    static Float4x4 projection;
    static float elapsed;

    static readonly List<CubeInstance> cubes = [];


    /// <summary>How many cubes are being drawn, for the status line.</summary>
    public static int CubeCount => cubes.Count;


    public static async Task CreateAsync(GraphicsDevice device, int cubeCount)
    {
        (texture, sampler) = await WebImageLoader.LoadAsync(device, "Cat_cat.png");
        sharedMesh = ModelLoader.CreateCube(device);

        ShaderDescription description = await WebAssets.LoadShaderAsync(device, "Shader.shader.json");
        description.BlendState = BlendStateDescription.SingleDisabled;
        description.DepthStencilState = DepthStencilStateDescription.DepthOnlyLessEqual;
        description.RasterizerState = new(FaceCullMode.Back, FrontFace.Clockwise, true, false);
        sharedPass = device.ResourceFactory.CreateGraphicsProgram(description);

        Random rng = new(1337);

        for (int x = 0; x < cubeCount; x++)
        {
            PropertySet props = new();
            props.SetTexture("MainTexture", texture, sampler);
            // WGSL has no combined texture-sampler, so the sampler is bound under its own name.
            props.SetSampler("MainSampler", sampler);
            props.SetFloat4("Color", new Float4(
                (float)rng.NextDouble(),
                (float)rng.NextDouble(),
                (float)rng.NextDouble(),
                1.0f));

            Float3 axis = Float3.Normalize(new Float3(
                (float)rng.NextDouble() * 2.0f - 1.0f,
                (float)rng.NextDouble() * 2.0f - 1.0f,
                (float)rng.NextDouble() * 2.0f - 1.0f));

            Float3 position = Float3.Normalize(new Float3(
                (float)rng.NextDouble() * 2.0f - 1.0f,
                (float)rng.NextDouble() * 2.0f - 1.0f,
                (float)rng.NextDouble() * 2.0f - 1.0f)) * SphereRadius;

            cubes.Add(new CubeInstance
            {
                Properties = props,
                Position = position,
                Axis = axis,
                Speed = 0.5f + (float)rng.NextDouble() * 2.0f,
                PhaseOffset = (float)rng.NextDouble() * MathF.PI * 2.0f,
            });
        }

        float cameraDistance = SphereRadius * 1.45f;

        projection = Float4x4.CreatePerspectiveFov(1.0472f, 1, 1f, 100);

        Float3 cameraPosition = new(cameraDistance, cameraDistance, cameraDistance);
        view = Float4x4.CreateLookAt(cameraPosition, Float3.Zero, Float3.UnitY);
    }


    public static void Draw(CommandBuffer buffer, double deltaSeconds)
    {
        elapsed += (float)deltaSeconds;

        Float4x4 viewProj = projection * view;

        buffer.SetShader(sharedPass);
        buffer.SetVertexSource(sharedMesh);

        foreach (CubeInstance cube in cubes)
        {
            Quaternion rotation = Quaternion.AxisAngle(cube.Axis, elapsed * cube.Speed + cube.PhaseOffset);
            Float4x4 model = Float4x4.CreateTRS(cube.Position, rotation, Float3.One);

            cube.Properties.SetMatrix("MatrixMVP", viewProj * model);

            buffer.SetProperties(cube.Properties);
            buffer.DrawIndexed();
        }
    }


    public static void Dispose()
    {
        sharedPass.Dispose();
        sharedMesh.Dispose();
        texture.Dispose();
        sampler.Dispose();
        cubes.Clear();
    }
}


internal sealed class CubeGridPresentPass : IPresentPass<SceneView>
{
    public string Name => "Present";

    public void Setup(PresentContextBuilder builder) => builder.RequestSwapchain();

    public void Present(RenderContext<SceneView> context)
    {
        Framebuffer? target = context.SwapchainTarget;
        if (target == null)
            return;

        CommandBuffer cmd = context.GetCommandBuffer("CubeGrid");
        cmd.SetFramebuffer(target);
        cmd.ClearDepthStencil(1, 0);
        cmd.ClearColorTarget(0, new Color(0.10f, 0.12f, 0.16f, 1.0f));
        CubeGridScene.Draw(cmd, Program.LastDelta);
        context.SubmitCommandBuffer(cmd);
        context.Present();
    }
}


internal sealed class CubeGridPipeline : RenderPipeline<SceneView>
{
    protected override void InitializePasses() => SetPresentPass(new CubeGridPresentPass());
}


public static class Program
{
    // The desktop sample draws ten thousand cubes. That is a lot of interop calls per frame, so the
    // browser default is lower and the URL can ask for more: ?cubes=10000 to match the desktop.
    private const int DefaultCubeCount = 2000;

    static GraphicsDevice device = null!;
    static WebFrameTimer timer = null!;
    static CubeGridPipeline pipeline = null!;
    static SceneView[] views = null!;

    internal static double LastDelta;


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

        await CubeGridScene.CreateAsync(device, ReadCubeCount());

        pipeline = new CubeGridPipeline();
        views = [new SceneView(600, 600)];
    }


    static int ReadCubeCount()
    {
        string value = WebHost.QueryParameter("cubes");
        return int.TryParse(value, out int count) && count > 0 ? count : DefaultCubeCount;
    }


    public static void Render(double dt)
    {
        LastDelta = dt;
        device.DispatchGraph(pipeline, views);
        timer.Frame(dt, $"{CubeGridScene.CubeCount} cubes");
    }


    public static void Close()
    {
        pipeline.Dispose();
        CubeGridScene.Dispose();
        device.Dispose();
    }
}
