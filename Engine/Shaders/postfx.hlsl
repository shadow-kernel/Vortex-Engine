// Post-processing uber-pass (#28 framework / #29 pack 1) — vignette + film grain + chromatic
// aberration in ONE fullscreen pass (feature bits in Flags avoid three RT round-trips), plus a
// trivial invert used only to verify the chain's ping-pong plumbing. Runs at output resolution
// between the upscale/DLSS composite and the UI overlay; reads color only, writes no depth.
//
// The PostFx cbuffer is byte-matched to DX12PostFxChain::PassCB — keep both in sync.

cbuffer PostFx : register(b0)
{
    float2 TexelSize;      // 1 / output size
    float  Time;           // seconds since renderer start (grain re-seed)
    uint   Flags;          // 1 vignette, 2 grain, 4 chromatic aberration, 8 invert, 16 color grading
    float4 Vignette;       // x intensity, y smoothness, z roundness (1 = circular), w unused
    float4 VignetteColor;  // rgb linear, w unused
    float4 GrainCA;        // x grain intensity, y grain size (px), z CA strength, w CA radial falloff
    float4 Grade1;         // x exposure (stops), y contrast, z saturation, w temperature (-1..1)
    float4 Grade2;         // x tint (-1..1 green<->magenta), yzw reserved
};

Texture2D    Src : register(t0);
SamplerState Smp : register(s0);

struct VS_OUT
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

// Fullscreen triangle from SV_VertexID — no vertex buffer, no input layout (same trick as upscale.hlsl).
VS_OUT VSMain(uint vertexID : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    o.uv = uv;
    return o;
}

// Cheap 2D hash (no noise texture): stable per grain cell, re-seeded per frame via Time.
float Hash21(float2 p)
{
    p = frac(p * float2(443.8975, 397.2973));
    p += dot(p, p.yx + 19.19);
    return frac(p.x * p.y);
}

float4 PSMain(VS_OUT i) : SV_TARGET
{
    float2 uv = i.uv;
    float3 col;

    if (Flags & 4)
    {
        // Chromatic aberration: radial RGB split growing towards the screen edges. Strength is in
        // "percent of half-screen" units (0.35 = subtle horror unease, 2+ = heavy VHS smear).
        float2 fromC = uv - 0.5;
        float  r = saturate(length(fromC) * 2.0);
        float  amt = GrainCA.z * 0.01 * pow(r, max(GrainCA.w, 0.01));
        col.r = Src.Sample(Smp, uv + fromC * amt).r;
        col.g = Src.Sample(Smp, uv).g;
        col.b = Src.Sample(Smp, uv - fromC * amt).b;
    }
    else
    {
        col = Src.Sample(Smp, uv).rgb;
    }

    if (Flags & 2)
    {
        // Film grain: hash noise per size-px cell, luminance-weighted so shadows grain more than
        // highlights (film-negative behaviour — and horror lives in the shadows).
        float2 cell = floor(i.pos.xy / max(GrainCA.y, 1.0));
        float  n = Hash21(cell + frac(Time * float2(17.131, 3.7171)) * 289.17) * 2.0 - 1.0;
        float  luma = dot(col, float3(0.299, 0.587, 0.114));
        col = saturate(col + n * GrainCA.x * 0.25 * (1.0 - saturate(luma)));
    }

    if (Flags & 16)
    {
        // Color grading (#31): exposure -> white balance -> contrast -> saturation. Order matters (photographic).
        col *= exp2(Grade1.x);                                   // exposure in stops (2^EV)

        // White balance: warm/cool (temperature) on the R/B axis, green/magenta (tint) on the G axis.
        // Cheap channel scale around 1 — enough for a mood shift without a full chromatic-adaptation matrix.
        float3 wb = float3(1.0 + Grade1.w * 0.2, 1.0 + Grade2.x * 0.2, 1.0 - Grade1.w * 0.2);
        col *= wb;

        col = (col - 0.5) * max(Grade1.y, 0.0) + 0.5;            // contrast around mid-grey
        float luma = dot(col, float3(0.299, 0.587, 0.114));      // saturation: lerp from luminance
        col = lerp(luma.xxx, col, Grade1.z);
        col = max(col, 0.0);
    }

    if (Flags & 1)
    {
        // Vignette: intensity scales the center distance, smoothness the falloff curve, roundness 1
        // aspect-corrects to a true circle (0 hugs the screen shape). Darkens towards VignetteColor.
        float  aspect = TexelSize.y / TexelSize.x;   // = width / height
        float2 d = (uv - 0.5) * 2.0 * Vignette.x;
        d.x *= lerp(1.0, aspect, Vignette.z);
        float  vig = pow(saturate(1.0 - dot(d, d)), Vignette.y * 4.0 + 0.001);
        col = lerp(VignetteColor.rgb, col, vig);
    }

    if (Flags & 8)
        col = 1.0 - col;   // chain-verification pass only — never enabled in production

    return float4(col, 1.0);
}
