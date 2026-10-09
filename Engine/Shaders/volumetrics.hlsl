// Volumetric fog on DirectX 12 (#119) — the HLSL twin of volumetrics.metal / the GLSL volumetrics.* set.
// VolFogPS ray-marches each pixel of a half-resolution target from the near plane to the scene depth (the max
// distance in the sky), accumulating the light scattered towards the camera — the fog colour, the sun through the
// cascaded shadow maps, the point and spot lights through their shadow maps — with a Henyey-Greenstein phase
// against the transmittance of a density that follows the scene fog's height profile and a wind-animated noise.
// VolCompositePS then blends it over the scene: scene * T + S (blend ONE / SRC_ALPHA).
// Conventions as in standard.hlsl: row-major matrices, mul(vec, mat). VFrame (256) is byte-matched to
// DX12Volumetrics.cpp; the light block (b2) is the renderer's 2304-byte light buffer, exactly as standard.hlsl reads it.

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

cbuffer VFrame : register(b0)
{
    row_major float4x4 inv_view_projection;   // @0
    float4 eye;                               // @64  xyz, w = time (s)
    float4 screen;                            // @80  fog target w, h, 1/w, 1/h
    float4 depth_params;                      // @96  x near, y far, z ortho
    float4 fog;                               // @112 rgb colour, w volumetric density
    float4 fog2;                              // @128 height_y, height_falloff, noise_strength, noise_scale
    float4 vol;                               // @144 anisotropy, max_distance, steps, noise_speed
    float4 sun_dir;                           // @160 xyz = direction the light travels, w = intensity
    float4 sun_color;                         // @176 rgb, w = ambient strength
    float4 params2;                           // @192 light_intensity, sun_shafts, shadows, shadow_map_texel
    uint4  counts;                            // @208 x point lights, y spot lights
    float4 params3;                           // @224 x ambient_fog
    float4 pad;                               // @240
};                                            // = 256

struct PointLight { float3 position; float range; float3 color; float intensity; };
struct SpotLight
{
    float3 position;  float range;
    float3 direction; float spot_angle;
    float3 color;     float intensity;
    float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
};
cbuffer LightBuffer : register(b2)
{
    PointLight PointLights[MAX_POINT_LIGHTS];
    SpotLight SpotLights[MAX_SPOT_LIGHTS];
    row_major float4x4 ShadowVP[4];
    row_major float4x4 CascadeVP[3];
    float4 CascadeSplits;
    float4 DirShadowParams;      // x strength, y bias, z cascade count
    float4 PointShadows[2];      // x light index (-1 = none), y strength, z bias
    row_major float4x4 PointFaceVP[12];
};

Texture2D        FogTex         : register(t0);   // composite: the half-res fog (S in rgb, T in a)
Texture2D<float> SceneDepth     : register(t1);
Texture2D        ShadowMap      : register(t7);   // spot atlas (2x2 tiles)
Texture2D        CsmShadowMap   : register(t8);   // cascade atlas (2x2 tiles)
Texture2D        PointShadowMap : register(t9);   // point cube faces (4x3 tiles)
SamplerState            LinearClamp   : register(s0);
SamplerComparisonState  ShadowSampler : register(s1);

struct VolOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VolOut VolFogVS(uint id : SV_VertexID)
{
    VolOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// ---- helpers ----
float3 world_from_ndc(float2 uv, float d)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 h = mul(float4(ndc, d, 1.0), inv_view_projection);
    return h.xyz / h.w;
}

float hash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float vnoise(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i), n100 = hash13(i + float3(1, 0, 0)), n010 = hash13(i + float3(0, 1, 0)), n110 = hash13(i + float3(1, 1, 0));
    float n001 = hash13(i + float3(0, 0, 1)), n101 = hash13(i + float3(1, 0, 1)), n011 = hash13(i + float3(0, 1, 1)), n111 = hash13(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y), lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

// scattering density at a point: base × the scene fog's height profile × the wind-blown noise
float density_at(float3 p)
{
    float d = fog.w;
    if (fog2.y > 0.0 && p.y > fog2.x) d *= exp(-fog2.y * (p.y - fog2.x));
    if (fog2.z > 0.0)
    {
        float3 q = p / max(fog2.w, 0.05) + float3(0.0, 0.0, eye.w * vol.w);
        float n = vnoise(q) * 0.65 + vnoise(q * 2.7 + 17.0) * 0.35;      // 0..1
        d *= saturate(1.0 - fog2.z + fog2.z * n * 2.0);
    }
    return d;
}

float hg(float cos_t, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cos_t, 1e-4), 1.5));
}

float p_atten(float dist, float range)
{
    float d = dist / range;
    float a = saturate(1.0 - d * d);
    return a * a / (dist * dist + 0.01);
}

// single-tap shadow lookups (standard.hlsl's, without the PCF kernels — the march averages them anyway)
float spot_shadow(float3 wp, int slot, float strength, float bias)
{
    float4 sp = mul(float4(wp, 1.0), ShadowVP[slot]);
    if (sp.w <= 0.0) return 1.0;
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
    float texel = params2.w;
    suv = clamp(suv, texel * 0.5, 1.0 - texel * 0.5) * 0.5;
    suv += float2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
    float lit = ShadowMap.SampleCmpLevelZero(ShadowSampler, suv, ndc.z - bias);
    return lerp(1.0, lit, saturate(strength));
}

