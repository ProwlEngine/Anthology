// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using Prowl.Quill;
using Prowl.Vector;

namespace MonoGameSample;

/// <summary>
/// Draws Quill's canvas draw calls with a MonoGame <see cref="GraphicsDevice"/> and the
/// CanvasShader effect. Backdrop blur is not implemented, so <see cref="SupportsBackdropBlur"/>
/// stays false and the demo falls back to opaque fills for frosted-glass elements.
/// </summary>
public sealed class MonoGameCanvasRenderer : ICanvasRenderer
{
    private readonly GraphicsDevice _graphicsDevice;
    private readonly Effect _effect;
    private readonly Texture2D _defaultTexture;

    // Parameters may be null if the shader compiler strips an unused uniform.
    private readonly EffectParameter? _projectionParam;
    private readonly EffectParameter? _textureSamplerParam;
    private readonly EffectParameter? _textureSizeParam;
    private readonly EffectParameter? _fontSamplerParam;
    private readonly EffectParameter? _fontSizeParam;
    private readonly EffectParameter? _scissorMatParam;
    private readonly EffectParameter? _scissorExtParam;
    private readonly EffectParameter? _brushMatParam;
    private readonly EffectParameter? _brushTypeParam;
    private readonly EffectParameter? _brushColor1Param;
    private readonly EffectParameter? _brushColor2Param;
    private readonly EffectParameter? _brushParamsParam;
    private readonly EffectParameter? _brushParams2Param;
    private readonly EffectParameter? _brushTextureMatParam;
    private readonly EffectParameter? _dpiScaleParam;

    private Matrix _projection;

    // Reused CPU-side buffers, grown as needed to avoid per-frame allocations.
    private PaperVertex[] _vertices = new PaperVertex[1024];
    private int[] _indices = new int[1024];

    public MonoGameCanvasRenderer(GraphicsDevice graphicsDevice, Effect canvasEffect)
    {
        _graphicsDevice = graphicsDevice;
        _effect = canvasEffect;

        _projectionParam = _effect.Parameters["Projection"];
        _textureSamplerParam = _effect.Parameters["TextureSampler"];
        _textureSizeParam = _effect.Parameters["TextureSize"];
        _fontSamplerParam = _effect.Parameters["FontSampler"];
        _fontSizeParam = _effect.Parameters["FontSize"];
        _scissorMatParam = _effect.Parameters["ScissorMat"];
        _scissorExtParam = _effect.Parameters["ScissorExt"];
        _brushMatParam = _effect.Parameters["BrushMat"];
        _brushTypeParam = _effect.Parameters["BrushType"];
        _brushColor1Param = _effect.Parameters["BrushColor1"];
        _brushColor2Param = _effect.Parameters["BrushColor2"];
        _brushParamsParam = _effect.Parameters["BrushParams"];
        _brushParams2Param = _effect.Parameters["BrushParams2"];
        _brushTextureMatParam = _effect.Parameters["BrushTextureMat"];
        _dpiScaleParam = _effect.Parameters["DpiScale"];

        // 1x1 white texture used when a draw call has no texture of its own.
        _defaultTexture = new(graphicsDevice, 1, 1, false, SurfaceFormat.Color);
        _defaultTexture.SetData(new byte[] { 255, 255, 255, 255 });

        UpdateProjection(graphicsDevice.Viewport.Width, graphicsDevice.Viewport.Height);
    }

