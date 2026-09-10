using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

using Prowl.Graphite.Wgpu;

namespace Prowl.Graphite.Samples.Web;


/// <summary>
/// Browser counterpart to <c>DeviceCreateUtilities</c>: creates a device against a canvas and drives
/// a render loop from requestAnimationFrame.
/// </summary>
/// <remarks>
/// The shape deliberately matches the desktop helper so a ported sample differs only in its entry
/// point. Two things could not match. Creation is asynchronous, because requesting a WebGPU adapter
/// and device are promises. And the loop is driven by the browser rather than owned by us, so this
/// returns as soon as the loop is armed instead of blocking until the window closes.
/// </remarks>
public static partial class WebHost
{
    private const string Module = "graphite-host";

    private static Action<double>? s_render;
    private static Action? s_close;
    private static GraphicsDevice? s_device;
    private static string s_canvasSelector = "#graphite-canvas";


    [JSImport("startLoop", Module)]
    private static partial void StartLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame);

    [JSImport("stopLoop", Module)]
    private static partial void StopLoop();

    [JSImport("syncCanvasSize", Module)]
    private static partial bool SyncCanvasSize(string selector);

    [JSImport("canvasWidth", Module)]
    private static partial int CanvasWidth(string selector);

    [JSImport("canvasHeight", Module)]
    private static partial int CanvasHeight(string selector);

    [JSImport("setStatus", Module)]
    internal static partial void SetStatus(string text);

    [JSImport("reportError", Module)]
    internal static partial void ReportError(string message);

    [JSImport("baseUrl", Module)]
    internal static partial string BaseUrl();


    /// <summary>
    /// Creates a canvas-backed device, runs <paramref name="load"/>, then drives
    /// <paramref name="render"/> once per animation frame.
    /// </summary>
    /// <param name="load">Builds the sample's resources. Awaited before the first frame.</param>
    /// <param name="render">Called once per frame with the elapsed seconds.</param>
    /// <param name="close">Called if the loop stops. Browsers rarely give us the chance.</param>
    /// <param name="options">Device options, matching the desktop samples'.</param>
    /// <param name="canvasSelector">CSS selector for the canvas to draw into.</param>
    public static async Task RunAsync(
        Func<GraphicsDevice, Task> load,
        Action<double> render,
        Action close,
        GraphicsDeviceOptions options,
        string canvasSelector = "#graphite-canvas")
    {
        s_canvasSelector = canvasSelector;
        s_render = render;
        s_close = close;

        try
        {
            await WebAssets.LoadModuleAsync(Module, "../graphite-host.js");
            SetStatus("Requesting a WebGPU device...");

            // The shim has to be in before anything asks it a question; IsSupported is the first.
            await WebGpuDevice.LoadAsync();

            if (!WebGpuDevice.IsSupported())
            {
                ReportError("This browser has no WebGPU. Chrome 113+, Edge, Firefox 141+ or Safari 26+ can run this.");
                return;
            }

            SyncCanvasSize(canvasSelector);

            SwapchainDescription swapchain = new()
            {
                DepthFormat = options.SwapchainDepthFormat,
                ColorSrgb = options.SwapchainSrgbFormat,
                Width = (uint)Math.Max(1, CanvasWidth(canvasSelector)),
                Height = (uint)Math.Max(1, CanvasHeight(canvasSelector)),
                SyncToVerticalBlank = options.SyncToVerticalBlank,
                Source = SwapchainSource.CreateCanvas(canvasSelector)
            };

            s_device = await WebGpuDevice.CreateAsync(options, swapchain);
            s_device.OnWarning = message => ReportError(message);

            SetStatus($"{s_device.DeviceName} ({s_device.VendorName})");

            await load(s_device);

            StartLoop(OnFrame);
        }
        catch (Exception e)
        {
            ReportError(e.Message);
            throw;
        }
    }


    /// <summary>The device this host created, once <see cref="RunAsync"/> has got that far.</summary>
    public static GraphicsDevice Device
        => s_device ?? throw new InvalidOperationException("The device has not been created yet.");


    private static void OnFrame(double deltaSeconds)
    {
        // The canvas follows its CSS size, so a resized window has to reach the swapchain before
        // anything draws into a stale target.
        if (SyncCanvasSize(s_canvasSelector))
        {
            s_device?.ResizeMainWindow(
                (uint)Math.Max(1, CanvasWidth(s_canvasSelector)),
                (uint)Math.Max(1, CanvasHeight(s_canvasSelector)));
        }

        s_render?.Invoke(deltaSeconds);

        // The desktop host swaps explicitly and so does this, even though WebGPU has no present
        // call: it is what releases this frame's canvas view.
        s_device?.SwapBuffers();
    }


    /// <summary>Stops the loop and runs the sample's close callback.</summary>
    public static void Stop()
    {
        StopLoop();
        s_close?.Invoke();
    }
}
