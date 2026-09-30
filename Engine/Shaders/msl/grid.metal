// Editor viewport floor grid — Metal port of grid.hlsl. Fullscreen triangle, per-pixel ray vs y=0 plane,
// anti-aliased minor/major lines + colored X/Z axes with distance fade; writes depth so it sorts with the scene.
#include <metal_stdlib>
using namespace metal;

struct GridConstants
{
    float4x4 view_projection;
    float4x4 inverse_view_projection;
    packed_float3 camera_position;
    float spacing;
    float extent;
    float major;
    float pad[2];
};

struct GridOut
{
    float4 pos [[position]];
    float3 near_pt;
    float3 far_pt;
};

vertex GridOut GridVS(uint id [[vertex_id]], constant GridConstants& c [[buffer(0)]])
{
    GridOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    float2 ndc = uv * 2.0 - 1.0;
    o.pos = float4(ndc.x, -ndc.y, 0.0, 1.0);
    float4 near_pt = c.inverse_view_projection * float4(ndc.x, -ndc.y, 0.0, 1.0);
    float4 far_pt  = c.inverse_view_projection * float4(ndc.x, -ndc.y, 1.0, 1.0);
    o.near_pt = near_pt.xyz / near_pt.w;
    o.far_pt  = far_pt.xyz / far_pt.w;
    return o;
}

struct GridFragment
{
    float4 color [[color(0)]];
    float depth [[depth(any)]];
};

static inline float grid_line(float3 p, float s)
{
    float2 c = p.xz / s;
    float2 d = fwidth(c);
    float2 g = abs(fract(c - 0.5) - 0.5) / d;
    return 1.0 - min(min(g.x, g.y), 1.0);
}

fragment GridFragment GridPS(GridOut i [[stage_in]], constant GridConstants& c [[buffer(0)]])
{
    float3 dir = i.far_pt - i.near_pt;
    if (abs(dir.y) < 0.0001) discard_fragment();
    float t = -i.near_pt.y / dir.y;
    if (t < 0.0) discard_fragment();

    float3 p = i.near_pt + t * dir;
    float3 cam = float3(c.camera_position);
    float dist = length(p.xz - cam.xz);
    if (dist > c.extent) discard_fragment();

    float fade = 1.0 - (dist / c.extent);
    fade = fade * fade;

    float g1 = grid_line(p, c.spacing) * 0.4;
    float g2 = grid_line(p, c.spacing * c.major) * 0.7;
    float g = saturate(g1 + g2);

    float3 bg = float3(0.15, 0.15, 0.18);
    float3 line_color = float3(0.5, 0.5, 0.5);
    float axis_w = c.spacing * min(fwidth(p.x / c.spacing), 1.0);
    if (abs(p.x) < axis_w) line_color = float3(0.2, 0.4, 1.0);
    if (abs(p.z) < axis_w) line_color = float3(1.0, 0.3, 0.3);

    float3 col = mix(bg, line_color, g);
    float alpha = fade;
    if (alpha < 0.01) discard_fragment();

    float4 clip = c.view_projection * float4(p, 1.0);
    GridFragment o;
    o.depth = clip.z / clip.w;
    o.color = float4(col, alpha);
    return o;
}
