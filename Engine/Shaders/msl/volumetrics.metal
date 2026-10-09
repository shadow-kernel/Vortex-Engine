// Volumetric fog for the SDL GPU backend (#119) — the Metal twin of volumetrics.hlsl / the GLSL volumetrics.* set.
// VolFogPS ray-marches each pixel of a half-resolution target from the near plane to the scene depth (the max
// distance in the sky), accumulating the light scattered towards the camera — the fog colour, the sun through the
// cascaded shadow maps, the point and spot lights through their shadow maps — with a Henyey-Greenstein phase against
// the transmittance of a density that follows the scene fog's height profile and a wind-animated noise.
// VolCompositePS blends it over the scene: scene * T + S (blend ONE / SRC_ALPHA).
//
// Binding conventions follow SDL GPU's MSL rules (see standard.metal):
//   fragment (march): uniform buffers [[buffer(0..1)]] (frame, lights); texture 0 = scene depth (point sampler),
//                     textures 1..3 = spot / cascade / point shadow atlases with their comparison samplers
//   fragment (composite): texture 0 = the fog target (linear clamp)
// VFrame (256) is byte-matched to SdlGpuVolumetrics.cpp; LightBuffer is the renderer's 2304-byte light buffer.
#include <metal_stdlib>
using namespace metal;

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

struct VFrame
{
    float4x4 inv_view_projection;   // @0   (row-major in memory -> M * v here)
    float4 eye;                     // @64  xyz, w = time (s)
    float4 screen;                  // @80  fog target w, h, 1/w, 1/h
    float4 depth_params;            // @96  x near, y far, z ortho
    float4 fog;                     // @112 rgb colour, w volumetric density
    float4 fog2;                    // @128 height_y, height_falloff, noise_strength, noise_scale
    float4 vol;                     // @144 anisotropy, max_distance, steps, noise_speed
    float4 sun_dir;                 // @160 xyz = direction the light travels, w = intensity
    float4 sun_color;               // @176 rgb, w = ambient strength
    float4 params2;                 // @192 light_intensity, sun_shafts, shadows, shadow_map_texel
    uint4 counts;                   // @208 x point lights, y spot lights
    float4 params3;                 // @224 x ambient_fog
    float4 pad;                     // @240
};                                  // = 256

struct PointLight { packed_float3 position; float range; packed_float3 color; float intensity; };
struct SpotLight
{
    packed_float3 position;  float range;
    packed_float3 direction; float spot_angle;
    packed_float3 color;     float intensity;
    float inner_spot_angle;  float shadow_strength; float shadow_bias; float shadow_slot;
};
struct LightBuffer
{
    PointLight point_lights[MAX_POINT_LIGHTS];   // @0
    SpotLight  spot_lights[MAX_SPOT_LIGHTS];     // @512
    float4x4   shadow_vp[4];                     // @1024
    float4x4   cascade_vp[3];                    // @1280
    float4     cascade_splits;                   // @1472
    float4     dir_shadow_params;                // @1488
    float4     point_shadows[2];                 // @1504
    float4x4   point_face_vp[12];                // @1536 .. 2304
};

struct VolOut { float4 pos [[position]]; float2 uv; };

vertex VolOut VolFogVS(uint id [[vertex_id]])
{
    VolOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

static float3 world_from_ndc(constant VFrame& f, float2 uv, float d)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 h = f.inv_view_projection * float4(ndc, d, 1.0);
    return h.xyz / h.w;
}

static float hash13(float3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

static float vnoise(float3 p)
{
    float3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i), n100 = hash13(i + float3(1, 0, 0)), n010 = hash13(i + float3(0, 1, 0)), n110 = hash13(i + float3(1, 1, 0));
    float n001 = hash13(i + float3(0, 0, 1)), n101 = hash13(i + float3(1, 0, 1)), n011 = hash13(i + float3(0, 1, 1)), n111 = hash13(i + float3(1, 1, 1));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y), mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

