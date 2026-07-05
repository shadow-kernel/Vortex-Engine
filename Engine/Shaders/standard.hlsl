// Standard PBR shader — the engine's main lit pipeline (drives 3 PSOs: solid, wireframe, double-sided).
// Cook-Torrance GGX + directional/point/spot lights + hemisphere ambient + environment/rim + ACES tonemap.
// World comes per-INSTANCE from the vertex stream (GPU instancing); PerObject.World is unused by the VS.
//
// IMPORTANT: the cbuffer field order + padding and the texture/sampler register bindings are ABI-coupled to the
// C++ PerFrame / PerObject / PointLight / SpotLight structs (and ResourceRegistry texture binding) — do NOT
// reorder or repad. Conventions: row-major matrices, mul(vec, mat).

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define PI 3.14159265359

cbuffer PerFrame : register(b0)
{
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float Padding0;
    float3 LightDirection;
    float DirectionalIntensity;
    float3 LightColor;
    float AmbientStrength;
    uint PointLightCount;
    uint SpotLightCount;
    uint2 FramePadding;
    // Fog (Welle A #27) — byte-matches the appended C++ PerFrameConstants fields (FogColor @128).
    float3 FogColor;
    float FogDensity;
    float FogHeightY;
    float FogHeightFalloff;
    uint FogMode;
    float FogPadding;
    // Spot-light shadows (#23) — APPENDED after fog (@160), byte-matched to PerFrameConstants.
    // ShadowStrength 0 = shadows off (the always-safe default); ShadowSpotIndex picks WHICH spot
    // the shadow map belongs to (one shadow-casting spot in v1 — the flashlight).
    row_major float4x4 ShadowViewProjection;
    float ShadowStrength;
    float ShadowBias;
    float ShadowMapTexel;
    uint ShadowSpotIndex;
};

// Exp2 distance fog with optional height weighting (ground mist below FogHeightY).
// Applied in LINEAR space before tonemapping; FogDensity 0 = off (default).
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (FogDensity <= 0.0) return color;
    float dist = length(CameraPosition - worldPos);
    float d = FogDensity * dist;
    float f = 1.0 - exp2(-d * d);
    if (FogHeightFalloff > 0.0)
        f *= saturate((FogHeightY - worldPos.y) * FogHeightFalloff);
    return lerp(color, FogColor, saturate(f));
}

cbuffer PerObject : register(b1)
{
    row_major float4x4 World;
    float4 BaseColor;
    float Metallic;
    float Roughness;
    float AO;
    float NormalStrength;
    uint HasAlbedoTexture;
    uint HasNormalTexture;
    uint HasMetallicTexture;
    uint HasRoughnessTexture;
    uint HasAOTexture;
    uint UseDirectXNormals;
    uint IsUnlit;
    float EmissiveStrength;
    float2 UVTiling;         // texture repeat scale (mirrors PerObjectConstants at byte offset 128)
    uint HasHeightTexture;   // @136 — parallax/displacement height map bound
    float HeightScale;       // @140 — parallax depth
};

struct PointLight
{
    float3 position;
    float range;
    float3 color;
    float intensity;
};

struct SpotLight
{
    float3 position;
    float range;
    float3 direction;
    float spotAngle;
    float3 color;
    float intensity;
    float innerSpotAngle;
    float3 spotPadding;
};

cbuffer LightBuffer : register(b2)
{
    PointLight PointLights[MAX_POINT_LIGHTS];
    SpotLight SpotLights[MAX_SPOT_LIGHTS];
};

Texture2D AlbedoTexture    : register(t0);
Texture2D NormalTexture    : register(t1);
Texture2D MetallicTexture  : register(t2);
Texture2D RoughnessTexture : register(t3);
Texture2D AOTexture        : register(t4);
// t5 is the vertex-stage bone-palette root SRV (skinning). Height/displacement map lives at t6 (pixel).
Texture2D HeightTexture    : register(t6);
// t7: spot-light shadow map (R32_FLOAT depth from the light's view), s1: comparison sampler
// (LESS_EQUAL, border=white so samples outside the map read "lit" — the cone gate masks the rest).
Texture2D ShadowMap        : register(t7);
SamplerState LinearSampler : register(s0);
SamplerComparisonState ShadowSampler : register(s1);

