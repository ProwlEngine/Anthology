namespace Prowl.Graphite;

public abstract partial class GraphicsDevice
{
    /// <summary>
    /// Whether the backend is supported on this system.
    /// </summary>
    /// <param name="backend">Backend to check.</param>
    /// <returns>True if supported.</returns>
    public static bool IsBackendSupported(GraphicsBackend backend)
    {
        switch (backend)
        {
            case GraphicsBackend.Vulkan:
#if !EXCLUDE_VULKAN_BACKEND
                return Vk.VkGraphicsDevice.IsSupported();
#else
                return false;
#endif
            case GraphicsBackend.WebGPU:
                // No WebGPU device exists yet; the enum member and its swapchain source landed first so
                // the rest of the library can be built and tested with Vulkan excluded.
                return false;
            default:
                throw Illegal.Value<GraphicsBackend>();
        }
    }

#if !EXCLUDE_VULKAN_BACKEND
    /// <summary>
    /// Creates a Vulkan graphics device.
    /// </summary>
    /// <param name="options">Common device properties.</param>
    /// <param name="swapchainDescription">Main swapchain to create, or null for none.</param>
    /// <param name="vkOptions">Vulkan-specific creation options.</param>
    /// <returns>A new Vulkan graphics device.</returns>
    public static GraphicsDevice CreateVulkan(
        GraphicsDeviceOptions options,
        SwapchainDescription? swapchainDescription = null,
        VulkanDeviceOptions vkOptions = default)
    {
        return new Vk.VkGraphicsDevice(options, swapchainDescription, vkOptions);
    }
#endif
}
