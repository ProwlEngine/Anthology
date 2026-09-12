// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MonoGameSample;

// Vertex layout matching Quill's Vertex struct: float2 position, float2 uv, RGBA bytes (20 bytes).
internal struct PaperVertex : IVertexType
{
    public Vector2 Position;
    public Vector2 TexCoord;
    public Color Color;

    public static readonly VertexDeclaration Declaration = new VertexDeclaration(
        new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
        new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0));

    readonly VertexDeclaration IVertexType.VertexDeclaration => Declaration;
}
