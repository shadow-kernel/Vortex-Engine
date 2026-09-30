// 2D UI overlay (rects with rounded corners, text glyphs, images, lines) drawn over the finished frame.
// Vertex: screen-space position in overlay units, uv, color, misc = (mode, radius, half_w, half_h), local
// = rect-local pixel position for the rounded-corner distance field. mode: 0 solid, 1 text (alpha from the
// R8 glyph atlas), 2 image (RGBA).
#include <metal_stdlib>
using namespace metal;

struct OverlayConstants
{
    float2 screen_size;
    float2 padding;
};

struct OverlayVertexIn
{
    float2 pos   [[attribute(0)]];
    float2 uv    [[attribute(1)]];
    float4 color [[attribute(2)]];
    float4 misc  [[attribute(3)]];
    float2 local [[attribute(4)]];
};

struct OverlayOut
{
    float4 pos [[position]];
    float2 uv;
    float4 color;
    float4 misc;
    float2 local;
};

vertex OverlayOut OverlayVS(OverlayVertexIn in [[stage_in]], constant OverlayConstants& c [[buffer(0)]])
{
    OverlayOut o;
    float2 ndc = float2(in.pos.x / c.screen_size.x * 2.0 - 1.0, 1.0 - in.pos.y / c.screen_size.y * 2.0);
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv = in.uv;
    o.color = in.color;
    o.misc = in.misc;
    o.local = in.local;
    return o;
}

fragment float4 OverlayPS(OverlayOut in [[stage_in]], texture2d<float> tex [[texture(0)]], sampler smp [[sampler(0)]])
{
    float4 color = in.color;
    int mode = (int)in.misc.x;
    if (mode == 1)
    {
        color.a *= tex.sample(smp, in.uv).r;
    }
    else if (mode == 2)
    {
        color *= tex.sample(smp, in.uv);
    }
    else if (in.misc.y > 0.5)
    {
        // Rounded rectangle: signed distance to the rounded box in rect-local pixels, 1px anti-aliased edge.
        float2 half_size = in.misc.zw;
        float radius = min(in.misc.y, min(half_size.x, half_size.y));
        float2 q = abs(in.local) - (half_size - radius);
        float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        color.a *= saturate(0.5 - d);
    }
    return color;
}
