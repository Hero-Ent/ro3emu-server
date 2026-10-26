// Port of the game's Soft Mask/HappyEngine/UberUI/Main: UberUI.shader's port with the Soft Mask cut (SoftMask.cginc).
// The Soft Mask component swaps it in for HappyEngine/UberUI/Main on graphics under a soft mask (the minimap, scroll
// lists); without it they drew past the mask's edge.
Shader "Soft Mask/HappyEngine/UberUI/Main"
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
        [PerRendererData] _SoftMask ("Mask", 2D) = "white" {}
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
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #define RO3_UBERUI_SOFTMASK 1
            #include "Assets/Shell/Resources/Ro3Shaders/Game/UberUI.cginc"
            ENDCG
        }
    }
}
