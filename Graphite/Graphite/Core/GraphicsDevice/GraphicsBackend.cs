namespace Prowl.Graphite;

/// <summary>
/// Graphics API used by the device.
/// </summary>
public enum GraphicsBackend : byte
{
    /// <summary>
    /// Vulkan.
    /// </summary>
    Vulkan,

    /// <summary>
    /// WebGPU, running against the browser's implementation.
    /// </summary>
    WebGPU,
}
