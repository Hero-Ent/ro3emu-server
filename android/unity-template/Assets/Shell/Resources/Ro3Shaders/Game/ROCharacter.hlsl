// Shared by the ports of ROMeta/3DCharacter/ShowBase (character select, RO_SHOWBASE) and BattleBase (in the world),
// from their DXBC disassembly (android\tools\ShaderDis.cs): the toon look (base colour, shadow colour band from the
// _DirectionControl/view half vector, metal tint, matcap, rim light, dithered fade) and HappyEngine.DOTSAnimation
// skinning. Left out: the state effects (stone, frozen, dissolve, glitter, selection outline), faker point lights,
// cast shadows, rain and fog.
#ifndef RO_CHARACTER_INCLUDED
#define RO_CHARACTER_INCLUDED

#include "Assets/Ro3Shaders/Lib/universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _StreamEffectTex_ST;
    float4 _Sequence;               // columns, rows, frame
    float4 _BaseColor;
    float4 _AC_Color;
    float4 _ShadowColor;
    float4 _DirectionControl;
    float4 _RimLightColor;
    float4 _EmissionColor;
    float4 _StreamEffectColor;
    float4 _StreamTilingOffset;
    float4 _DarkColor;
    float _LightingThr;
    float _SoftEdge;
    float _RimRange;
    float _RimIntensity;
    float _LocalMetalStrength;
    float _StreamEffectIntensity;
    float _uvStreamEffectSpeedX;
    float _uvStreamEffectSpeedY;
    float _BloomThreshold;
    float _MatcapStrength;
    float _MatcapContrast;
    float _Cutoff;
    float _Alpha;
    float _RemoveBloom;
    float _MatCapToggle;
CBUFFER_END
float4 _PlayerLightColor;

TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
TEXTURE2D(_StreamEffectTex); SAMPLER(sampler_StreamEffectTex);
TEXTURE2D(_ShadowTex); SAMPLER(sampler_ShadowTex);
TEXTURE2D(_MatCapTex); SAMPLER(sampler_MatCapTex);

#ifdef GPUSKIN_ON
// The CPU writes each skinning matrix (rows 0-2, 3 RGBAFloat texels) into _GlobalBoneMatricesTexture; a renderer's
// first matrix is in unity_LightmapST.x (y = 1: not skinned).
TEXTURE2D_FLOAT(_GlobalBoneMatricesTexture);
float4 _GlobalBoneMatricesTextureSize;      // width, height, 0, group height

float3x4 BoneMatrix(int index)
{
    int width = (int)_GlobalBoneMatricesTextureSize.x;
    int groupHeight = (int)_GlobalBoneMatricesTextureSize.w;
    int perGroup = groupHeight * width / 3;
    int group = index / perGroup;
    int rest = index % perGroup;
    int2 texel = int2(rest / groupHeight * 3, rest % groupHeight + group * groupHeight);
    return float3x4(LOAD_TEXTURE2D(_GlobalBoneMatricesTexture, texel),
                    LOAD_TEXTURE2D(_GlobalBoneMatricesTexture, texel + int2(1, 0)),
                    LOAD_TEXTURE2D(_GlobalBoneMatricesTexture, texel + int2(2, 0)));
}
#endif

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    float3 normalOS : NORMAL;
#ifdef GPUSKIN_ON
    uint4 indices : BLENDINDICES;
    float4 weights : BLENDWEIGHTS;
#endif
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float4 uv : TEXCOORD0;          // xy: base map (sequence frame), zw: stream effect
    float2 matcapUV : TEXCOORD1;
    float3 positionWS : TEXCOORD2;
    float3 normalWS : TEXCOORD3;
};

Varyings CharacterVert (Attributes v)
{
    Varyings o;
    float3 position = v.positionOS.xyz;
    float3 normal = v.normalOS;
#ifdef GPUSKIN_ON
    if (unity_LightmapST.y < 0.5 && _GlobalBoneMatricesTextureSize.w > 0 && dot(v.weights, 1) > 0.001)
    {
        int first = (int)unity_LightmapST.x;
        float3x4 skin = BoneMatrix(first + v.indices.x) * v.weights.x + BoneMatrix(first + v.indices.y) * v.weights.y
                      + BoneMatrix(first + v.indices.z) * v.weights.z + BoneMatrix(first + v.indices.w) * v.weights.w;
        position = mul(skin, float4(position, 1));
        normal = mul((float3x3)skin, normal);
    }
#endif
    o.positionWS = TransformObjectToWorld(position);
    o.positionCS = TransformWorldToHClip(o.positionWS);
    o.normalWS = normalize(TransformObjectToWorldNormal(normal));

    // Flip-book frame of the base map (_Sequence: columns, rows, frame).
    float2 uv = v.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
    float columns = max(_Sequence.x, 1), rows = max(_Sequence.y, 1);
    float frames = columns * rows;
    float frame = floor(frac(_Sequence.z / frames) * frames);
    float row = floor(frame / columns);
    float column = frame - columns * row;
    o.uv.xy = float2(column / columns + uv.x / columns, (rows - 1 - row) / rows + uv.y / rows);
    o.uv.zw = v.uv * _StreamEffectTex_ST.xy + _StreamEffectTex_ST.zw + float2(_uvStreamEffectSpeedX, _uvStreamEffectSpeedY) * _Time.y;
    o.matcapUV = mul((float3x3)UNITY_MATRIX_V, o.normalWS).xy * 0.495 + 0.5;
    return o;
}

