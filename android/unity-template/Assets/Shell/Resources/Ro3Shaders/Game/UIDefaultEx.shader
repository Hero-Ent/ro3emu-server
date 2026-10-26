// Port of the game's UI/Default Ex (D3D11 only in the PC client), from the disassembly of its UNITY_UI_CLIP_RECT pass
// (android\tools\ShaderDis.cs): UI/Default driven by the vertex streams the game's meshes fill in, which the plain
// stand-in ignored (the minimap's and the region map's CP_UIMiniMapRawMesh markers and backgrounds):
//   - uv0.z >= 0.5: the colour is lerp(NORMAL.xyz, TANGENT.xyz, texture red), a two-colour recolour; else the texture;
//   - then (+ _TextureSampleAdd) * vertex colour * _Color, alpha * the RectMask2D clip (Unity 2022's, with softness);
//   - output premultiplied by alpha unless uv1.z >= 0.5;
//   - saturation: lerp(luminance * (uv1.x, uv1.y, TANGENT.w), colour, uv0.w); exposure: * uv1.w.
// State as the client's pass: Blend One OneMinusSrcAlpha, ZTest unity_GUIZTestMode, no ZWrite or culling, material
// stencil and colour mask.
Shader "UI/Default Ex"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
        [ToggleUI] _ProxyColor ("Proxy Color", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float _UIMaskSoftnessX, _UIMaskSoftnessY;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uv : TEXCOORD0;      // xy texture UV, z recolour flag, w saturation
                float4 mask : TEXCOORD1;    // RectMask2D: xy distance from the clip rect centre, zw softness
                float3 colorA : TEXCOORD2;  // recolour from (NORMAL)...
                float3 colorB : TEXCOORD3;  // ...to (TANGENT)
                float4 color : TEXCOORD4;   // vertex colour * _Color
                float2 flags : TEXCOORD5;   // x straight (not premultiplied) alpha flag, y exposure
                float3 grey : TEXCOORD6;    // the tint the desaturated colour takes
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = float4(TRANSFORM_TEX(v.uv0.xy, _MainTex), v.uv0.zw);
                float2 pixelSize = o.pos.w / abs(float2(_ScreenParams.x * UNITY_MATRIX_P[0][0], _ScreenParams.y * UNITY_MATRIX_P[1][1]));
                float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
                o.mask = float4(v.vertex.xy * 2 - clampedRect.xy - clampedRect.zw,
                    0.25 / (0.25 * float2(_UIMaskSoftnessX, _UIMaskSoftnessY) + abs(pixelSize)));
                o.colorA = v.normal;
                o.colorB = v.tangent.xyz;
                o.color = v.color * _Color;
                o.flags = v.uv1.zw;
                o.grey = float3(v.uv1.xy, v.tangent.w);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv.xy);
                float3 rgb = i.uv.z >= 0.5 ? lerp(i.colorA, i.colorB, t.r) : t.rgb;
                float4 c = (float4(rgb, t.a) + _TextureSampleAdd) * i.color;
                float alpha = c.a;
                #ifdef UNITY_UI_CLIP_RECT
                float2 m = saturate((_ClipRect.zw - _ClipRect.xy - abs(i.mask.xy)) * i.mask.zw);
                alpha *= m.x * m.y;
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(alpha - 0.001);
                #endif
                rgb = i.flags.x >= 0.5 ? c.rgb : c.rgb * alpha;
                float luminance = dot(rgb, float3(0.2125, 0.7154, 0.0721));
                rgb = lerp(luminance * i.grey, rgb, i.uv.w) * i.flags.y;
                return float4(rgb, alpha);
            }
            ENDCG
        }
    }
}
