// HLSL port of the shared Canvas GLSL shader for the MonoGame (DesktopGL) sample.
// Mirrors Samples/Common/CanvasShaderSource.cs: scissor masking, brush gradients,
// SDF text and edge anti-aliasing. Backdrop blur is not implemented here
// (the renderer reports SupportsBackdropBlur = false).

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_5_0
    #define PS_SHADERMODEL ps_5_0
#endif

// Orthographic projection (pixels -> clip space).
float4x4 Projection;

// texture0 is bound on sampler slot 0 by the renderer.
sampler2D TextureSampler : register(s0);
// font texture is bound on sampler slot 1 by the renderer.
sampler2D FontSampler : register(s1);

// Size of texture0 in texels; needed by the SDF text screen-range calculation.
float2 TextureSize;
float2 FontSize;

// Scissor rectangle (in logical units), passed as an inverse transform + half-extent.
float4x4 ScissorMat;
float2 ScissorExt;

// Brush / gradient parameters.
float4x4 BrushMat;
float BrushType;        // 0=none, 1=linear, 2=radial, 3=box
float4 BrushColor1;
float4 BrushColor2;
float4 BrushParams;
float2 BrushParams2;

// Texture-fill transform (inverse) mapping logical position -> texture UV.
float4x4 BrushTextureMat;

// Pixels per logical unit.
float DpiScale;

// Width of the signed-distance range in atlas texels. Must match Scribe's FontSystem.DistanceRange.
static const float sdfPxRange = 4.0;

struct VSInput
{
    float2 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
    float4 Color    : COLOR0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float4 Color    : COLOR0;
    float2 FragPos  : TEXCOORD1;
};

PSInput MainVS(VSInput input)
{
    PSInput output;
    output.Position = mul(float4(input.Position, 0.0, 1.0), Projection);
    output.TexCoord = input.TexCoord;
    output.Color = input.Color;
    output.FragPos = input.Position;
    return output;
}

float calculateBrushFactor(float2 fragPos)
{
    if (BrushType < 0.5) return 0.0;

    float2 logicalPos = fragPos / DpiScale;
    // Matrices are uploaded transposed so mul(vector, matrix) (XNA row-vector convention) matches
    // the GLSL samples' mul(matrix, vector).
    float2 transformedPoint = mul(float4(logicalPos, 0.0, 1.0), BrushMat).xy;

    // Linear brush.
    if (BrushType < 1.5)
    {
        float2 startPoint = BrushParams.xy;
        float2 endPoint = BrushParams.zw;
        float2 lineDir = endPoint - startPoint;
        float lineLength = length(lineDir);
        if (lineLength < 0.001) return 0.0;
        return clamp(dot(transformedPoint - startPoint, lineDir) / (lineLength * lineLength), 0.0, 1.0);
    }

    // Radial brush.
    if (BrushType < 2.5)
    {
        float2 center = BrushParams.xy;
        return clamp(smoothstep(BrushParams.z, BrushParams.w, length(transformedPoint - center)), 0.0, 1.0);
    }

    // Box brush.
    float2 boxCenter = BrushParams.xy;
    float2 halfSize = BrushParams.zw;
    float radius = BrushParams2.x;
    float feather = BrushParams2.y;
    if (halfSize.x < 0.001 || halfSize.y < 0.001) return 0.0;
    float2 q = abs(transformedPoint - boxCenter) - (halfSize - float2(radius, radius));
    float dist = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
    // Use smoothstep to match GLSL and avoid NaN when feather == 0.
    return smoothstep(-feather * 0.5, feather * 0.5, dist);
}

float scissorMask(float2 p)
{
    if (ScissorExt.x < 0.0 || ScissorExt.y < 0.0) return 1.0;

    float2 logicalP = p / DpiScale;
    float2 transformedPoint = mul(float4(logicalP, 0.0, 1.0), ScissorMat).xy;
    float2 logicalExt = ScissorExt / DpiScale;
    float2 distanceFromEdges = abs(transformedPoint) - logicalExt;
    float halfPixelLogical = 0.5 / DpiScale;
    float2 smoothEdges = float2(halfPixelLogical, halfPixelLogical) - distanceFromEdges;
    return clamp(smoothEdges.x, 0.0, 1.0) * clamp(smoothEdges.y, 0.0, 1.0);
}

// Screen-space width (in pixels) that one distance-field unit spans at this fragment.
float sdfScreenPxRange(float2 uv)
{
    float2 unitRange = float2(sdfPxRange, sdfPxRange) / FontSize;
    float2 screenTexSize = float2(1.0, 1.0) / fwidth(uv);
    return max(0.5 * dot(unitRange, screenTexSize), 1.0);
}

float4 MainPS(PSInput input) : COLOR0
{
    float2 fragPos = input.FragPos;
    float mask = scissorMask(fragPos);    

    float4 color = input.Color;

    if (BrushType > 0.5)
    {
        float factor = calculateBrushFactor(fragPos);
        color = lerp(BrushColor1, BrushColor2, factor);
    }

    // Text mode: UV >= 2.0 means SDF text rendering.
    if (input.TexCoord.x >= 2.0)
    {
        float2 uv = input.TexCoord - float2(2.0, 2.0);
        float sd = tex2D(FontSampler, uv).r;
        float screenPxDistance = sdfScreenPxRange(uv) * (sd - 0.5);
        float coverage = clamp(screenPxDistance + 0.5, 0.0, 1.0);
        return color * coverage * mask;
    }

    // Edge anti-aliasing: coverage is baked into the geometry and carried in TexCoord.x
    // (1 = solid core, 0 = outer fringe edge). Matches the GLSL reference shader.
    float edgeAlpha = clamp(input.TexCoord.x, 0.0, 1.0);

    float2 logicalPos = fragPos / DpiScale;
    float2 fillUV = mul(float4(logicalPos, 0.0, 1.0), BrushTextureMat).xy;
    float4 fill = color;

    if (TextureSize.x > 0.0 && TextureSize.y > 0.0)
    {
        fill = color * tex2D(TextureSampler, fillUV);
    }
    
    return fill * edgeAlpha * mask;
}

technique Canvas
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