float Smooth(float x) { return x * x * (3 - 2 * x); }

// 4x4 ordered dither: the character fades out with _Alpha.
void DitherFade(float2 pixel)
{
    static const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    uint2 p = (uint2)pixel & 3;
    clip(bayer[p.x * 4 + p.y] * 0.058824 - (1 - _Alpha));
}

half4 CharacterFrag (Varyings i) : SV_Target
{
    half4 base = _BaseColor * _AC_Color * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv.xy);
    half3 color = base.rgb;
#ifdef RO_SHOWBASE
    color = lerp(color, color * _EmissionColor.rgb * _LocalMetalStrength, base.a);
#endif
    color = _RemoveBloom == 1 ? pow(abs(color), _BloomThreshold) : max(color, 0);

    float3 N = normalize(i.normalWS);
    float3 V = normalize(_WorldSpaceCameraPos - i.positionWS);
    float nv = dot(N, V);
    float3 H = normalize(normalize(_DirectionControl.xyz) + V);
    float nh = max(dot(N, H), 0);
    float lit = Smooth(saturate((nh - _LightingThr) / (_SoftEdge * 0.5)));
#ifdef RO_SHOWBASE
    half4 shadowTex = SAMPLE_TEXTURE2D(_ShadowTex, sampler_ShadowTex, i.uv.xy);
    color *= lerp(shadowTex.rgb * _ShadowColor.rgb, 1, lit);
#else
    color *= lerp(_ShadowColor.rgb, 1, lit);
    float brightest = max(color.r, max(color.g, color.b));
    if (brightest > 1) color /= brightest;
    color = lerp(color, color * _EmissionColor.rgb * _LocalMetalStrength, base.a);
#endif
    if (_MatCapToggle == 1)
    {
        half3 matcap = SAMPLE_TEXTURE2D(_MatCapTex, sampler_MatCapTex, i.matcapUV).rgb;
        color += pow(abs(matcap), _MatcapContrast) * base.a * _MatcapStrength;
    }
#ifdef RO_SHOWBASE
    color += nh * (1 - Smooth(saturate((abs(nv) - _RimRange) * 20))) * _RimLightColor.rgb * _RimIntensity;
    half stream = SAMPLE_TEXTURE2D(_StreamEffectTex, sampler_StreamEffectTex, i.uv.xy).z * SAMPLE_TEXTURE2D(_StreamEffectTex, sampler_StreamEffectTex, i.uv.zw).x;
    color += stream * _StreamEffectIntensity * _StreamEffectColor.rgb;
    clip(shadowTex.a - _Cutoff);
#else
    if (_StreamEffectIntensity > 0)
    {
        float2 suv = i.uv.zw * _StreamTilingOffset.xy + _StreamTilingOffset.zw;
        color += SAMPLE_TEXTURE2D(_StreamEffectTex, sampler_StreamEffectTex, suv).x * _StreamEffectIntensity * _StreamEffectColor.rgb;
    }
    float fade = saturate((length(_WorldSpaceCameraPos - i.positionWS) - 50) * 0.04);
    float rimEdge = 1 - Smooth(saturate((abs(nv) - (fade * 0.25 + _RimRange)) * 20));
    color += max(dot(N, float3(-0.609106, 0.545775, -0.575429)), 0) * rimEdge * _RimLightColor.rgb * _RimIntensity;
    // The game sets these per scene; before it does (all zero) they would black the character out.
    half3 playerLight = any(_PlayerLightColor.rgb) ? _PlayerLightColor.rgb : 1;
    half3 dark = any(_DarkColor.rgb) ? _DarkColor.rgb : 1;
    color *= playerLight * dark;
#endif
    DitherFade(i.positionCS.xy);
    return half4(color, 1);
}

#endif
