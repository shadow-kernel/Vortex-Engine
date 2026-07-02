// Skinned-mesh vertex shader — GPU skinning variant of standard.hlsl's VSMain.
// Only the VS is compiled from this file; the pixel stage reuses standard.hlsl's PSMain blob, so the
// PS_IN struct here MUST stay semantically identical to standard.hlsl's.
//
// Bone palette: StructuredBuffer of float4 ROWS at t5, bound as a ROOT SRV (raw GPU VA — no descriptor
// heap slot needed). Each bone = 4 consecutive float4 rows forming a row-major 4x4 (same reconstruction
// pattern as the INSTANCEWORLD rows). The palette entries are inverseBind * boneWorld, computed by the
// managed AnimationService — this shader is a dumb executor.
//
// IMPORTANT: the cbuffers below are byte-identical to standard.hlsl (ABI-coupled to the C++ structs in
// DX12Renderer.h). Conventions: row-major matrices, mul(vec, mat).

cbuffer PerFrame : register(b0)
{
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float Padding0;
    float3 LightDirection;
    float DirectionalIntensity;
    float3 LightColor;
    float AmbientStrength;
    uint PointLightCount;
    uint SpotLightCount;
    uint2 FramePadding;
};

cbuffer PerObject : register(b1)
{
    row_major float4x4 World;
    float4 BaseColor;
    float Metallic;
    float Roughness;
    float AO;
    float NormalStrength;
    uint HasAlbedoTexture;
    uint HasNormalTexture;
    uint HasMetallicTexture;
    uint HasRoughnessTexture;
    uint HasAOTexture;
    uint UseDirectXNormals;
    uint IsUnlit;
    float EmissiveStrength;
};

// Bone palette rows (root SRV, t5): bone i = rows [i*4 .. i*4+3], row-major.
StructuredBuffer<float4> BoneRows : register(t5);

struct VS_IN
{
    float3 pos  : POSITION;
    float3 norm : NORMAL;
    float2 uv   : TEXCOORD0;
    uint4  boneIndices : BLENDINDICES0;   // R8G8B8A8_UINT, offset 32 in the 52-byte skinned vertex
    float4 boneWeights : BLENDWEIGHT0;    // offset 36, normalized at import
    // Per-instance world matrix (4 rows) streamed from vertex slot 1 — same as standard.hlsl.
    float4 iw0 : INSTANCEWORLD0;
    float4 iw1 : INSTANCEWORLD1;
    float4 iw2 : INSTANCEWORLD2;
    float4 iw3 : INSTANCEWORLD3;
};

struct PS_IN
{
    float4 pos       : SV_POSITION;
    float3 worldPos  : TEXCOORD1;
    float3 norm      : TEXCOORD2;
    float2 uv        : TEXCOORD0;
    float3 tangent   : TEXCOORD3;
    float3 bitangent : TEXCOORD4;
};

float4x4 LoadBone(uint i)
{
    uint b = i * 4;
    return float4x4(BoneRows[b], BoneRows[b + 1], BoneRows[b + 2], BoneRows[b + 3]);
}

PS_IN VSMain(VS_IN input)
{
    PS_IN output;

    // 4-influence linear-blend skinning in mesh space, BEFORE the instance-world multiply.
    float4x4 skin =
        input.boneWeights.x * LoadBone(input.boneIndices.x) +
        input.boneWeights.y * LoadBone(input.boneIndices.y) +
        input.boneWeights.z * LoadBone(input.boneIndices.z) +
        input.boneWeights.w * LoadBone(input.boneIndices.w);

    float4 skinnedPos = mul(float4(input.pos, 1), skin);
    float3 skinnedNorm = normalize(mul(input.norm, (float3x3)skin));

    float4x4 World = float4x4(input.iw0, input.iw1, input.iw2, input.iw3);
    float4 worldPos = mul(skinnedPos, World);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjection);
    output.norm = normalize(mul(skinnedNorm, (float3x3)World));
    output.uv = input.uv;

    float3 N = output.norm;
    float3 T = normalize(cross(N, float3(0, 1, 0)));
    if (length(T) < 0.001) T = normalize(cross(N, float3(1, 0, 0)));
    float3 B = normalize(cross(N, T));
    output.tangent = T;
    output.bitangent = B;

    return output;
}