// 1 = fully lit, 0 = fully shadowed (scaled by ShadowStrength). Hard shadows via a single
// hardware-PCF comparison tap (SampleCmpLevelZero) — soft/PCF-kernel is a later upgrade
// (ShadowMapTexel is already plumbed for it).
float SampleSpotShadow(float3 worldPos)
{
    float4 sp = mul(float4(worldPos, 1.0), ShadowViewProjection);
    if (sp.w <= 0.0) return 1.0;                       // behind the light -> outside the cone anyway
    float3 ndc = sp.xyz / sp.w;
    float2 suv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(saturate(suv) != suv) || ndc.z > 1.0) return 1.0;   // outside the map -> lit
    float lit = ShadowMap.SampleCmpLevelZero(ShadowSampler, suv, ndc.z - ShadowBias);
    return lerp(1.0, lit, ShadowStrength);
}

struct VS_IN
{
    float3 pos  : POSITION;
    float3 norm : NORMAL;
    float2 uv   : TEXCOORD0;
    // Per-instance world matrix (4 rows) streamed from vertex slot 1 — enables GPU instancing.
    float4 iw0 : INSTANCEWORLD0;
    float4 iw1 : INSTANCEWORLD1;
    float4 iw2 : INSTANCEWORLD2;
    float4 iw3 : INSTANCEWORLD3;
};

struct PS_IN
{
    float4 pos       : SV_POSITION;
    float3 worldPos  : TEXCOORD1;
    float3 norm      : TEXCOORD2;
    float2 uv        : TEXCOORD0;
    float3 tangent   : TEXCOORD3;
    float3 bitangent : TEXCOORD4;
};

PS_IN VSMain(VS_IN input)
{
    PS_IN output;
    // World comes per-instance from the vertex stream (row-major), not the constant buffer.
    float4x4 World = float4x4(input.iw0, input.iw1, input.iw2, input.iw3);
    float4 worldPos = mul(float4(input.pos, 1), World);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjection);
    output.norm = normalize(mul(input.norm, (float3x3)World));
    output.uv = input.uv;

    // Derive a tangent basis from the normal. Guard on the PRE-normalized cross length: for a flat up-facing
    // surface (N=(0,1,0), a ground/ceiling plane) cross(N,(0,1,0))=(0,0,0) and normalize() would yield NaN — and
    // `length(NaN) < 0.001` is FALSE (all NaN comparisons are false), so the old fallback was dead code and the
    // tangent stayed NaN. That NaN now feeds the parallax path -> NaN UVs -> the whole surface renders black.
    float3 N = output.norm;
    float3 c = cross(N, float3(0, 1, 0));
    float3 T = (dot(c, c) < 1e-6) ? normalize(cross(N, float3(1, 0, 0))) : normalize(c);
    float3 B = normalize(cross(N, T));
    output.tangent = T;
    output.bitangent = B;

    return output;
}

float D_GGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 0.0001);
}

float G_SchlickGGX(float NdotV, float roughness)
{
    float k = (roughness + 1.0);
    k = (k * k) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k + 0.0001);
}

float G_Smith(float NdotV, float NdotL, float roughness)
{
    return G_SchlickGGX(NdotV, roughness) * G_SchlickGGX(NdotL, roughness);
}

float3 F_Schlick(float VdotH, float3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);
}

float3 SRGBToLinear(float3 color)
{
    return pow(max(color, 0.0), 2.2);
}

float Attenuation(float distance, float range)
{
    float d = distance / range;
    float atten = saturate(1.0 - d * d);
    return atten * atten / (distance * distance + 0.01);
}

