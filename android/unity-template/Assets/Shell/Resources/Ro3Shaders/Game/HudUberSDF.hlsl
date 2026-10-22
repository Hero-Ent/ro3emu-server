// Port of Happyengine/HUD/HudUberSDF (names and damage numbers over the characters, D3D11 only in the PC client) from
// its DXBC disassembly (android\tools\ShaderDis.cs). TextMeshPro-style signed distance field text with per-glyph
// styles: uv0.z picks one of eight font atlases (_FontTex1-8), uv0.w the style (outline, underlay: arrays of 50), and
// uv0.w < 0 marks an icon from the _SpriteTex array (slice uv0.z). uv1.x packs the face/outline texture coordinates,
// uv1.y <= 0 means bold. Output is premultiplied (Blend One OneMinusSrcAlpha). Left out: UI clip rects.
#ifndef HUD_UBER_SDF_INCLUDED
#define HUD_UBER_SDF_INCLUDED

#include "Assets/Ro3Shaders/Lib/universal/ShaderLibrary/Core.hlsl"

float4 _FaceColor;
float _FaceUVSpeedX, _FaceUVSpeedY;
float _OutlineSoftness, _OutlineUVSpeedX, _OutlineUVSpeedY;
float _WeightNormal, _WeightBold, _ScaleRatioA, _ScaleRatioC;
float _VertexOffsetX, _VertexOffsetY;
float _TextureWidth, _TextureHeight, _GradientScale, _Sharpness;
float4 _FaceTex_ST, _OutlineTex_ST;
float4 outline_color[50];
float outline_thickness[50];
float outline_dilate[50];
float4 underlay_color[50];
float underlay_offsetx[50];
float underlay_offsety[50];
float underlay_dilate[50];
float underlay_softness[50];

TEXTURE2D(_FontTex1); SAMPLER(sampler_FontTex1);
TEXTURE2D(_FontTex2); SAMPLER(sampler_FontTex2);
TEXTURE2D(_FontTex3); SAMPLER(sampler_FontTex3);
TEXTURE2D(_FontTex4); SAMPLER(sampler_FontTex4);
TEXTURE2D(_FontTex5); SAMPLER(sampler_FontTex5);
TEXTURE2D(_FontTex6); SAMPLER(sampler_FontTex6);
TEXTURE2D(_FontTex7); SAMPLER(sampler_FontTex7);
TEXTURE2D(_FontTex8); SAMPLER(sampler_FontTex8);
TEXTURE2D(_FaceTex); SAMPLER(sampler_FaceTex);
TEXTURE2D(_OutlineTex); SAMPLER(sampler_OutlineTex);
TEXTURE2D_ARRAY(_SpriteTex); SAMPLER(sampler_SpriteTex);

struct Attributes
{
    float4 positionOS : POSITION;
    float4 color : COLOR;
    float4 uv0 : TEXCOORD0;         // xy uv, z atlas (or sprite slice), w style (< 0: sprite)
    float2 uv1 : TEXCOORD1;         // x packed face/outline uv, y <= 0: bold
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float4 color : COLOR;
    float4 uv0 : TEXCOORD0;
    float bias : TEXCOORD1;
    float2 underlayUV : TEXCOORD2;
    float4 underlayColor : TEXCOORD3;
    float4 faceOutlineUV : TEXCOORD4;
};

Varyings HudVert (Attributes v)
{
    Varyings o;
    float3 positionOS = float3(v.positionOS.xy + float2(_VertexOffsetX, _VertexOffsetY), v.positionOS.z);
    o.positionCS = TransformObjectToHClip(positionOS);
    o.color = v.color;
    o.uv0 = v.uv0;
    int style = (int)round(v.uv0.w);
    style = clamp(style, 0, 49);
    float weight = v.uv1.y <= 0 ? _WeightBold : _WeightNormal;
    o.bias = (weight * 0.25 + outline_dilate[style]) * _ScaleRatioA * 0.5;
    float2 offset = -float2(underlay_offsetx[style], underlay_offsety[style]) * _ScaleRatioC * _GradientScale / float2(_TextureWidth, _TextureHeight);
    o.underlayUV = v.uv0.xy + offset;
    o.underlayColor = float4(underlay_color[style].rgb * underlay_color[style].a, underlay_color[style].a);
    float packed = v.uv1.x * 0.000244;
    float row = floor(packed);
    float2 faceUV = float2(row, v.uv1.x - row * 4096) * 0.001953;
    o.faceOutlineUV = float4(faceUV * _FaceTex_ST.xy + _FaceTex_ST.zw, faceUV * _OutlineTex_ST.xy + _OutlineTex_ST.zw);
    return o;
}

