// Temporal anti-aliasing (#141): the previous resolved frame reprojected through this frame's depth and the previous
// view-projection, clamped to this frame's 3x3 neighbourhood, blended 88 / 12 with the new frame. Runs on the LDR picture
// before the grade / FXAA / vignette (SdlGpuRenderer::record_taa). The scene is rasterised a sub-pixel off every frame
// (SdlGpuRenderer::apply_taa_jitter); this frame's colour at the pixel centre is rebuilt from the 3x3 samples weighted by
// where they really sit, so the history gathers the pixel's coverage instead of one wobbling point sample.
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
    float4 jitter;     // xy: how far this frame's samples sit off their pixel centres (uv); zw unused
};


// The history read with a Catmull-Rom kernel (nine texels, five bilinear fetches): a bilinear tap re-blurs the history a
// little every frame and the picture went soft; the cubic keeps the detail the clamp lets through.
static inline float3 sample_history(texture2d<float> tex, sampler smp, float2 uv, float2 texel)
{
    float2 size = 1.0 / texel;
    float2 pos = uv * size;
    float2 center = floor(pos - 0.5) + 0.5;
    float2 f = pos - center;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 off12 = w2 / w12;
    float2 p0 = (center - 1.0) * texel, p3 = (center + 2.0) * texel, p12 = (center + off12) * texel;
    float3 r = tex.sample(smp, float2(p12.x, p0.y)).rgb * (w12.x * w0.y);
    r += tex.sample(smp, float2(p0.x, p12.y)).rgb * (w0.x * w12.y);
    r += tex.sample(smp, float2(p12.x, p12.y)).rgb * (w12.x * w12.y);
    r += tex.sample(smp, float2(p3.x, p12.y)).rgb * (w3.x * w12.y);
    r += tex.sample(smp, float2(p12.x, p3.y)).rgb * (w12.x * w3.y);
    return max(r, 0.0);
}

fragment float4 TaaPS(BlitOut in [[stage_in]], constant TaaCB& c [[buffer(0)]],
                      texture2d<float> cur [[texture(0)]], sampler cur_smp [[sampler(0)]],
                      texture2d<float> hist [[texture(1)]], sampler hist_smp [[sampler(1)]],
                      depth2d<float> depth [[texture(2)]], sampler depth_smp [[sampler(2)]])
{
    float2 uv = in.uv;
    // the neighbourhood box the history may not leave (what changed since last frame is not ghosted), and this frame's
    // colour at the pixel centre: each sample sits `jitter` off its own centre, weighted by its distance in pixels
    // (the Blackman-Harris fit exp(-2.29 d^2)) — without jitter the centre sample dominates
    float2 jp = c.jitter.xy / c.texel;
    float3 mn = float3(1e5), mx = float3(-1e5), sum = float3(0.0);
    float wsum = 0.0;
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
        {
            float3 n = cur.sample(cur_smp, uv + float2(x, y) * c.texel).rgb;
            mn = min(mn, n); mx = max(mx, n);
            float2 d = float2(x, y) - jp;
            float wk = exp(-2.29 * dot(d, d));
            sum += n * wk; wsum += wk;
        }
    // no jitter (VORTEX_NO_TAA_JITTER): the centre sample as it is, the plain TAA
    float3 c0 = any(c.jitter.xy != 0.0) ? sum / max(wsum, 1e-4) : cur.sample(cur_smp, uv).rgb;
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
            h = clamp(sample_history(hist, hist_smp, puv, c.texel), mn, mx);
        }
        else w = 1.0;
    }
    else w = 1.0;
    return float4(mix(h, c0, w), 1.0);
}