float4 PSMain(PS_IN input) : SV_TARGET
{
    // Texture repeat scale: multiply UVs so a small tiling texture repeats across a large surface instead of being
    // stretched once (the "blurry ground" bug). Guard against an unset/zero tiling (e.g. a non-material draw) -> 1x.
    float2 tiling = (UVTiling.x > 0.0 && UVTiling.y > 0.0) ? UVTiling : float2(1.0, 1.0);
    float2 uv = input.uv * tiling;

    // Parallax mapping: shift the UVs along the tangent-space view direction by the height map, so a "texture with
    // depth" reads as real relief (stones stand out) instead of a flat decal. Guarded: no height map / zero scale
    // leaves UVs untouched. max(Vt.z,..) tames swimming at grazing angles.
    if (HasHeightTexture != 0 && HeightScale > 0.0) {
        float3 Vw = normalize(CameraPosition - input.worldPos);
        float3x3 TBN = float3x3(normalize(input.tangent), normalize(input.bitangent), normalize(input.norm));
        float3 Vt = mul(TBN, Vw);
        float h = HeightTexture.Sample(LinearSampler, uv).r;
        uv -= (Vt.xy / max(Vt.z, 0.15)) * ((1.0 - h) * HeightScale);
    }

    float3 albedo = BaseColor.rgb;
    float alpha = BaseColor.a;

    if (HasAlbedoTexture != 0) {
        float4 tex = AlbedoTexture.Sample(LinearSampler, uv);
        albedo = SRGBToLinear(tex.rgb);
        alpha = tex.a;
    }

    // UNLIT/EMISSIVE PATH - bypass all lighting calculations (for skybox, etc.)
    if (IsUnlit != 0) {
        float3 emissive = albedo * EmissiveStrength;
        emissive = ApplyFog(emissive, input.worldPos);  // fog swallows unlit props too (atmosphere consistency)
        // Apply simple tone mapping for HDR
        emissive = emissive / (emissive + 1.0);
        // Gamma correction
        emissive = pow(emissive, 1.0 / 2.2);
        return float4(emissive, alpha);
    }

    float metallic = Metallic;
    if (HasMetallicTexture != 0) {
        metallic = MetallicTexture.Sample(LinearSampler, uv).r;
    }

    float roughness = max(Roughness, 0.04);
    if (HasRoughnessTexture != 0) {
        roughness = max(RoughnessTexture.Sample(LinearSampler, uv).r, 0.04);
    }

    float ao = AO;
    if (HasAOTexture != 0) {
        ao = AOTexture.Sample(LinearSampler, uv).r;
    }

    float3 N = normalize(input.norm);
    if (HasNormalTexture != 0) {
        float3 normalMap = NormalTexture.Sample(LinearSampler, uv).rgb;
        normalMap = normalMap * 2.0 - 1.0;
        if (UseDirectXNormals == 0) normalMap.y = -normalMap.y;
        normalMap.xy *= NormalStrength;
        float3x3 TBN = float3x3(normalize(input.tangent), normalize(input.bitangent), N);
        N = normalize(mul(normalMap, TBN));
    }

    float3 V = normalize(CameraPosition - input.worldPos);
    float NdotV = max(dot(N, V), 0.001);

    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

    float3 Lo = float3(0, 0, 0);

    // DIRECTIONAL LIGHT
    if (DirectionalIntensity > 0.001) {
        float3 L = normalize(-LightDirection);
        float3 H = normalize(V + L);
        float NdotL = max(dot(N, L), 0.0);
        float NdotH = max(dot(N, H), 0.0);
        float VdotH = max(dot(V, H), 0.0);

        float D = D_GGX(NdotH, roughness);
        float G = G_Smith(NdotV, NdotL, roughness);
        float3 F = F_Schlick(VdotH, F0);

        float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
        float3 kD = (1.0 - F) * (1.0 - metallic);

        float3 radiance = LightColor * DirectionalIntensity;
        Lo += (kD * albedo / PI + spec) * radiance * NdotL;
    }

    // POINT LIGHTS
    for (uint i = 0; i < PointLightCount && i < MAX_POINT_LIGHTS; ++i) {
        float3 lightVec = PointLights[i].position - input.worldPos;
        float dist = length(lightVec);

        if (dist < PointLights[i].range) {
            float3 L = lightVec / dist;
            float3 H = normalize(V + L);
            float NdotL = max(dot(N, L), 0.0);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);

            float atten = Attenuation(dist, PointLights[i].range);
            float3 radiance = PointLights[i].color * PointLights[i].intensity * atten;

            float D = D_GGX(NdotH, roughness);
            float G = G_Smith(NdotV, NdotL, roughness);
            float3 F = F_Schlick(VdotH, F0);

            float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
            float3 kD = (1.0 - F) * (1.0 - metallic);

            Lo += (kD * albedo / PI + spec) * radiance * NdotL;
        }
    }

    // SPOT LIGHTS
    for (uint j = 0; j < SpotLightCount && j < MAX_SPOT_LIGHTS; ++j) {
        float3 lightVec = SpotLights[j].position - input.worldPos;
        float dist = length(lightVec);

        if (dist < SpotLights[j].range) {
            float3 L = lightVec / dist;
            float3 spotDir = normalize(SpotLights[j].direction);

            float theta = dot(-L, spotDir);
            float outerCos = cos(radians(SpotLights[j].spotAngle * 0.5));
            float innerCos = cos(radians(SpotLights[j].innerSpotAngle * 0.5));
            float spotFade = saturate((theta - outerCos) / (innerCos - outerCos + 0.001));

            if (theta > outerCos) {
                float3 H = normalize(V + L);
                float NdotL = max(dot(N, L), 0.0);
                float NdotH = max(dot(N, H), 0.0);
                float VdotH = max(dot(V, H), 0.0);

                float atten = Attenuation(dist, SpotLights[j].range) * spotFade;
                float3 radiance = SpotLights[j].color * SpotLights[j].intensity * atten;

                // Spot shadow (#23): only the designated shadow-casting spot samples the map.
                if (j == ShadowSpotIndex && ShadowStrength > 0.0)
                    radiance *= SampleSpotShadow(input.worldPos);

                float D = D_GGX(NdotH, roughness);
                float G = G_Smith(NdotV, NdotL, roughness);
                float3 F = F_Schlick(VdotH, F0);

                float3 spec = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
                float3 kD = (1.0 - F) * (1.0 - metallic);

                Lo += (kD * albedo / PI + spec) * radiance * NdotL;
            }
        }
    }

    // AMBIENT - Reduced hemisphere lighting for realistic PBR
    float3 skyColor = float3(0.5, 0.55, 0.7);
    float3 groundColor = float3(0.15, 0.15, 0.18);
    float skyAmount = dot(N, float3(0, 1, 0)) * 0.5 + 0.5;
    float3 hemisphereLight = lerp(groundColor, skyColor, skyAmount);

    float3 ambient = hemisphereLight * AmbientStrength * albedo * ao * (1.0 - metallic);

    // Subtle rim for metals only
    float rimFresnel = pow(saturate(1.0 - NdotV), 5.0);
    float3 rimLight = rimFresnel * F0 * 0.1 * ao * metallic;

    // Environment reflection for metals
    float3 R = reflect(-V, N);
    float upFactor = R.y * 0.5 + 0.5;
    float3 envColor = lerp(float3(0.01, 0.01, 0.02), float3(0.08, 0.10, 0.15), upFactor);
    float envRoughness = roughness * roughness;
    envColor = lerp(envColor, envColor * 0.2, envRoughness);

    float3 envFresnel = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
    float3 specularAmbient = envColor * envFresnel * ao;

    ambient += specularAmbient + rimLight;

    float3 color = ambient + Lo;

    // Fog before tonemap (linear space): the flashlight cone "cuts" into the mist because lit
    // fragments still carry their radiance; distant/unlit ones converge to FogColor.
    color = ApplyFog(color, input.worldPos);

    // ACES Filmic Tone Mapping (RRT+ODT fit)
    float3 x = color * 0.5;
    float3 a = x * (x + 0.0245786) - 0.000090537;
    float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
    color = saturate(a / b);

    // Gamma Correction (sRGB)
    color = pow(max(color, 0.0), 1.0 / 2.2);

    return float4(color, alpha);
}
