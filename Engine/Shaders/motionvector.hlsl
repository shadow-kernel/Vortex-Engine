// Motion-vector pass for DLSS Super-Resolution + Frame Generation. Reproject each pixel through inverse(curVP)
// -> world -> prevVP to find its previous-frame screen UV, and output the pixel-space velocity (prev - cur).
// Engine convention: row-major matrices, mul(vec, mat), clip.y = -ndc.y. DLSS consumes this with
// mvecScale = {1/renderW, 1/renderH} (pixel-space -> NDC). Output RT is RG16F.

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

// Fullscreen triangle from SV_VertexID (same as the upscale pass).
VS_OUT MvecVS(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

Texture2D<float> Depth : register(t0);
SamplerState     Smp   : register(s0);

cbuffer C : register(b0)
{
    row_major float4x4 InvViewProj;
    row_major float4x4 PrevViewProj;
    float2 Dims;
    float2 Pad;
};

float2 MvecPS(VS_OUT i) : SV_TARGET
{
    float d = Depth.SampleLevel(Smp, i.uv, 0);
    float2 ndc = i.uv * 2.0 - 1.0;
    float4 clip = float4(ndc.x, -ndc.y, d, 1.0);
    float4 world = mul(clip, InvViewProj);
    world /= world.w;
    float4 pc = mul(world, PrevViewProj);
    pc /= pc.w;
    float2 prevUV = float2(pc.x * 0.5 + 0.5, 0.5 - pc.y * 0.5);
    return (prevUV - i.uv) * Dims;   // pixel-space motion vector (current -> previous)
}
