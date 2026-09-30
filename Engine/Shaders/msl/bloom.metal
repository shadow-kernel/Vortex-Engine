// Bloom chain for the SDL GPU backend (bloom.hlsl): soft-knee prefilter, 13-tap downsample chain and
// additive 9-tap tent upsample. The uber pass (postfx.metal, flag 32) composites mip 0 onto the scene.
#include <metal_stdlib>
using namespace metal;

struct BloomCB
{
    float2 src_texel;     // 1 / size of the SOURCE level
    float threshold;
    float knee;
    float sample_scale;   // upsample tent radius in source texels
    float weight;         // upsample additive weight (scatter)
    float2 pad;
};

struct FsOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex FsOut BloomVS(uint id [[vertex_id]])
{
    FsOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

static inline float3 down13(texture2d<float> src, sampler smp, float2 uv, float2 t)
{
    float3 a = src.sample(smp, uv + t * float2(-1.0, -1.0)).rgb;
    float3 b = src.sample(smp, uv + t * float2( 0.0, -1.0)).rgb;
    float3 c = src.sample(smp, uv + t * float2( 1.0, -1.0)).rgb;
    float3 d = src.sample(smp, uv + t * float2(-0.5, -0.5)).rgb;
    float3 e = src.sample(smp, uv + t * float2( 0.5, -0.5)).rgb;
    float3 f = src.sample(smp, uv + t * float2(-1.0,  0.0)).rgb;
    float3 g = src.sample(smp, uv).rgb;
    float3 h = src.sample(smp, uv + t * float2( 1.0,  0.0)).rgb;
    float3 i = src.sample(smp, uv + t * float2(-0.5,  0.5)).rgb;
    float3 j = src.sample(smp, uv + t * float2( 0.5,  0.5)).rgb;
    float3 k = src.sample(smp, uv + t * float2(-1.0,  1.0)).rgb;
    float3 l = src.sample(smp, uv + t * float2( 0.0,  1.0)).rgb;
    float3 m = src.sample(smp, uv + t * float2( 1.0,  1.0)).rgb;
    float3 o = (d + e + i + j) * (0.5 * 0.25);
    o += (a + b + f + g) * (0.125 * 0.25);
    o += (b + c + g + h) * (0.125 * 0.25);
    o += (f + g + k + l) * (0.125 * 0.25);
    o += (g + h + l + m) * (0.125 * 0.25);
    return o;
}

fragment float4 BloomPrefilterPS(FsOut in [[stage_in]], constant BloomCB& c [[buffer(0)]],
                                 texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    float3 col = down13(src, smp, in.uv, c.src_texel);
    float br = max(col.r, max(col.g, col.b));
    float soft = clamp(br - c.threshold + c.knee, 0.0, 2.0 * c.knee);
    soft = soft * soft / (4.0 * c.knee + 1e-4);
    float contrib = max(soft, br - c.threshold) / max(br, 1e-4);
    return float4(col * contrib, 1.0);
}

fragment float4 BloomDownsamplePS(FsOut in [[stage_in]], constant BloomCB& c [[buffer(0)]],
                                  texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    return float4(down13(src, smp, in.uv, c.src_texel), 1.0);
}

fragment float4 BloomUpsamplePS(FsOut in [[stage_in]], constant BloomCB& c [[buffer(0)]],
                                texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    float4 d = c.src_texel.xyxy * float4(1.0, 1.0, -1.0, 0.0) * c.sample_scale;
    float3 s;
    s  = src.sample(smp, in.uv - d.xy).rgb;
    s += src.sample(smp, in.uv - d.wy).rgb * 2.0;
    s += src.sample(smp, in.uv - d.zy).rgb;
    s += src.sample(smp, in.uv + d.zw).rgb * 2.0;
    s += src.sample(smp, in.uv).rgb * 4.0;
    s += src.sample(smp, in.uv + d.xw).rgb * 2.0;
    s += src.sample(smp, in.uv + d.zy).rgb;
    s += src.sample(smp, in.uv + d.wy).rgb * 2.0;
    s += src.sample(smp, in.uv + d.xy).rgb;
    return float4(s * (1.0 / 16.0) * c.weight, 1.0);
}