static float density_at(constant VFrame& f, float3 p)
{
    float d = f.fog.w;
    if (f.fog2.y > 0.0 && p.y > f.fog2.x) d *= exp(-f.fog2.y * (p.y - f.fog2.x));
    if (f.fog2.z > 0.0)
    {
        float3 q = p / max(f.fog2.w, 0.05) + float3(0.0, 0.0, f.eye.w * f.vol.w);
        float n = vnoise(q) * 0.65 + vnoise(q * 2.7 + 17.0) * 0.35;
        d *= saturate(1.0 - f.fog2.z + f.fog2.z * n * 2.0);
    }
    return d;
}

static float hg(float cos_t, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cos_t, 1e-4), 1.5));
}

static float p_atten(float dist, float range)
{
    float d = dist / range;
    float a = saturate(1.0 - d * d);
    return a * a / (dist * dist + 0.01);
}

static float spot_shadow(constant VFrame& f, constant LightBuffer& L, depth2d<float> atlas, sampler smp, float3 wp, int slot, float strength, float bias)
{
    float4 sp = L.shadow_vp[slot] * float4(wp, 1.0);
    if (sp.w <= 0.0) return 1.0;
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
    float texel = f.params2.w;
    suv = clamp(suv, texel * 0.5, 1.0 - texel * 0.5) * 0.5;
    suv += float2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
    float lit = atlas.sample_compare(smp, suv, ndc.z - bias);
    return mix(1.0, lit, saturate(strength));
}

static float cascade_shadow(constant VFrame& f, constant LightBuffer& L, depth2d<float> atlas, sampler smp, float3 wp)
{
    int count = (int)L.dir_shadow_params.z;
    if (count <= 0) return 1.0;
    for (int c = 0; c < 3; ++c)
    {
        if (c >= count) break;
        float4 sp = L.cascade_vp[c] * float4(wp, 1.0);
        if (sp.w <= 0.0) continue;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0) continue;
        float texel = f.params2.w;
        suv = clamp(suv, texel * 1.5, 1.0 - texel * 1.5) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
        float bias = L.dir_shadow_params.y * (1.0 + (float)c);
        float lit = atlas.sample_compare(smp, suv, ndc.z - bias);
        return mix(1.0, lit, saturate(L.dir_shadow_params.x));
    }
    return 1.0;
}

static float point_shadow(constant LightBuffer& L, depth2d<float> atlas, sampler smp, float3 wp, float3 lpos, int light_index)
{
    for (int p = 0; p < 2; ++p)
    {
        if ((int)L.point_shadows[p].x != light_index) continue;
        float3 d = wp - lpos;
        float3 ad = abs(d);
        int face;
        if (ad.x >= ad.y && ad.x >= ad.z) face = d.x > 0.0 ? 0 : 1;
        else if (ad.y >= ad.z)            face = d.y > 0.0 ? 2 : 3;
        else                              face = d.z > 0.0 ? 4 : 5;
        float4 sp = L.point_face_vp[p * 6 + face] * float4(wp, 1.0);
        if (sp.w <= 0.0) return 1.0;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
        const float tile_texel = 1.0 / 1024.0;
        suv = clamp(suv, tile_texel * 1.5, 1.0 - tile_texel * 1.5);
        int tile = p * 6 + face;
        float2 auv = (suv + float2(tile & 3, tile >> 2)) * float2(0.25, 1.0 / 3.0);
        float lit = atlas.sample_compare(smp, auv, ndc.z - L.point_shadows[p].z);
        return mix(1.0, lit, saturate(L.point_shadows[p].y));
    }
    return 1.0;
}

