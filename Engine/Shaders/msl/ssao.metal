// SSAO for the SDL GPU backend (ssao.hlsl): half-res depth prepass -> Alchemy-style AO -> 4-tap blur.
// SsaoCB is byte-matched to the C++ SsaoCB; the result darkens only the ambient term in standard.metal.
#include <metal_stdlib>
using namespace metal;

struct SsaoCB
{
    float4x4 inv_proj;     // clip -> view (this view's projection only)
    float2 texel;          // 1 / AO target size
    float radius;          // world-space sample radius
    float intensity;
    float bias;
    float proj_scale;      // 0.5 * proj._22 * targetHeight
    float2 pad;
};

struct FsOut
{
    float4 pos [[position]];
    float2 uv;
};

vertex FsOut SsaoVS(uint id [[vertex_id]])
{
    FsOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

static inline float3 view_pos(depth2d<float> depth, sampler smp, constant SsaoCB& c, float2 uv)
{
    float d = depth.sample(smp, uv);
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 v = c.inv_proj * float4(ndc, d, 1.0);
    return v.xyz / max(v.w, 1e-6);
}

static inline float hash12(float2 p)
{
    p = fract(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return fract(p.x * p.y);
}

fragment float4 SsaoPS(FsOut in [[stage_in]], constant SsaoCB& c [[buffer(0)]],
                       depth2d<float> depth [[texture(0)]], sampler smp [[sampler(0)]])
{
    float d = depth.sample(smp, in.uv);
    if (d >= 0.9999) return float4(1.0);
    float3 P = view_pos(depth, smp, c, in.uv);
    float3 N = normalize(cross(dfdy(P), dfdx(P)));
    float pix_radius = clamp(c.proj_scale * c.radius / max(P.z, 0.1), 2.0, 64.0);
    const int TAPS = 12;
    float rot = hash12(in.pos.xy) * 6.2831853;
    float occlusion = 0.0;
    for (int k = 0; k < TAPS; ++k)
    {
        float a = rot + (float)k * 2.3999632;
        float r = pix_radius * sqrt(((float)k + 0.7) / (float)TAPS);
        float2 duv = float2(cos(a), sin(a)) * r * c.texel;
        float3 S = view_pos(depth, smp, c, in.uv + duv);
        float3 v = S - P;
        occlusion += max(0.0, dot(v, N) - c.bias) / (dot(v, v) + 0.01);
    }
    float ao = saturate(1.0 - c.intensity * occlusion * (2.0 / (float)TAPS));
    return float4(ao, ao, ao, 1.0);
}

fragment float4 SsaoBlurPS(FsOut in [[stage_in]], constant SsaoCB& c [[buffer(0)]],
                           texture2d<float> src [[texture(0)]], sampler smp [[sampler(0)]])
{
    float s = 0.0;
    s += src.sample(smp, in.uv + c.texel * float2(-0.5, -0.5)).r;
    s += src.sample(smp, in.uv + c.texel * float2( 1.5, -0.5)).r;
    s += src.sample(smp, in.uv + c.texel * float2(-0.5,  1.5)).r;
    s += src.sample(smp, in.uv + c.texel * float2( 1.5,  1.5)).r;
    float ao = s * 0.25;
    return float4(ao, ao, ao, 1.0);
}
