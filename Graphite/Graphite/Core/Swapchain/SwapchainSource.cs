#if !EXCLUDE_VULKAN_BACKEND
using Silk.NET.Core.Contexts;
#endif

namespace Prowl.Graphite;

/// <summary>
/// Platform-specific renderable surface; use static factory methods to create.
/// </summary>
public abstract class SwapchainSource
{
    internal SwapchainSource() { }

#if !EXCLUDE_VULKAN_BACKEND
    /// <summary>
    /// Create Vulkan swapchain source from Silk.NET surface.
    /// </summary>
    public static SwapchainSource CreateVulkan(IVkSurface surface)
        => new VkSurfaceSwapchainSource(surface);
#endif

#if !EXCLUDE_WEBGPU_BACKEND
    /// <summary>
    /// Create WebGPU swapchain source from an HTML canvas element.
    /// </summary>
    /// <param name="canvasSelector">CSS selector for the canvas, for example "#graphite-canvas".</param>
    public static SwapchainSource CreateCanvas(string canvasSelector)
        => new WgpuCanvasSwapchainSource(canvasSelector);
#endif
}