float FontDistance(float atlas, float2 uv)
{
    int a = (int)round(atlas);
    switch (a)
    {
        case 0: return SAMPLE_TEXTURE2D(_FontTex1, sampler_FontTex1, uv).a;
        case 1: return SAMPLE_TEXTURE2D(_FontTex2, sampler_FontTex2, uv).a;
        case 2: return SAMPLE_TEXTURE2D(_FontTex3, sampler_FontTex3, uv).a;
        case 3: return SAMPLE_TEXTURE2D(_FontTex4, sampler_FontTex4, uv).a;
        case 4: return SAMPLE_TEXTURE2D(_FontTex5, sampler_FontTex5, uv).a;
        case 5: return SAMPLE_TEXTURE2D(_FontTex6, sampler_FontTex6, uv).a;
        case 6: return SAMPLE_TEXTURE2D(_FontTex7, sampler_FontTex7, uv).a;
        case 7: return SAMPLE_TEXTURE2D(_FontTex8, sampler_FontTex8, uv).a;
        default: return 1;
    }
}

half4 HudFrag (Varyings i) : SV_Target
{
    if (i.uv0.w < 0)
    {
        half4 icon = SAMPLE_TEXTURE2D_ARRAY_LOD(_SpriteTex, sampler_SpriteTex, i.uv0.xy, i.uv0.z, 0) * i.color;
        return half4(icon.rgb * icon.a, icon.a);
    }
    int style = clamp((int)round(i.uv0.w), 0, 49);
    float d = FontDistance(i.uv0.z, i.uv0.xy);
    float2 pixel = _TextureHeight * 0.75 * float2(ddx(i.uv0.y), ddy(i.uv0.y));
    float scale = rsqrt(dot(pixel, pixel)) * _GradientScale * (_Sharpness + 1);
    float bias = 0.5 - i.bias;
    float sd = (0.5 / scale + bias) - d;
    float outline = _ScaleRatioA * outline_thickness[style] * scale;
    float softness = _OutlineSoftness * _ScaleRatioA * scale;

    half4 faceTex = SAMPLE_TEXTURE2D(_FaceTex, sampler_FaceTex, i.faceOutlineUV.xy + float2(_FaceUVSpeedX, _FaceUVSpeedY) * _Time.y);
    half3 faceRGB = i.color.rgb * _FaceColor.rgb * faceTex.rgb;
    half faceA = faceTex.a * _FaceColor.a;
    half4 outlineTex = SAMPLE_TEXTURE2D(_OutlineTex, sampler_OutlineTex, i.faceOutlineUV.zw + float2(_OutlineUVSpeedX, _OutlineUVSpeedY) * _Time.y) * outline_color[style];

    half coverage = 1 - saturate((sd * scale - outline * 0.5 + softness * 0.5) / (_OutlineSoftness * _ScaleRatioA * scale + 1));
    half outlineMix = saturate(sd * scale + outline * 0.5) * sqrt(min(outline, 1));
    half4 face = half4(faceRGB * faceA, faceA);
    half4 color = face + (half4(outlineTex.rgb * outlineTex.a, outlineTex.a) - face) * outlineMix;
    color *= coverage;
#ifdef UNDERLAY_ON
    float uScale = scale / (_ScaleRatioC * underlay_softness[style] * scale + 1);
    float uBias = bias * uScale - 0.5 - _ScaleRatioC * underlay_dilate[style] * uScale * 0.5;
    float ud = FontDistance(i.uv0.z, i.underlayUV);
    half4 underlay = saturate(ud * uScale - uBias) * i.underlayColor;
    color += underlay * (1 - color.a);
#endif
    return color * i.color.a;
}

#endif
