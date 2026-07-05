// Bloom (#30) — the multi-pass half of the effect: soft-knee bright-pass, progressive 13-tap
// downsample chain and additive 9-tap tent upsample, recorded by DX12PostFxChain BEFORE the
// postfx uber-pass (which does the final two-texture composite: scene t0 + bloom t1, flag 32).
// Three pixel entry points share this file and the postfx root signature:
//   PSPrefilter   scene color -> half-res mip 0: 13-tap downsample + quadratic soft-knee threshold
//   PSDownsample  mip N-1 -> mip N: 13-tap (Jimenez) — filtered enough that thin bright features
//                 don't flicker under camera motion (the anti-shimmer acceptance criterion)
//   PSUpsample    mip N -> mip N-1: 3x3 tent, ADDITIVE (the PSO blends ONE/ONE); Weight is the
//                 scatter dial — how much each coarser mip bleeds back into the finer one
//
// The BloomCB cbuffer is byte-matched to DX12PostFxChain::BloomCB — keep both in sync.

cbuffer BloomCB : register(b0)
{
    float2 SrcTexel;     // 1 / size of the texture bound at t0 (the SOURCE level)
    float  Threshold;    // prefilter: brightness where glow starts (SDR scene -> 0..1 useful)
    float  Knee;         // prefilter: soft-knee width below the threshold (0 = hard cut)
    float  SampleScale;  // upsample: tent radius in source texels
    float  Weight;       // upsample: additive weight (scatter)
    float2 _padB;
};

Texture2D    Src : register(t0);
SamplerState Smp : register(s0);

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

// Fullscreen triangle from SV_VertexID — same trick as postfx/upscale (no VB, no input layout).
VS_OUT VSMain(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// 13-tap downsample (Jimenez, SIGGRAPH 2014 "Next Generation Post Processing in COD:AW"):
// four overlapping 4-tap boxes on the outer 3x3 grid (each 0.125) + one inner half-texel box
// (0.5) — the partial overlap suppresses the fireflies/pulsing a plain box average shows.
float3 Down13(float2 uv)
{
    float3 a = Src.Sample(Smp, uv + SrcTexel * float2(-1.0, -1.0)).rgb;
    float3 b = Src.Sample(Smp, uv + SrcTexel * float2( 0.0, -1.0)).rgb;
    float3 c = Src.Sample(Smp, uv + SrcTexel * float2( 1.0, -1.0)).rgb;
    float3 d = Src.Sample(Smp, uv + SrcTexel * float2(-0.5, -0.5)).rgb;
    float3 e = Src.Sample(Smp, uv + SrcTexel * float2( 0.5, -0.5)).rgb;
    float3 f = Src.Sample(Smp, uv + SrcTexel * float2(-1.0,  0.0)).rgb;
    float3 g = Src.Sample(Smp, uv).rgb;
    float3 h = Src.Sample(Smp, uv + SrcTexel * float2( 1.0,  0.0)).rgb;
    float3 i = Src.Sample(Smp, uv + SrcTexel * float2(-0.5,  0.5)).rgb;
    float3 j = Src.Sample(Smp, uv + SrcTexel * float2( 0.5,  0.5)).rgb;
    float3 k = Src.Sample(Smp, uv + SrcTexel * float2(-1.0,  1.0)).rgb;
    float3 l = Src.Sample(Smp, uv + SrcTexel * float2( 0.0,  1.0)).rgb;
    float3 m = Src.Sample(Smp, uv + SrcTexel * float2( 1.0,  1.0)).rgb;

    float3 o = (d + e + i + j) * (0.5   * 0.25);
    o       += (a + b + f + g) * (0.125 * 0.25);
    o       += (b + c + g + h) * (0.125 * 0.25);
    o       += (f + g + k + l) * (0.125 * 0.25);
    o       += (g + h + l + m) * (0.125 * 0.25);
    return o;
}

float4 PSPrefilter(VS_OUT i) : SV_TARGET
{
    float3 col = Down13(i.uv);

    // Quadratic soft-knee threshold: zero below (Threshold - Knee), smooth quadratic ramp
    // through the knee, linear above — no hard-cutoff shimmer on grazing highlights.
    float br      = max(col.r, max(col.g, col.b));
    float soft    = clamp(br - Threshold + Knee, 0.0, 2.0 * Knee);
    soft          = soft * soft / (4.0 * Knee + 1e-4);
    float contrib = max(soft, br - Threshold) / max(br, 1e-4);
    return float4(col * contrib, 1.0);
}

float4 PSDownsample(VS_OUT i) : SV_TARGET
{
    return float4(Down13(i.uv), 1.0);
}

// 9-tap 3x3 tent upsample — the progressive tent accumulation across the mip chain is what
// approximates the big gaussian. Output is Weight-scaled; the PSO adds it onto the finer mip.
float4 PSUpsample(VS_OUT i) : SV_TARGET
{
    float4 d = SrcTexel.xyxy * float4(1.0, 1.0, -1.0, 0.0) * SampleScale;
    float3 s;
    s  = Src.Sample(Smp, i.uv - d.xy).rgb;
    s += Src.Sample(Smp, i.uv - d.wy).rgb * 2.0;
    s += Src.Sample(Smp, i.uv - d.zy).rgb;
    s += Src.Sample(Smp, i.uv + d.zw).rgb * 2.0;
    s += Src.Sample(Smp, i.uv).rgb        * 4.0;
    s += Src.Sample(Smp, i.uv + d.xw).rgb * 2.0;
    s += Src.Sample(Smp, i.uv + d.zy).rgb;
    s += Src.Sample(Smp, i.uv + d.wy).rgb * 2.0;
    s += Src.Sample(Smp, i.uv + d.xy).rgb;
    return float4(s * (1.0 / 16.0) * Weight, 1.0);
}
