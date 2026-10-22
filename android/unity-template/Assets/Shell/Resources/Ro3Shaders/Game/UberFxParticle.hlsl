// Port of HappyEngine/UberFx/Particle (the game's effect shader, D3D11 only in the PC client) from its DXBC disassembly
// (android\tools\ShaderDis.cs), variants _USE_MASK / _USE_MASK2: main texture (tiling, scrolling, rotation, polar
// mapping, clamp mode, single channel), particle custom data offset, two masks, colour, alpha, saturation. Left out:
// dissolve, distortion, rim, vertex texture animation, soft particles, decals, screen texture, height fade, fog.
#ifndef UBERFX_PARTICLE_INCLUDED
#define UBERFX_PARTICLE_INCLUDED

#include "Assets/Ro3Shaders/Lib/universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _Color;
    float _Alpha;
    float4 _MainTex_ST;
    float _UV_Polar_Main;
    float4 _PolarMainTiling;
    float _UV_Speed_U_Main;
    float _UV_Speed_V_Main;
    float _MainTex_Channel;
    float _MainTex_Clamp;
    float _MainTexAngle;
    float _UVRot;
    float _UV_Speed_Rot;
    float _MaskTex_Channel;
    float _MaskTex_Clamp;
    float _UV_Polar_MASK;
    float4 _MaskTex_ST;
    float _UV_Speed_U_Mask;
    float _UV_Speed_V_Mask;
    float _MaskTexAngle;
    float4 _MaskTexColor;
    float _UVRot_Mask;
    float _MaskTexRotSpeed;
    float _MaskTex_Channel2;
    float _MaskTex_Clamp2;
    float _UV_Polar_MASK2;
    float4 _MaskTex2_ST;
    float _UV_Speed_U_Mask2;
    float _UV_Speed_V_Mask2;
    float _MaskTexAngle2;
    float4 _MaskTexColor2;
    float _UVRot_Mask2;
    float _MaskTexRotSpeed2;
    float4 _BaseColor;
CBUFFER_END
float _Saturation;

TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
TEXTURE2D(_MaskTex); SAMPLER(sampler_MaskTex);
TEXTURE2D(_MaskTex2); SAMPLER(sampler_MaskTex2);

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    float4 custom : TEXCOORD1;      // particle custom data: zw offsets the main texture
    float4 color : COLOR;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    float2 custom : TEXCOORD2;
    float2 maskUV : TEXCOORD3;
    float2 mask2UV : TEXCOORD4;
};

float2 Rotate(float2 uv, float turns)
{
    float s, c;
    sincos(turns * TWO_PI, s, c);
    float2 p = uv - 0.5;
    return float2(p.x * c + p.y * s, -p.x * s + p.y * c) + 0.5;
}

// The masks' rotation runs on a one-minute clock, like the original.
float MinuteClock() { return frac(abs(_Time.y * 0.016667)) * sign(_Time.y) * 60; }

float2 MaskUV(float2 uv, float4 st, float2 speed, float rotate, float rotSpeed, float angle)
{
    float2 tiled = uv * st.xy + st.zw;
    float2 scroll = frac(_Time.y * speed);
    if (rotate != 0) tiled = Rotate(tiled, rotSpeed * MinuteClock() + angle);
    return tiled + scroll;
}

Varyings UberFxVert (Attributes v)
{
    Varyings o;
    o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
    o.uv = v.uv;
    o.color = v.color;
    o.custom = v.custom.zw;
    o.maskUV = MaskUV(v.uv, _MaskTex_ST, float2(_UV_Speed_U_Mask, _UV_Speed_V_Mask), _UVRot_Mask, _MaskTexRotSpeed, _MaskTexAngle);
    o.mask2UV = MaskUV(v.uv, _MaskTex2_ST, float2(_UV_Speed_U_Mask2, _UV_Speed_V_Mask2), _UVRot_Mask2, _MaskTexRotSpeed2, _MaskTexAngle2);
    return o;
}

// Polar mapping around the centre (+ offset): radius * tiling.x, angle * tiling.y.
float2 Polar(float2 uv, float4 tiling)
{
    float2 p = uv - 0.5 + tiling.zw;
    return float2(length(p) * 2 * tiling.x, frac((atan2(p.y, p.x) * 0.159236 + 0.5) * tiling.y));
}

// 0 repeat, 1 clamp, 2 clamp X (Y repeats), 3 clamp Y (X repeats).
float2 ClampMode(float2 uv, float mode)
{
    uint m = (uint)mode;
    if (m < 1) return uv;
    float2 clamped = saturate(uv);
    return float2(m == 3 ? frac(uv.x) : clamped.x, m == 2 ? frac(uv.y) : clamped.y);
}

// 0 RGBA, 1-3: that channel in all four components.
half4 Channel(half4 c, float channel)
{
    uint ch = (uint)channel;
    return ch >= 1 ? c[min(ch - 1, 3)].xxxx : c;
}

half4 UberFxFrag (Varyings i) : SV_Target
{
    float2 uv = _UV_Polar_Main > 0 ? Polar(i.uv, _PolarMainTiling) : i.uv;
    uv = frac(_Time.y * float2(_UV_Speed_U_Main, _UV_Speed_V_Main)) + uv * _MainTex_ST.xy + _MainTex_ST.zw + i.custom;
    if (_UVRot != 0) uv = Rotate(uv, _Time.y * _UV_Speed_Rot + _MainTexAngle);
    half4 tex = Channel(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, ClampMode(uv, _MainTex_Clamp)), _MainTex_Channel);
    half4 color = half4(tex.rgb * _Color.rgb * i.color.rgb, saturate(tex.a * _Alpha * i.color.a * _BaseColor.a));
#ifdef _USE_MASK
    float2 muv = _UV_Polar_MASK > 0.5 ? Polar(i.uv, _MaskTex_ST) + float2(_UV_Speed_U_Mask, _UV_Speed_V_Mask) * _Time.x : ClampMode(i.maskUV, _MaskTex_Clamp);
    color *= Channel(SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, muv), _MaskTex_Channel) * _MaskTexColor;
#endif
#ifdef _USE_MASK2
    float2 m2uv = _UV_Polar_MASK2 > 0.5 ? Polar(i.uv, _MaskTex2_ST) + float2(_UV_Speed_U_Mask2, _UV_Speed_V_Mask2) * _Time.x : ClampMode(i.mask2UV, _MaskTex_Clamp2);
    color *= Channel(SAMPLE_TEXTURE2D(_MaskTex2, sampler_MaskTex2, m2uv), _MaskTex_Channel2) * _MaskTexColor2;
#endif
    half luma = dot(half3(0.2125, 0.7154, 0.0721), color.rgb);
    color.rgb += _Saturation * (luma - color.rgb);
    clip(color.a - 0.005);
    return half4(color.rgb * _BaseColor.rgb, color.a);
}

#endif
