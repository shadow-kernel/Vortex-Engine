// Procedural gradient skybox — Metal port of skybox.hlsl (sky/horizon/ground gradient + sun disc + glow), the equirect
// texture sky (#326) with the sun's disc at the scene's directional light (#499), and up to two more pictures cross-faded
// over either (#464).
#include <metal_stdlib>
using namespace metal;

struct SkyboxConstants
{
    float4x4 inverse_view_projection;
    packed_float3 camera_position; float padding0;
    packed_float3 sky_color;       float padding1;
    packed_float3 horizon_color;   float padding2;
    packed_float3 ground_color;    float padding3;
    packed_float3 sun_direction;   float sun_intensity;   // unused since #499: the disc follows the scene's light (glow_dir / glow_color)
    packed_float3 sun_color;       float tonemapper;   // the HDR path's curve: 0 = ACES fit, 1 = PBR Neutral
    float4 params;        // @160  x: 1 = sample the equirect texture (#326), y: exposure, z: yaw offset (radians), w: the HDR mode
    float4 haze;          // @176  aerial perspective on the HDR sky: rgb fog colour, w density (0 = off)
    float4 glow_dir;      // @192  xyz the direction the scene's sun light travels (normalised), w the fog's sun glow
    float4 glow_color;    // @208  rgb the sun's colour, w its intensity (0 = no sun)
    float4 sun_disc;      // @224  the sun's disc (#499): x angular radius (radians), y intensity, z glow, w the layers that
                          //       draw it (bit 0 the sky, bits 1 / 2 the cross-fade pictures)
    float4 blend1;        // @240  a picture cross-faded over the sky (#464): x amount (0 = off), y exposure, z yaw (radians),
    float4 blend2;        // @256  w its HDR mode like params.w — composited in order: sky, then blend1, then blend2
};

// The sun's disc at the scene's directional light (#499), with a corona hugging it and the wide aureole of the gradient
// sky. `hdr`: linear radiance before the scene exposure + tonemapper (it blooms through them like a lamp); else the display
// values of the gradient / an 8-bit picture. The disc is the light's: rotating the picture or changing its exposure does
// not move or dim it. Occlusion is free — the sky pass runs first, the world covers it.
static inline float3 sun_disc(float3 dir, constant SkyboxConstants& c, bool hdr)
{
    float3 to_sun = -c.glow_dir.xyz;
    float d = length(dir - to_sun);                              // the chord to the sun ~ the angle (radians)
    float t = d / max(c.sun_disc.x, 1e-5);                       // 0 at the centre, 1 at the rim
    float aa = clamp(fwidth(t), 0.02, 0.5);                      // ~1 pixel of edge, before the branch below
    // the aureole is gone 17° from the sun, the corona 9 radii out: the rest of the sky pays a length and a compare
    if (d > max(0.3, 9.0 * c.sun_disc.x)) return float3(0.0);
    float disc = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, t);
    float limb = 0.6 + 0.4 * sqrt(saturate(1.0 - t * t));       // limb darkening
    float corona = exp(-max(t - 1.0, 0.0) * 0.8);
    float aureole = pow(saturate(1.0 - 0.5 * d * d), 256.0);    // cos of the angle, from the chord
    float3 col = float3(c.glow_color.rgb);
    return hdr ? col * (disc * limb * 40.0 * c.sun_disc.y + c.sun_disc.z * (1.5 * corona + 0.35 * aureole))
               : col * (disc * limb * 10.0 * c.sun_disc.y + c.sun_disc.z * (0.5 * corona + 0.5 * aureole));
}

struct SkyOut
{
    float4 pos [[position]];
    float3 world_dir;
};

vertex SkyOut SkyVS(uint id [[vertex_id]], constant SkyboxConstants& c [[buffer(0)]])
{
    SkyOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
    o.pos.y = -o.pos.y;
    float4 clip = float4(uv * 2.0 - 1.0, 1.0, 1.0);
    clip.y = -clip.y;
    float4 world = c.inverse_view_projection * clip;
    o.world_dir = world.xyz / world.w - float3(c.camera_position);
    return o;
}

