namespace Prowl.Graphite;

/// <summary>
/// A renderable surface backed by an HTML canvas element, identified by a CSS selector.
/// </summary>
/// <remarks>
/// The canvas is looked up and configured by the WebGPU backend at swapchain creation time, so the
/// element only has to exist by then, not when this source is built.
/// </remarks>
internal class WgpuCanvasSwapchainSource : SwapchainSource
{
    /// <summary>CSS selector identifying the canvas element, for example "#graphite-canvas".</summary>
    public string CanvasSelector { get; }


    public WgpuCanvasSwapchainSource(string canvasSelector)
    {
        CanvasSelector = canvasSelector;
    }
}
