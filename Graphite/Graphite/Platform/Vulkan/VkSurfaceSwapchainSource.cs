using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;

namespace Prowl.Graphite;

/// <summary>
/// A renderable surface backed by a Silk.NET Vulkan surface.
/// </summary>
internal class VkSurfaceSwapchainSource : SwapchainSource
{
    public IVkSurface VkSurface { get; }


    public VkSurfaceSwapchainSource(IVkSurface surface)
    {
        VkSurface = surface;
    }


    internal unsafe SurfaceKHR GetSurface(Instance instance)
    {
        return VkSurface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
    }
}