    /// <summary>Rebuild the orthographic projection when the framebuffer size changes.</summary>
    public void UpdateProjection(int width, int height)
    {
        // Top-left origin, matching the pixel coordinates Paper produces.
        _projection = Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, 1);
    }

    public object CreateTexture(uint width, uint height)
    {
        Texture2D texture = new(_graphicsDevice, (int)width, (int)height, false, SurfaceFormat.Color);
        
        return texture;
    }

    public Int2 GetTextureSize(object texture)
    {
        if (texture is Texture2D tex)
        {
            return new Int2(tex.Width, tex.Height);
        }

        return Int2.Zero;
    }

    public void SetTextureData(object texture, IntRect bounds, byte[] data)
    {
        if (texture is Texture2D tex)
        {
            Rectangle rect = new(bounds.Min.X, bounds.Min.Y, bounds.Size.X, bounds.Size.Y);

            tex.SetData(0, rect, data, 0, data.Length);
        }
    }

    public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls)
    {
        if (drawCalls.Count == 0)
        {
            return;
        }

        EnsureBuffers(canvas.Vertices.Count, canvas.Indices.Count);

        for (int i = 0; i < canvas.Vertices.Count; i++)
        {
            Vertex v = canvas.Vertices[i];
            _vertices[i].Position = new(v.x, v.y);
            _vertices[i].TexCoord = new(v.u, v.v);
            _vertices[i].Color = new(v.r, v.g, v.b, v.a);
        }

        for (int i = 0; i < canvas.Indices.Count; i++)
        {
            _indices[i] = (int)canvas.Indices[i];
        }

        // Premultiplied-alpha blending (MonoGame's AlphaBlend is One / InvSrcAlpha), no depth,
        // no culling, and linear clamped sampling for both the font atlas and fill textures.
        _graphicsDevice.BlendState = BlendState.AlphaBlend;
        _graphicsDevice.DepthStencilState = DepthStencilState.None;
        _graphicsDevice.RasterizerState = RasterizerState.CullNone;
        _graphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

        int indexOffset = 0;
        foreach (DrawCall drawCall in drawCalls)
        {
            if (drawCall.Shader is Effect customEffect)
            {
                ApplyCustomEffect(customEffect, drawCall);
            }
            else
            {
                ApplyDefaultEffect(canvas, drawCall);
            }

            int primitiveCount = drawCall.ElementCount / 3;

            _graphicsDevice.DrawUserIndexedPrimitives(
                PrimitiveType.TriangleList,
                _vertices, 0, canvas.Vertices.Count,
                _indices, indexOffset, primitiveCount);

            indexOffset += drawCall.ElementCount;
        }
    }

    private void ApplyDefaultEffect(Canvas canvas, DrawCall drawCall)
    {
        _projectionParam?.SetValue(_projection);
        _dpiScaleParam?.SetValue((float)canvas.FramebufferScale);

        if (drawCall.Texture is Texture2D texture)
        {
            _textureSamplerParam?.SetValue(texture);
            _textureSizeParam?.SetValue(new Vector2(texture.Width, texture.Height));
            _graphicsDevice.SamplerStates[0] = SamplerState.LinearWrap;
        }
        else
        {
            _textureSizeParam?.SetValue(new Vector2(0, 0));
        }

        if (drawCall.FontAtlas is Texture2D fontAtlas)
        {
            _fontSamplerParam?.SetValue(fontAtlas);
            _fontSizeParam?.SetValue(new Vector2(fontAtlas.Width, fontAtlas.Height));
            _graphicsDevice.SamplerStates[1] = SamplerState.AnisotropicClamp;
        }

        drawCall.GetScissor(out Float4x4 scissor, out Float2 extent);
        _scissorMatParam?.SetValue(ToXnaTransposed(scissor));
        _scissorExtParam?.SetValue(new Vector2(extent.X, extent.Y));

        _brushTypeParam?.SetValue((float)(int)drawCall.Brush.Type);
        _brushMatParam?.SetValue(ToXnaTransposed(drawCall.Brush.BrushMatrix));
        _brushColor1Param?.SetValue(ToXna(drawCall.Brush.Color1));
        _brushColor2Param?.SetValue(ToXna(drawCall.Brush.Color2));
        _brushParamsParam?.SetValue(new Vector4(drawCall.Brush.Point1.X, drawCall.Brush.Point1.Y, drawCall.Brush.Point2.X, drawCall.Brush.Point2.Y));
        _brushParams2Param?.SetValue(new Vector2(drawCall.Brush.CornerRadii, drawCall.Brush.Feather));
        _brushTextureMatParam?.SetValue(ToXnaTransposed(drawCall.Brush.TextureMatrix));

        _effect.CurrentTechnique.Passes[0].Apply();
    }

    private void ApplyCustomEffect(Effect customEffect, DrawCall drawCall)
    {
        customEffect.Parameters["Projection"]?.SetValue(_projection);

        if (drawCall.ShaderUniforms != null)
        {
            foreach (KeyValuePair<string, object> kvp in drawCall.ShaderUniforms.Values)
            {
                if (customEffect.Parameters[kvp.Key] is EffectParameter param)
                {
                    switch (kvp.Value)
                    {
                        case float f:
                            param.SetValue(f);
                            break;

                        case int i:
                            param.SetValue(i);
                            break;

                        case Float2 v2:
                            param.SetValue(new Vector2(v2.X, v2.Y));
                            break;

                        case Float3 v3:
                            param.SetValue(new Vector3(v3.X, v3.Y, v3.Z));
                            break;

                        case Float4 v4:
                            param.SetValue(new Vector4(v4.X, v4.Y, v4.Z, v4.W));
                            break;

                        case Float4x4 mat:
                            param.SetValue(ToXna(mat));
                            break;
                    }
                }
            }
        }

        customEffect.CurrentTechnique.Passes[0].Apply();
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (_vertices.Length < vertexCount)
        {
            Array.Resize(ref _vertices, Math.Max(vertexCount, _vertices.Length * 2));
        }

        if (_indices.Length < indexCount)
        {
            Array.Resize(ref _indices, Math.Max(indexCount, _indices.Length * 2));
        }
    }

    // Copies a Quill Float4x4 (indexed [row, col]) straight into an XNA Matrix.
    private static Matrix ToXna(Float4x4 m) => new(
        (float)m[0, 0], (float)m[0, 1], (float)m[0, 2], (float)m[0, 3],
        (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], (float)m[1, 3],
        (float)m[2, 0], (float)m[2, 1], (float)m[2, 2], (float)m[2, 3],
        (float)m[3, 0], (float)m[3, 1], (float)m[3, 2], (float)m[3, 3]);

    // MonoGame's HLSL uses the row-vector convention mul(vector, matrix), so the built-in
    // canvas shader multiplies transposed matrices to reproduce the GLSL samples' mul(matrix, vector).
    private static Matrix ToXnaTransposed(Float4x4 m) => Matrix.Transpose(ToXna(m));

    private static Vector4 ToXna(Color32 c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    public void Dispose()
    {
        _defaultTexture.Dispose();
        _effect.Dispose();
    }
}
