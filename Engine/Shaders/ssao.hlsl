// SSAO (#32) — screen-space ambient occlusion from the dedicated half-res AO depth prepass.
// Two entry points share this file and the SSAO root signature (t0 + 16 root constants @ b0):
//   PSMain  Alchemy/McGuire-style AO: reconstruct the view-space position from depth, derive the
//           normal from position derivatives, take a per-pixel-rotated spiral of taps and
//           accumulate the angle-weighted, distance-falloff occlusion.
//   PSBlur  4-tap box blur (the spiral's rotation noise averages out; cheap and cache-friendly).
// The result darkens ONLY the ambient/indirect term in standard.hlsl (t10) — direct light, fog
// and emissive stay untouched.

cbuffer SsaoCB : register(b0)
{
    row_major float4x4 InvProj;   // clip -> view (this view's projection only, no view matrix)
    float2 TexelSize;             // 1 / AO-target size (the half-res target being written)
    float  Radius;                // world-space sample radius
    float  Intensity;             // occlusion strength multiplier
    float  Bias;                  // self-occlusion guard (view-space units)
    float  ProjScale;             // 0.5 * proj._22 * targetHeight — world radius -> pixels at z=1
    float2 _padS;
};

Texture2D    Src : register(t0);   // PSMain: AO depth (R32_FLOAT). PSBlur: raw AO (R8_UNORM).
SamplerState Smp : register(s0);   // point/clamp — depth must not be filtered

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VS_OUT VSMain(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// Reconstruct the view-space position of a pixel from its depth-buffer value.
float3 ViewPos(float2 uv)
{
    float d = Src.SampleLevel(Smp, uv, 0).r;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float4 v = mul(float4(ndc, d, 1.0), InvProj);
    return v.xyz / max(v.w, 1e-6);
}

float Hash12(float2 p)
{
    p = frac(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return frac(p.x * p.y);
}

float4 PSMain(VS_OUT i) : SV_TARGET
{
    float d = Src.SampleLevel(Smp, i.uv, 0).r;
    if (d >= 0.9999) return 1.0;   // sky / no geometry -> unoccluded

    float3 P = ViewPos(i.uv);
    // Normal from position derivatives — no G-buffer needed. Faceted on curved surfaces at
    // half res, but the blur + ambient-only application hide it completely.
    float3 N = normalize(cross(ddy(P), ddx(P)));

    // Screen-space spiral radius for the world-space Radius at this depth.
    float pixRadius = ProjScale * Radius / max(P.z, 0.1);
    pixRadius = clamp(pixRadius, 2.0, 64.0);

    const int TAPS = 12;
    float rot = Hash12(i.pos.xy) * 6.2831853;
    float occlusion = 0.0;
    [unroll]
    for (int k = 0; k < TAPS; ++k)
    {
        // Golden-angle spiral: well-distributed taps, one rotation hash per pixel.
        float a = rot + (float)k * 2.3999632;
        float r = pixRadius * sqrt(((float)k + 0.7) / (float)TAPS);
        float2 duv = float2(cos(a), sin(a)) * r * TexelSize;
        float3 S = ViewPos(i.uv + duv);
        float3 v = S - P;
        // Angle-weighted (only geometry in the normal hemisphere occludes), distance-falloff
        // (far-away samples contribute nothing — kills halos across depth discontinuities).
        occlusion += max(0.0, dot(v, N) - Bias) / (dot(v, v) + 0.01);
    }
    float ao = saturate(1.0 - Intensity * occlusion * (2.0 / (float)TAPS));
    return float4(ao, ao, ao, 1.0);
}

float4 PSBlur(VS_OUT i) : SV_TARGET
{
    float s = 0.0;
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2(-0.5, -0.5), 0).r;
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2( 1.5, -0.5), 0).r;
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2(-0.5,  1.5), 0).r;
    s += Src.SampleLevel(Smp, i.uv + TexelSize * float2( 1.5,  1.5), 0).r;
    float ao = s * 0.25;
    return float4(ao, ao, ao, 1.0);
}