fragment float4 VolFogPS(VolOut in [[stage_in]], constant VFrame& f [[buffer(0)]], constant LightBuffer& L [[buffer(1)]],
                         depth2d<float> depth [[texture(0)]], sampler dsmp [[sampler(0)]],
                         depth2d<float> spot_atlas [[texture(1)]], sampler spot_smp [[sampler(1)]],
                         depth2d<float> csm_atlas [[texture(2)]], sampler csm_smp [[sampler(2)]],
                         depth2d<float> point_atlas [[texture(3)]], sampler point_smp [[sampler(3)]])
{
    uint2 px = uint2(clamp(in.uv * float2(depth.get_width(), depth.get_height()), float2(0.0), float2(depth.get_width() - 1, depth.get_height() - 1)));
    float d = depth.read(px);
    float3 p0 = world_from_ndc(f, in.uv, 0.0);
    float3 p1 = world_from_ndc(f, in.uv, 0.5);
    float3 dir = normalize(p1 - p0);
    float t_max = f.vol.y;
    if (d < 0.99999) t_max = min(t_max, length(world_from_ndc(f, in.uv, d) - p0));
    if (t_max <= 1e-3 || f.fog.w <= 0.0) return float4(0.0, 0.0, 0.0, 1.0);
    int steps = clamp((int)f.vol.z, 4, 64);
    float step = t_max / (float)steps;
    float jitter = fract(52.9829189 * fract(0.06711056 * in.pos.x + 0.00583715 * in.pos.y));
    bool shadows = f.params2.z > 0.5;
    float g = f.vol.x;
    float3 sunL = normalize(f.sun_dir.xyz);
    float sunv = f.params2.y * f.sun_dir.w;
    float lk = f.params2.x;
    float3 S = float3(0.0);
    float T = 1.0;
    for (int k = 0; k < steps; ++k)
    {
        float t = ((float)k + jitter) * step;
        float3 p = p0 + dir * t;
        float dens = density_at(f, p);
        if (dens <= 0.0) continue;
        float seg_t = exp(-dens * step);
        float3 Lc = f.fog.rgb * f.params3.x * f.sun_color.w;
        if (sunv > 0.0)
        {
            float sh = shadows ? cascade_shadow(f, L, csm_atlas, csm_smp, p) : 1.0;
            Lc += f.sun_color.rgb * sunv * hg(dot(sunL, -dir), g) * sh;
        }
        for (uint i = 0u; i < f.counts.x && i < uint(MAX_POINT_LIGHTS); ++i)
        {
            float3 lpos = float3(L.point_lights[i].position);
            float3 lv = p - lpos;
            float dist = length(lv);
            if (dist >= L.point_lights[i].range || dist < 1e-4) continue;
            float sh = shadows ? point_shadow(L, point_atlas, point_smp, p, lpos, (int)i) : 1.0;
            Lc += float3(L.point_lights[i].color) * L.point_lights[i].intensity * lk * p_atten(dist, L.point_lights[i].range) * hg(dot(lv / dist, -dir), g) * sh;
        }
        for (uint j = 0u; j < f.counts.y && j < uint(MAX_SPOT_LIGHTS); ++j)
        {
            float3 lv = p - float3(L.spot_lights[j].position);
            float dist = length(lv);
            if (dist >= L.spot_lights[j].range || dist < 1e-4) continue;
            float3 ld = lv / dist;
            float theta = dot(ld, normalize(float3(L.spot_lights[j].direction)));
            float oc = cos(L.spot_lights[j].spot_angle * 0.5 * PI / 180.0);
            float ic = cos(L.spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
            float cone = saturate((theta - oc) / (ic - oc + 0.001));
            if (cone <= 0.0) continue;
            float sh = (shadows && L.spot_lights[j].shadow_slot >= 0.0) ? spot_shadow(f, L, spot_atlas, spot_smp, p, (int)L.spot_lights[j].shadow_slot, L.spot_lights[j].shadow_strength, L.spot_lights[j].shadow_bias) : 1.0;
            Lc += float3(L.spot_lights[j].color) * L.spot_lights[j].intensity * lk * p_atten(dist, L.spot_lights[j].range) * cone * hg(dot(ld, -dir), g) * sh;
        }
        S += T * (1.0 - seg_t) * Lc;
        T *= seg_t;
        if (T < 0.005) break;
    }
    return float4(S, T);
}

fragment float4 VolCompositePS(VolOut in [[stage_in]], texture2d<float> fog [[texture(0)]], sampler smp [[sampler(0)]])
{
    return fog.sample(smp, in.uv);
}
