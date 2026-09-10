using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Prowl.Graphite.Samples.Web;


/// <summary>
/// Browser counterpart to <c>ImageLoader</c>: the browser decodes the image instead of Magick.NET.
/// </summary>
/// <remarks>
/// The desktop loader flips the image on load, because Vulkan and the sample shaders disagree about
/// which way up a texture is. The same flip happens here, in <c>createImageBitmap</c>, which is why
/// the decoded rows arrive bottom-up like the desktop ones.
/// </remarks>
public static partial class WebImageLoader
{
    private const string Module = "graphite-host";


    [JSImport("decodeImage", Module)]
    private static partial Task<JSObject> DecodeImageAsync(string url);

    [JSImport("imageWidth", Module)]
    private static partial int ImageWidth(JSObject image);

    [JSImport("imageHeight", Module)]
    private static partial int ImageHeight(JSObject image);

    [JSImport("copyImagePixels", Module)]
    private static partial void CopyImagePixels(
        JSObject image,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);


    /// <summary>
    /// Decodes an image and uploads it as a sampled texture, with a linear sampler beside it.
    /// </summary>
    /// <param name="device">Device to create the texture on.</param>
    /// <param name="url">Where the image is served from, relative to the page.</param>
    /// <returns>The texture and a sampler to read it through.</returns>
    public static async Task<(Texture Texture, Sampler Sampler)> LoadAsync(GraphicsDevice device, string url)
    {
        using JSObject image = await DecodeImageAsync(url);

        uint width = (uint)ImageWidth(image);
        uint height = (uint)ImageHeight(image);

        // Always four bytes per pixel: the decode path goes through a 2D canvas, which is RGBA.
        byte[] pixels = new byte[width * height * 4];
        CopyImagePixels(image, pixels);

        TextureDescription description = TextureDescription.Texture2D(
            width, height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);

        Texture texture = device.ResourceFactory.CreateTexture(description);
        device.UpdateTexture(texture, pixels, 0, 0, 0, width, height, 1, 0, 0);

        Sampler sampler = device.ResourceFactory.CreateSampler(SamplerDescription.Linear);

        return (texture, sampler);
    }
}


/// <summary>
/// Browser counterpart to <c>RenderMSTracker</c>.
/// </summary>
/// <remarks>
/// The desktop tracker redraws in place using console cursor positioning, which means nothing in a
/// browser. This keeps the same smoothing and reports into the page instead.
/// </remarks>
public sealed class WebFrameTimer
{
    private const float Smoothing = 0.05f;

    private readonly GraphicsDevice _device;
    private double _smoothedDelta = 0.0001;
    private double _sinceReport;


    /// <summary>Creates a timer that reports the device it is measuring.</summary>
    public WebFrameTimer(GraphicsDevice device) => _device = device;


    /// <summary>Folds one frame's delta into the running average and updates the status line.</summary>
    /// <param name="deltaSeconds">Seconds since the previous frame.</param>
    public void Frame(double deltaSeconds)
    {
        _smoothedDelta += (deltaSeconds - _smoothedDelta) * Smoothing;
        _sinceReport += deltaSeconds;

        // Four updates a second: often enough to read, rarely enough not to churn the DOM.
        if (_sinceReport < 0.25)
            return;

        _sinceReport = 0;

        double milliseconds = _smoothedDelta * 1000.0;
        double fps = _smoothedDelta > 0 ? 1.0 / _smoothedDelta : 0;

        WebHost.SetStatus($"{_device.DeviceName} ({_device.VendorName}) — {milliseconds:F2} ms, {fps:F0} fps");
    }
}
