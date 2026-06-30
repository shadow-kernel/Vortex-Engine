// Minimal flat-color pipeline — the renderer's fallback PSO, used when no scene geometry has been submitted.
// Positions are already in clip space (no transform). Vertex layout = POSITION(float3) + COLOR(float3).

struct VSInput
{
    float3 position : POSITION;
    float3 color    : COLOR;
};

struct PSInput
{
    float4 position : SV_POSITION;
    float3 color    : COLOR;
};

PSInput BasicVS(VSInput input)
{
    PSInput output;
    output.position = float4(input.position, 1.0f);
    output.color = input.color;
    return output;
}

float4 BasicPS(PSInput input) : SV_TARGET
{
    return float4(input.color, 1.0f);
}
