// Procedural gradient skybox: sky->horizon->ground vertical gradient plus a sun disc/glow and faint noise,
// reconstructed per-pixel from a fullscreen triangle's world-space view direction. SunDirection points FROM the
// sun (the PS negates it). cbuffer layout is ABI-coupled to SkyboxConstants in C++ — keep field order + padding.
#define PI 3.14159265359

cbuffer SkyboxConstants : register(b0)
{
    row_major float4x4 InverseViewProjection;
    float3 CameraPosition; float Padding0;
    float3 SkyColor;       float Padding1;
    float3 HorizonColor;   float Padding2;
    float3 GroundColor;    float Padding3;
    float3 SunDirection;   float SunIntensity;
    float3 SunColor;       float Padding4;
};

struct VS_OUT
{
    float4 pos      : SV_POSITION;
    float3 worldDir : TEXCOORD0;
};

VS_OUT SkyVS(uint vertexID : SV_VertexID)
{
    VS_OUT output;

    // Full-screen triangle
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    output.pos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
    output.pos.y = -output.pos.y;

    // Transform to a world-space view direction
    float4 clipPos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
    clipPos.y = -clipPos.y;
    float4 worldPos = mul(clipPos, InverseViewProjection);
    output.worldDir = worldPos.xyz / worldPos.w - CameraPosition;

    return output;
}

float4 SkyPS(VS_OUT input) : SV_TARGET
{
    float3 dir = normalize(input.worldDir);
    float y = dir.y;

    // Sky to horizon to ground gradient
    float3 color;
    if (y > 0)
    {
        float t = pow(y, 0.4); // smooth falloff
        color = lerp(HorizonColor, SkyColor, t);
    }
    else
    {
        float t = pow(-y, 0.7);
        color = lerp(HorizonColor, GroundColor, t);
    }

    // Sun disc + glow + horizon scattering
    if (SunIntensity > 0.001)
    {
        float3 sunDir = normalize(-SunDirection);
        float sunDot = dot(dir, sunDir);

        float sunDisc = smoothstep(0.9995, 0.9999, sunDot);
        color += SunColor * sunDisc * SunIntensity * 10.0;

        float sunGlow = pow(max(sunDot, 0.0), 256.0);
        color += SunColor * sunGlow * SunIntensity * 0.5;

        float horizonGlow = pow(1.0 - abs(y), 4.0) * pow(max(sunDot, 0.0), 2.0);
        color += SunColor * horizonGlow * SunIntensity * 0.3;
    }

    // Subtle noise for texture
    float noise = frac(sin(dot(dir.xz, float2(12.9898, 78.233))) * 43758.5453);
    color += (noise - 0.5) * 0.01;

    return float4(color, 1.0);
}
