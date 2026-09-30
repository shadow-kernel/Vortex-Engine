// Fullscreen passes for the SDL GPU backend:
//   BlitVS/BlitPS   — render-scale composite (upscale.hlsl): samples the scene RT onto the present target
//   PostFxPS        — post-processing uber pass (postfx.hlsl): chromatic aberration, bloom composite, grain,
//                     color grading, vignette, debug invert. PostFxCB is byte-matched to the C++ PassCB.
#include <metal_stdlib>
using namespace metal;

struct BlitOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex BlitOut BlitVS(uint id [[vertex_id]])
{
    BlitOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

fragment float4 BlitPS(BlitOut in [[stage_in]], texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    return src.sample(smp, in.uv);
}

struct PostFxCB
{
    float2 texel_size;
    float  time;
    uint   flags;            // 1 vignette, 2 grain, 4 chromatic aberration, 8 invert, 16 color grading, 32 bloom
    float4 vignette;         // intensity, smoothness, roundness, unused
    float4 vignette_color;   // rgb, unused
    float4 grain_ca;         // grain intensity, grain size, ca strength, ca falloff
    float4 grade1;           // exposure, contrast, saturation, temperature
    float4 grade2;           // tint, reserved...
    float4 bloom;            // composite intensity, reserved...
};

static inline float hash21(float2 p)
{
    p = fract(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return fract(p.x * p.y);
}

fragment float4 PostFxPS(BlitOut in [[stage_in]], constant PostFxCB& c [[buffer(0)]],
                         texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]],
                         texture2d<float> bloom_tex [[texture(1)]], sampler bloom_smp [[sampler(1)]])
{
    float2 uv = in.uv;
    float3 col;
    if (c.flags & 4u)
    {
        float2 from_c = uv - 0.5;
        float r = saturate(length(from_c) * 2.0);
        float amt = c.grain_ca.z * 0.01 * pow(r, max(c.grain_ca.w, 0.01));
        col.r = src.sample(smp, uv + from_c * amt).r;
        col.g = src.sample(smp, uv).g;
        col.b = src.sample(smp, uv - from_c * amt).b;
    }
    else
    {
        col = src.sample(smp, uv).rgb;
    }

    if (c.flags & 32u)
        col += bloom_tex.sample(bloom_smp, uv).rgb * c.bloom.x;

    if (c.flags & 2u)
    {
        float2 cell = floor(in.pos.xy / max(c.grain_ca.y, 1.0));
        float n = hash21(cell + fract(c.time * float2(17.131, 3.7171)) * 289.17) * 2.0 - 1.0;
        float luma = dot(col, float3(0.299, 0.587, 0.114));
        col = saturate(col + n * c.grain_ca.x * 0.25 * (1.0 - saturate(luma)));
    }

    if (c.flags & 16u)
    {
        col *= exp2(c.grade1.x);
        float3 wb = float3(1.0 + c.grade1.w * 0.2, 1.0 + c.grade2.x * 0.2, 1.0 - c.grade1.w * 0.2);
        col *= wb;
        col = (col - 0.5) * max(c.grade1.y, 0.0) + 0.5;
        float luma = dot(col, float3(0.299, 0.587, 0.114));
        col = mix(float3(luma), col, c.grade1.z);
        col = max(col, 0.0);
    }

    if (c.flags & 1u)
    {
        float aspect = c.texel_size.y / c.texel_size.x;
        float2 d = (uv - 0.5) * 2.0 * c.vignette.x;
        d.x *= mix(1.0, aspect, c.vignette.z);
        float vig = pow(saturate(1.0 - dot(d, d)), c.vignette.y * 4.0 + 0.001);
        col = mix(c.vignette_color.rgb, col, vig);
    }

    if (c.flags & 8u)
        col = 1.0 - col;

    return float4(col, 1.0);
}
