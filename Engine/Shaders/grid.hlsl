// Editor viewport floor grid: a fullscreen pass that reconstructs a world-space ray per pixel, intersects the
// y=0 plane, and draws anti-aliased grid lines + colored X/Z axes with distance fade. Writes SV_DEPTH so the
// grid sorts against scene geometry. (Editor-only; the shipped game disables the grid.)

cbuffer CB : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 InvViewProjection;
    float3 CamPos;
    float  Spacing;
    float  Extent;
    float  Major;
    float2 Pad;
};

struct VS_OUT
{
    float4 pos      : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float3 near     : TEXCOORD1;
    float3 far      : TEXCOORD2;
};

VS_OUT GridVS(uint id : SV_VertexID)
{
    VS_OUT o;
    float2 uv = float2((id << 1) & 2, id & 2);
    float2 ndc = uv * 2.0 - 1.0;

    o.pos = float4(ndc.x, -ndc.y, 0, 1);

    float4 nearPt = mul(float4(ndc.x, -ndc.y, 0, 1), InvViewProjection);
    float4 farPt  = mul(float4(ndc.x, -ndc.y, 1, 1), InvViewProjection);

    o.near = nearPt.xyz / nearPt.w;
    o.far  = farPt.xyz / farPt.w;
    o.worldPos = o.near;

    return o;
}

struct PS_OUT
{
    float4 color : SV_TARGET;
    float  depth : SV_DEPTH;
};

float Grid(float3 p, float s)
{
    float2 c = p.xz / s;
    float2 d = fwidth(c);
    float2 g = abs(frac(c - 0.5) - 0.5) / d;
    return 1.0 - min(min(g.x, g.y), 1.0);
}

PS_OUT GridPS(VS_OUT i)
{
    PS_OUT o;

    float3 dir = i.far - i.near;
    if (abs(dir.y) < 0.0001) discard;

    float t = -i.near.y / dir.y;
    if (t < 0) discard;

    float3 p = i.near + t * dir;
    float dist = length(p.xz - CamPos.xz);

    if (dist > Extent) discard;

    float fade = 1.0 - (dist / Extent);
    fade = fade * fade;

    float g1 = Grid(p, Spacing) * 0.4;
    float g2 = Grid(p, Spacing * Major) * 0.7;
    float g = saturate(g1 + g2);

    // Solid dark background for the grid floor
    float3 bgColor = float3(0.15, 0.15, 0.18);
    float3 lineColor = float3(0.5, 0.5, 0.5);

    float axisW = Spacing * min(fwidth(p.x / Spacing), 1.0);
    if (abs(p.x) < axisW) lineColor = float3(0.2, 0.4, 1.0);
    if (abs(p.z) < axisW) lineColor = float3(1.0, 0.3, 0.3);

    // Blend grid lines over solid background
    float3 col = lerp(bgColor, lineColor, g);

    // Use fade for edge transparency only, not for the whole floor
    float alpha = fade;
    if (alpha < 0.01) discard;

    float4 clip = mul(float4(p, 1), ViewProjection);
    o.depth = clip.z / clip.w;
    o.color = float4(col, alpha);

    return o;
}
