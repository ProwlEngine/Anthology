namespace Prowl.Graphite;

public abstract partial class GraphicsDevice
{
    /// <summary>
    /// True = validation layer runs this device's checks. Set once at device creation.
    /// </summary>
    internal bool ValidationEnabled { get; set; }

    private void InitializeFrameOptions_SetValidationEnabled(in GraphicsDeviceOptions options)
    {
        ValidationEnabled = options.GraphiteValidation;
    }

    private void SyncToVerticalBlank_CheckMainSwapchain()
    {
        if (!ValidationEnabled)
            return;

        if (MainSwapchain == null)
        {
            throw new RenderException("This GraphicsDevice was created without a main Swapchain. This property cannot be set.");
        }
    }

    private void Map_CheckResource(DeviceBuffer buffer)
    {
        if (!ValidationEnabled)
            return;

        if ((buffer.Usage & BufferUsage.Dynamic) != BufferUsage.Dynamic
            && (buffer.Usage & BufferUsage.Staging) != BufferUsage.Staging)
        {
            throw new RenderException("Buffers must have the Staging or Dynamic usage flag to be mapped.");
        }
    }

    internal void UpdateTexture_CheckParameters(Texture texture, in TextureRegion region)
    {
        if (!ValidationEnabled)
            return;

        if (FormatHelpers.IsCompressedFormat(texture.Format))
        {
            // A region may end short of a whole block only where it meets the edge of the mip.
            (uint blockWidth, uint blockHeight) = FormatHelpers.GetBlockDimensions(texture.Format);
            Util.GetMipDimensions(texture, region.MipLevel, out uint mipWidth, out uint mipHeight, out _);

            bool alignedX = region.X % blockWidth == 0 && (region.Width % blockWidth == 0 || region.X + region.Width == mipWidth);
            bool alignedY = region.Y % blockHeight == 0 && (region.Height % blockHeight == 0 || region.Y + region.Height == mipHeight);
            if (!alignedX || !alignedY)
            {
                throw new RenderException($"Updates to block-compressed textures must use a region that is block-size aligned and sized.");
            }
        }
    }
}
