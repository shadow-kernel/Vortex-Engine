// Fullscreen upscale / composite pass: samples a (scaled) offscreen color RT and writes it to the full-res back
// buffer. This is the render-scale composite step (3D rendered low-res -> upscaled here -> UI overlay on top) and
// the exact slot DLSS-SR plugs into. A fullscreen triangle from SV_VertexID — no vertex buffer, no input layout.
//
// FIRST shader extracted out of C++ into a file (Stage 0 of the shader refactor). Loaded at runtime by
// DX12ShaderCompiler (compiled from this .hlsl in dev, or from a precompiled Shaders/bin/upscale.*.cso when shipped).

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

// Emits a UV; the .y flip keeps the sampled image upright (same convention as the skybox/grid passes).
VS_OUT VSMain(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// Sample the scaled source RT. Bilinear (the static sampler) gives a clean upscale; CLAMP avoids edge bleed.
Texture2D    Src : register(t0);
SamplerState Smp : register(s0);

float4 PSMain(VS_OUT i) : SV_TARGET
{
    return Src.Sample(Smp, i.uv);
}
