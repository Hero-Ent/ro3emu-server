// Port of the game's HappyEngine/UberUI/Main for Vulkan/GLES3 (the client only has D3D11 programs). Same name and
// property names, so ShaderFixer swaps it in and the game's materials keep their settings. Covers the main texture
// (channel, intensity, tiling, offset, panning, rotation, clamp), two masks, a plain dissolve (no edge colour) and the
// material-driven blend/cull/stencil; polar coordinates, distortion and particle CustomData are not ported yet.
Shader "HappyEngine/UberUI/Main"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Main", 2D) = "white" {}
        _Main_Intensity ("Main Intensity", Range(0, 5)) = 1
        _MainTex_Channel ("Main Channel", Float) = 0
        _MainTex_Clamp ("Main Clamp", Float) = 0
        _Main_Tilling ("Main Tiling", Vector) = (1,1,0,0)
        _Main_Offset ("Main Offset", Vector) = (0,0,0,0)
        _Main_Panner ("Main Panner", Vector) = (0,0,0,0)
        _Main_Anchor ("Main Anchor", Vector) = (0.5,0.5,0,0)
        _Main_Rotation ("Main Rotation", Range(-1, 1)) = 0
        _Main_Rotation_Speed ("Main Rotation Speed", Float) = 0
        _USE_MASK ("Mask", Float) = 0
        [NoScaleOffset] _MaskTex ("Mask", 2D) = "white" {}
        _Mask_Intensity ("Mask Intensity", Range(0, 5)) = 1
        _MaskTex_Channel ("Mask Channel", Float) = 0
        _MaskTex_Clamp ("Mask Clamp", Float) = 0
        _Mask_Tilling ("Mask Tiling", Vector) = (1,1,0,0)
        _Mask_Offset ("Mask Offset", Vector) = (0,0,0,0)
        _Mask_Panner ("Mask Panner", Vector) = (0,0,0,0)
        _Mask_Anchor ("Mask Anchor", Vector) = (0.5,0.5,0,0)
        _Mask_Rotation ("Mask Rotation", Range(-1, 1)) = 0
        _Mask_Rotation_Speed ("Mask Rotation Speed", Float) = 0
        _USE_MASK2 ("Mask 2", Float) = 0
        [NoScaleOffset] _MaskTex2 ("Mask 2", 2D) = "white" {}
        _Mask_Intensity2 ("Mask 2 Intensity", Range(0, 5)) = 1
        _MaskTex_Channel2 ("Mask 2 Channel", Float) = 0
        _MaskTex_Clamp2 ("Mask 2 Clamp", Float) = 0
        _Mask_Tilling2 ("Mask 2 Tiling", Vector) = (1,1,0,0)
        _Mask_Offset2 ("Mask 2 Offset", Vector) = (0,0,0,0)
        _Mask_Panner2 ("Mask 2 Panner", Vector) = (0,0,0,0)
        _Mask_Anchor2 ("Mask 2 Anchor", Vector) = (0.5,0.5,0,0)
        _Mask_Rotation2 ("Mask 2 Rotation", Range(-1, 1)) = 0
        _Mask_Rotation_Speed2 ("Mask 2 Rotation Speed", Float) = 0
        _USE_DISSOLVE ("Dissolve", Float) = 0
        [NoScaleOffset] _DissolveTex ("Dissolve", 2D) = "black" {}
        _Dissolve_Intensity ("Dissolve Intensity", Float) = 1
        _DissolveTex_Channel ("Dissolve Channel", Float) = 0
        _Dissolve_Tilling ("Dissolve Tiling", Vector) = (1,1,0,0)
        _Dissolve_Offset ("Dissolve Offset", Vector) = (0,0,0,0)
        _Dissolve_Panner ("Dissolve Panner", Vector) = (0,0,0,0)
        _DissolveStrength ("Dissolve", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _cull ("Cull Mode", Float) = 2
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.BlendMode)] _srcAlphaBlend ("Src Alpha Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _dstAlphaBlend ("Dst Alpha Blend", Float) = 10
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
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
        Cull [_cull]
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend [_SrcBlend] [_DstBlend], [_srcAlphaBlend] [_dstAlphaBlend]
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            sampler2D _MainTex, _MaskTex, _MaskTex2, _DissolveTex;
            float _Main_Intensity, _MainTex_Channel, _MainTex_Clamp, _Main_Rotation, _Main_Rotation_Speed;
            float4 _Main_Tilling, _Main_Offset, _Main_Panner, _Main_Anchor;
            float _USE_MASK, _Mask_Intensity, _MaskTex_Channel, _MaskTex_Clamp, _Mask_Rotation, _Mask_Rotation_Speed;
            float4 _Mask_Tilling, _Mask_Offset, _Mask_Panner, _Mask_Anchor;
            float _USE_MASK2, _Mask_Intensity2, _MaskTex_Channel2, _MaskTex_Clamp2, _Mask_Rotation2, _Mask_Rotation_Speed2;
            float4 _Mask_Tilling2, _Mask_Offset2, _Mask_Panner2, _Mask_Anchor2;
            float _USE_DISSOLVE, _Dissolve_Intensity, _DissolveTex_Channel, _DissolveStrength;
            float4 _Dissolve_Tilling, _Dissolve_Offset, _Dissolve_Panner;
            float4 _ClipRect;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float4 world : TEXCOORD1; };

            // Tiling/offset/panning, then a rotation (range -1..1 = -180..180 degrees, plus speed) around the anchor.
            float2 Transform(float2 uv, float4 tiling, float4 offset, float4 panner, float4 anchor, float rotation, float speed, float clampMode)
            {
                uv = uv * tiling.xy + offset.xy + panner.xy * _Time.y;
                float a = (rotation + speed * _Time.y) * UNITY_PI;
                float s = sin(a), c = cos(a);
                uv = mul(float2x2(c, -s, s, c), uv - anchor.xy) + anchor.xy;
                return clampMode > 0.5 ? saturate(uv) : uv;
            }

            float Channel(float4 t, float channel)
            {
                return channel < 0.5 ? t.r : channel < 1.5 ? t.g : channel < 2.5 ? t.b : t.a;
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.world = v.vertex;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, Transform(i.uv, _Main_Tilling, _Main_Offset, _Main_Panner, _Main_Anchor, _Main_Rotation, _Main_Rotation_Speed, _MainTex_Clamp));
                // Channel 0 = RGB(A), 1..3 = R, G, B as a grey value and its alpha.
                float4 c = _MainTex_Channel < 0.5 ? t : Channel(t, _MainTex_Channel - 1).xxxx;
                c.rgb *= _Main_Intensity;
                if (_USE_MASK > 0.5)
                    c.a *= saturate(Channel(tex2D(_MaskTex, Transform(i.uv, _Mask_Tilling, _Mask_Offset, _Mask_Panner, _Mask_Anchor, _Mask_Rotation, _Mask_Rotation_Speed, _MaskTex_Clamp)), _MaskTex_Channel) * _Mask_Intensity);
                if (_USE_MASK2 > 0.5)
                    c.a *= saturate(Channel(tex2D(_MaskTex2, Transform(i.uv, _Mask_Tilling2, _Mask_Offset2, _Mask_Panner2, _Mask_Anchor2, _Mask_Rotation2, _Mask_Rotation_Speed2, _MaskTex_Clamp2)), _MaskTex_Channel2) * _Mask_Intensity2);
                if (_USE_DISSOLVE > 0.5)
                {
                    float d = Channel(tex2D(_DissolveTex, i.uv * _Dissolve_Tilling.xy + _Dissolve_Offset.xy + _Dissolve_Panner.xy * _Time.y), _DissolveTex_Channel) * _Dissolve_Intensity;
                    c.a *= step(_DissolveStrength, d);
                }
                c *= i.color;
                #ifdef UNITY_UI_CLIP_RECT
                c.a *= UnityGet2DClipping(i.world.xy, _ClipRect);
                #endif
                return c;
            }
            ENDCG
        }
    }
}