float cascade_shadow(float3 wp)
{
    int count = (int)DirShadowParams.z;
    if (count <= 0) return 1.0;
    [unroll]
    for (int c = 0; c < 3; ++c)
    {
        if (c >= count) break;
        float4 sp = mul(float4(wp, 1.0), CascadeVP[c]);
        if (sp.w <= 0.0) continue;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0) continue;
        float texel = params2.w;
        suv = clamp(suv, texel * 1.5, 1.0 - texel * 1.5) * 0.5;
        suv += float2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
        float bias = DirShadowParams.y * (1.0 + (float)c);
        float lit = CsmShadowMap.SampleCmpLevelZero(ShadowSampler, suv, ndc.z - bias);
        return lerp(1.0, lit, saturate(DirShadowParams.x));
    }
    return 1.0;
}

float point_shadow(float3 wp, float3 lpos, int light_index)
{
    [unroll]
    for (int p = 0; p < 2; ++p)
    {
        if ((int)PointShadows[p].x != light_index) continue;
        float3 d = wp - lpos;
        float3 ad = abs(d);
        int face;
        if (ad.x >= ad.y && ad.x >= ad.z) face = d.x > 0.0 ? 0 : 1;
        else if (ad.y >= ad.z)            face = d.y > 0.0 ? 2 : 3;
        else                              face = d.z > 0.0 ? 4 : 5;
        float4 sp = mul(float4(wp, 1.0), PointFaceVP[p * 6 + face]);
        if (sp.w <= 0.0) return 1.0;
        float3 ndc = sp.xyz / sp.w;
        float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;
        const float tile_texel = 1.0 / 1024.0;
        suv = clamp(suv, tile_texel * 1.5, 1.0 - tile_texel * 1.5);
        int tile = p * 6 + face;
        float2 auv = (suv + float2(tile & 3, tile >> 2)) * float2(0.25, 1.0 / 3.0);
        float lit = PointShadowMap.SampleCmpLevelZero(ShadowSampler, auv, ndc.z - PointShadows[p].z);
        return lerp(1.0, lit, saturate(PointShadows[p].y));
    }
    return 1.0;
}

// light scattered towards the camera at p, for a view ray direction `dir`
float3 inscatter(float3 p, float3 dir, float g, bool shadows)
{
    float3 L = fog.rgb * params3.x * sun_color.w;
    float sunv = params2.y * sun_dir.w;
    if (sunv > 0.0)
    {
        float sh = shadows ? cascade_shadow(p) : 1.0;
        L += sun_color.rgb * sunv * hg(dot(normalize(sun_dir.xyz), -dir), g) * sh;
    }
    float lk = params2.x;
    [loop] for (uint i = 0u; i < counts.x && i < (uint)MAX_POINT_LIGHTS; ++i)
    {
        float3 lv = p - PointLights[i].position;
        float dist = length(lv);
        if (dist >= PointLights[i].range || dist < 1e-4) continue;
        float sh = shadows ? point_shadow(p, PointLights[i].position, (int)i) : 1.0;
        L += PointLights[i].color * PointLights[i].intensity * lk * p_atten(dist, PointLights[i].range) * hg(dot(lv / dist, -dir), g) * sh;
    }
    [loop] for (uint j = 0u; j < counts.y && j < (uint)MAX_SPOT_LIGHTS; ++j)
    {
        float3 lv = p - SpotLights[j].position;
        float dist = length(lv);
        if (dist >= SpotLights[j].range || dist < 1e-4) continue;
        float3 ld = lv / dist;
        float theta = dot(ld, normalize(SpotLights[j].direction));
        float oc = cos(SpotLights[j].spot_angle * 0.5 * PI / 180.0);
        float ic = cos(SpotLights[j].inner_spot_angle * 0.5 * PI / 180.0);
        float cone = saturate((theta - oc) / (ic - oc + 0.001));
        if (cone <= 0.0) continue;
        float sh = (shadows && SpotLights[j].shadow_slot >= 0.0) ? spot_shadow(p, (int)SpotLights[j].shadow_slot, SpotLights[j].shadow_strength, SpotLights[j].shadow_bias) : 1.0;
        L += SpotLights[j].color * SpotLights[j].intensity * lk * p_atten(dist, SpotLights[j].range) * cone * hg(dot(ld, -dir), g) * sh;
    }
    return L;
}

float4 VolFogPS(VolOut i) : SV_Target
{
    uint dw, dh;
    SceneDepth.GetDimensions(dw, dh);
    int2 px = int2(clamp(i.uv * float2((float)dw, (float)dh), float2(0.0, 0.0), float2((float)dw - 1.0, (float)dh - 1.0)));
    float d = SceneDepth.Load(int3(px, 0));
    float3 p0 = world_from_ndc(i.uv, 0.0);
    float3 p1 = world_from_ndc(i.uv, 0.5);
    float3 dir = normalize(p1 - p0);
    float t_max = vol.y;
    if (d < 0.99999) t_max = min(t_max, length(world_from_ndc(i.uv, d) - p0));
    if (t_max <= 1e-3 || fog.w <= 0.0) return float4(0.0, 0.0, 0.0, 1.0);
    int steps = clamp((int)vol.z, 4, 64);
    float step = t_max / (float)steps;
    float jitter = frac(52.9829189 * frac(0.06711056 * i.pos.x + 0.00583715 * i.pos.y));
    bool shadows = params2.z > 0.5;
    float g = vol.x;
    float3 S = float3(0.0, 0.0, 0.0);
    float T = 1.0;
    [loop] for (int k = 0; k < steps; ++k)
    {
        float t = ((float)k + jitter) * step;
        float3 p = p0 + dir * t;
        float dens = density_at(p);
        if (dens <= 0.0) continue;
        float seg_t = exp(-dens * step);
        S += T * (1.0 - seg_t) * inscatter(p, dir, g, shadows);
        T *= seg_t;
        if (T < 0.005) break;
    }
    return float4(S, T);
}

float4 VolCompositePS(VolOut i) : SV_Target
{
    return FogTex.Sample(LinearClamp, i.uv);
}
