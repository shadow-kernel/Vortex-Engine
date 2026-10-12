// Temporal anti-aliasing (#141), the plain version: the previous resolved frame reprojected through this frame's depth and
// the previous view-projection, clamped to this frame's 3x3 neighbourhood, blended 88 / 12 with the new frame. Runs on the
// LDR picture before the grade / FXAA / vignette (SdlGpuRenderer::record_taa). No projection jitter yet.
#include <metal_stdlib>
using namespace metal;

struct BlitOut
{
    float4 pos [[position]];
    float2 uv;
};

struct TaaCB
{
    float4x4 reproj;   // this frame's NDC (x, y, depth, 1) -> the previous frame's clip space
    float2 texel; float blend; float valid;
};

fragment float4 TaaPS(BlitOut in [[stage_in]], constant TaaCB& c [[buffer(0)]],
                      texture2d<float> cur [[texture(0)]], sampler cur_smp [[sampler(0)]],
                      texture2d<float> hist [[texture(1)]], sampler hist_smp [[sampler(1)]],
                      depth2d<float> depth [[texture(2)]], sampler depth_smp [[sampler(2)]])
{
    float2 uv = in.uv;
    float3 c0 = cur.sample(cur_smp, uv).rgb;
    // the neighbourhood box the history may not leave (what changed since last frame is not ghosted)
    float3 mn = c0, mx = c0;
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
        {
            if (x == 0 && y == 0) continue;
            float3 n = cur.sample(cur_smp, uv + float2(x, y) * c.texel).rgb;
            mn = min(mn, n); mx = max(mx, n);
        }
    // where this pixel was last frame
    float z = depth.sample(depth_smp, uv);
    float4 pc = c.reproj * float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, z, 1.0);
    float w = c.blend;
    float3 h = c0;
    if (c.valid > 0.5 && pc.w > 0.0001)
    {
        float2 puv = float2(pc.x / pc.w * 0.5 + 0.5, 0.5 - pc.y / pc.w * 0.5);
        if (puv.x >= 0.0 && puv.x <= 1.0 && puv.y >= 0.0 && puv.y <= 1.0)
        {
            h = clamp(hist.sample(hist_smp, puv).rgb, mn, mx);
        }
        else w = 1.0;
    }
    else w = 1.0;
    return float4(mix(h, c0, w), 1.0);
}