// One equirect picture of the sky (#326) as the display values the pass writes. Mip 0: the atan2 seam would otherwise
// pull the smallest mip into a visible line. mode: > 0 a float picture through the haze, the scene exposure and the
// tonemapper (the value is the exposure); < 0 a float picture the classic way (gamma-encoded, clamped); 0 an 8-bit one
// as it is. The sun's disc goes in before the tonemapper where there is one, else onto the display values.
// (u0, v): the view direction's equirect coordinates without a yaw, shared by the layers.
static inline float3 sky_picture(float3 dir, float u0, float v, texture2d<float> tex, sampler smp, constant SkyboxConstants& c,
                                 float exposure, float rotation, float mode, bool sun)
{
    float y = dir.y;
    float3 sky = tex.sample(smp, float2(u0 + rotation / 6.28318530718, v), level(0)).rgb * exposure;
    if (mode > 0.0 && c.haze.w > 0.0)
    {
        // aerial perspective on the sky: the horizon whitens into the scene's haze (the path through the air grows as
        // 1 / sin(elevation)), the sun glows in it, the zenith keeps its blue
        float h = max(y, 0.0);
        float f = 1.0 - exp(-c.haze.w * 40.0 / (h + 0.04));
        // below the horizon the picture holds a blurred ground that read as a blue sea once the world ended under a high
        // camera: that half is the haze (it follows the sky's own horizon), blended in over the first few degrees
        f = max(f, smoothstep(0.0, 0.08, -y));
        float mu = saturate(dot(dir, normalize(-c.glow_dir.xyz)));
        float3 hz = c.haze.rgb + c.glow_color.rgb * (c.glow_dir.w * (0.12 * mu * mu + 0.88 * pow(mu, 10.0)));
        sky = mix(sky, hz, f);
    }
    if (sun && mode > 0.0) sky += sun_disc(dir, c, true);   // on top of the haze: a setting sun stays a disc
    if (mode < 0.0) sky = pow(saturate(sky), 1.0 / 2.2);   // a float picture the classic way: gamma-encoded, clamped
    else if (mode > 0.0)
    {
        // an HDR picture: the scene exposure + ACES + gamma of the lit surfaces, so the sky and the world agree
        float3 x = max(sky * mode, 0.0);
        if (c.tonemapper > 0.5)
        {
            // Khronos PBR Neutral, as in standard.metal
            float mn = min(x.r, min(x.g, x.b));
            x -= mn < 0.08 ? mn - 6.25 * mn * mn : 0.04;
            float peak = max(x.r, max(x.g, x.b));
            if (peak >= 0.76)
            {
                float new_peak = 1.0 - 0.0576 / (peak + 0.24 - 0.76);
                x *= new_peak / peak;
                float g = 1.0 - 1.0 / (0.15 * (peak - new_peak) + 1.0);
                x = mix(x, float3(new_peak), g);
            }
            sky = pow(saturate(x), 1.0 / 2.2);
        }
        else
        {
            float3 a = x * (x + 0.0245786) - 0.000090537;
            float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
            sky = pow(max(saturate(a / b), 0.0), 1.0 / 2.2);
        }
    }
    if (sun && mode <= 0.0) sky += sun_disc(dir, c, false);   // an 8-bit picture / the classic float one: display values
    return sky;
}

// The three-colour gradient sky, its sun and a faint noise.
static inline float3 sky_gradient(float3 dir, constant SkyboxConstants& c, bool sun)
{
    float y = dir.y;
    float3 color;
    if (y > 0.0)
    {
        float t = pow(y, 0.4);
        color = mix(float3(c.horizon_color), float3(c.sky_color), t);
    }
    else
    {
        float t = pow(-y, 0.7);
        color = mix(float3(c.horizon_color), float3(c.ground_color), t);
    }

    // the sun at the scene's directional light (#499 — it used to sit at a fixed direction whatever the light did)
    if (sun)
    {
        color += sun_disc(dir, c, false);
        float horizon_glow = pow(1.0 - abs(y), 4.0) * pow(max(dot(dir, normalize(-c.glow_dir.xyz)), 0.0), 2.0);
        color += c.glow_color.rgb * horizon_glow * 0.3 * c.sun_disc.z;
    }

    float noise = fract(sin(dot(dir.xz, float2(12.9898, 78.233))) * 43758.5453);
    color += (noise - 0.5) * 0.01;
    return color;
}

fragment float4 SkyPS(SkyOut in [[stage_in]], constant SkyboxConstants& c [[buffer(0)]],
                      texture2d<float> sky_tex [[texture(0)]], sampler sky_smp [[sampler(0)]],
                      texture2d<float> blend1_tex [[texture(1)]], sampler blend1_smp [[sampler(1)]],
                      texture2d<float> blend2_tex [[texture(2)]], sampler blend2_smp [[sampler(2)]])
{
    float3 dir = normalize(in.world_dir);
    // the layers that draw the sun's disc (#499): bit 0 the sky, bits 1 / 2 the cross-fade pictures
    const uint sun = c.glow_color.w > 0.001 ? (uint)c.sun_disc.w : 0u;
    // the equirect coordinates once for every picture layer (a gradient sky alone needs none)
    float u0 = 0.0, v = 0.0;
    if (c.params.x > 0.5 || c.blend1.x > 0.0 || c.blend2.x > 0.0)
    {
        u0 = atan2(dir.x, dir.z) / 6.28318530718 + 0.5;
        v = acos(clamp(dir.y, -1.0, 1.0)) / 3.14159265359;
    }
    // the sky: an equirect picture sampled at the far plane, no depth write, centred on the rendering camera — or the gradient
    float3 col = c.params.x > 0.5 ? sky_picture(dir, u0, v, sky_tex, sky_smp, c, c.params.y, c.params.z, c.params.w, (sun & 1u) != 0u)
                                  : sky_gradient(dir, c, (sun & 1u) != 0u);
    // the cross-fade (#464): up to two more pictures over it, each by its amount — one at 0 is not sampled at all
    if (c.blend1.x > 0.0) col = mix(col, sky_picture(dir, u0, v, blend1_tex, blend1_smp, c, c.blend1.y, c.blend1.z, c.blend1.w, (sun & 2u) != 0u), c.blend1.x);
    if (c.blend2.x > 0.0) col = mix(col, sky_picture(dir, u0, v, blend2_tex, blend2_smp, c, c.blend2.y, c.blend2.z, c.blend2.w, (sun & 4u) != 0u), c.blend2.x);
    return float4(col, 1.0);
}
