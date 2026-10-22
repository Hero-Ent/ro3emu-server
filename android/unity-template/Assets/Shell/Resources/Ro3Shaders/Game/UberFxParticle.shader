// Port of HappyEngine/UberFx/Particle (D3D11 only in the PC client); the code is in UberFxParticle.hlsl. Properties: the
// original block (AssetRipper export), so materials that only store non-default values keep the game's defaults.
Shader "HappyEngine/UberFx/Particle"
{
	Properties {
		[Toggle(_USE_DECAL)] _Toggle_USE_DECAL_ON ("启用粒子贴花", Float) = 0
		_EdgeStretchPrevent ("贴花剔除角度", Range(0, 1)) = 0
		[Space(3)] [Toggle(_USE_SOFTPARTICLE)] _Toggle_USE_SOFTPARTICLE_ON ("启用软粒子", Float) = 0
		[Enum(G,0,R,1,H,2)] _Use_SoftParticle ("软边模式", Float) = 0
		[HDR] _SoftFadeColor ("软边颜色", Vector) = (0,0,0,1)
		_SoftFade ("柔和度", Range(0, 100)) = 1
		[Space(3)] _UV_Polar_Main ("启用uv 极坐标(中心方向)", Float) = 0
		_PolarMainTiling ("极坐标缩放(Tiling: x, y; Offset: z, w)", Vector) = (1,1,0,0)
		_UVRot ("启用uv 旋转", Float) = 0
		_UV_Speed_Rot ("UV旋转速度", Float) = 0
		_MainTexAngle ("纹理旋转", Range(0, 1)) = 0
		_UV_Speed_U_Main ("纹理U方向速度", Float) = 0
		_UV_Speed_V_Main ("纹理V方向速度", Float) = 0
		[HDR] _Color ("纹理颜色", Vector) = (1,1,1,1)
		[Toggle(_USE_SCREENTEX)] [HideInInspector] _Use_ScreenTex ("启用屏幕贴图", Float) = 0
		_MainTex ("纹理贴图", 2D) = "white" {}
		[Enum(RGB,0, R,1, G,2, B,3)] _MainTex_Channel ("纹理通道", Float) = 0
		[Enum(Repeat,0, Clamp,1, Clamp_X,2, Clamp_Y,3)] _MainTex_Clamp ("纹理贴图平铺", Float) = 0
		_Alpha ("纹理不透明度", Range(0, 5)) = 1
		[Space(3)] _Show_In_SceneRange ("仅在场景指定范围内显示", Float) = 0
		_LocalToggle_FX_Height ("是否开启高度效果显示(默认开启)", Float) = 1
		[Space(3)] [Toggle(_USE_MASK)] _USE_MASK ("启用遮罩纹理", Float) = 0
		_UVRot_Mask ("启用遮罩纹理uv旋转", Float) = 0
		_MaskTexAngle ("遮罩纹理旋转角度", Range(0, 1)) = 0
		_MaskTexRotSpeed ("遮罩旋转速度", Float) = 0
		_UV_Speed_U_Mask ("纹理U方向速度", Float) = 0
		_UV_Speed_V_Mask ("纹理V方向速度", Float) = 0
		_DistortStrengthMask ("遮罩扭曲强度", Float) = 0
		[HDR] _MaskTexColor ("遮罩颜色", Vector) = (1,1,1,1)
		_MaskTex ("遮罩纹理", 2D) = "white" {}
		_UV_Polar_MASK ("启用uv 极坐标(中心方向)", Float) = 0
		[Enum(RGB,0, R,1, G,2, B,3, A,4)] _MaskTex_Channel ("遮罩通道", Float) = 0
		[Enum(Repeat,0, Clamp,1, Clamp_X,2, Clamp_Y,3)] _MaskTex_Clamp ("遮罩贴图平铺", Float) = 0
		[Space(3)] [Toggle(_USE_MASK2)] _USE_MASK2 ("启用遮罩纹理2", Float) = 0
		_UVRot_Mask2 ("启用遮罩纹理uv旋转2", Float) = 0
		_MaskTexAngle2 ("遮罩纹理旋转角度2", Range(0, 1)) = 0
		_MaskTexRotSpeed2 ("遮罩旋转速度2", Float) = 0
		_UV_Speed_U_Mask2 ("纹理U方向速度2", Float) = 0
		_UV_Speed_V_Mask2 ("纹理V方向速度2", Float) = 0
		_DistortStrengthMask2 ("遮罩扭曲强度", Float) = 0
		[HDR] _MaskTexColor2 ("遮罩颜色2", Vector) = (1,1,1,1)
		_MaskTex2 ("遮罩纹理2", 2D) = "white" {}
		_UV_Polar_MASK2 ("启用uv 极坐标(中心方向)2", Float) = 0
		[Enum(RGB,0, R,1, G,2, B,3, A,4)] _MaskTex_Channel2 ("遮罩通道2", Float) = 0
		[Enum(Repeat,0, Clamp,1, Clamp_X,2, Clamp_Y,3)] _MaskTex_Clamp2 ("遮罩贴图平铺2", Float) = 0
		[Space(3)] [Toggle(_USE_DISTORT)] _Toggle_USE_DISTORT_ON ("启用扭曲纹理", Float) = 0
		_DistortTex ("扭曲纹理", 2D) = "white" {}
		_UV_Polar_Distort ("启用uv 极坐标(中心方向)", Float) = 0
		[Enum(RGB,0, R,1, G,2, B,3, A,4)] _DistortTex_Channel ("扭曲通道", Float) = 0
		[Enum(Repeat,0, Clamp,1, Clamp_X,2, Clamp_Y,3)] _DistortTex_Clamp ("扭曲贴图平铺", Float) = 0
		_Toggle_Use_Distort_Intencity_On ("使用扭曲强度", Float) = 1
		_DistortStrength ("扭曲强度", Range(0, 5)) = 0
		_DistortTex_Speed_U ("U 方向扭曲速度", Float) = 0
		_DistortTex_Speed_V ("V 方向扭曲速度", Float) = 0
		_UVRot_DistortTex ("扭曲纹理uv 旋转", Float) = 0
		_DistortTexRotSpeed ("扭曲旋转角度", Range(0, 1)) = 0
		[Enum(X,0,Y,1,Z,2,OFF,3)] _DistortTransitionAxis ("扭曲过渡轴", Float) = 3
		_DistortTransitionPoint ("扭曲过渡点", Float) = 0.5
		[Space(3)] [Toggle(_USE_VTF)] _Toggle_Use_VTF_On ("启用VTF纹理", Float) = 0
		_UV_Speed_U_VTF ("VTF U方向速度", Float) = 0
		_UV_Speed_V_VTF ("VTF V方向速度", Float) = 0
		_VTF_Texture ("VTF 纹理", 2D) = "white" {}
		[Enum(Repeat,0, Clamp,1, Clamp_X,2, Clamp_Y,3)] _VTF_Texture_Clamp ("扭曲贴图平铺", Float) = 0
		[Enum(RGB,0, R,1, G,2, B,3, A,4)] _VTF_Texture_Channel ("VTF 通道", Float) = 0
		_Toggle_Use_VTF_Intencity_On ("使用VTF顶点动画强度", Float) = 1
		_VTF_Intencity ("顶点动画强度", Float) = 1
		[Enum(X,0,Y,1,Z,2,OFF,3)] _VTFTransitionAxis ("VTF过渡轴", Float) = 3
		_VTFTransitionPoint ("VTF过渡点", Float) = 0.5
		[Space(3)] [Toggle(_USE_DISSOLVE)] _Toggle_Use_DISSOLVE_ON ("溶解", Float) = 0
		[Enum(OFF,0, U,1, NU,2, V,3, NV,4)] _DissolveDirection ("溶解方向", Float) = 0
		_DissolveDirectionThreshold1 ("溶解方向范围Min", Range(-2, 2)) = 0
		_DissolveDirectionThreshold2 ("溶解方向范围Max", Range(-2, 2)) = 0
		_DissolveAlpha ("随粒子alpha值溶解", Float) = 0
		_Toggle_Use_Dissolve_UV_Rot_ON ("启用溶解纹理旋转UV", Float) = 0
		_DissolveTexAngle ("溶解贴图旋转", Range(0, 1)) = 0
		_UV_Speed_U_Dissolve ("溶解 U方向速度", Float) = 0
		_UV_Speed_V_Dissolve ("溶解 V方向速度", Float) = 0
		_DissolveTex ("溶解贴图", 2D) = "white" {}
		_UV_Polar_Dissolve ("启用uv 极坐标(中心方向)", Float) = 0
		[Enum(RGB,0, R,1, G,2, B,3, A,4, Self,5)] _DissolveTex_Channel ("溶解通道", Float) = 0
		[Enum(Repeat,0, Clamp,1, Clamp_X,2, Clamp_Y,3)] _DissolveTex_Clamp ("溶解贴图平铺", Float) = 0
		_DissolveDistortStrength ("溶解贴图的扭曲强度", Range(0, 5)) = 0
		[HDR] _DissolveColor ("溶解边缘颜色", Vector) = (1,1,1,1)
		_DissolveWidth ("溶解边缘宽度", Range(0, 0.5)) = 0
		_DissolveSmooth ("溶解边缘柔和度", Range(0, 50)) = 0
		_Toggle_Use_DissolveStrength_on ("使用溶解强度", Float) = 1
		_DissolveStrength ("溶解强度", Float) = 0
		[Space(3)] [Toggle(_USE_RIM)] _Toggle_USE_RIM_ON ("边缘光", Float) = 0
		[HDR] _RimColor ("边缘光颜色", Vector) = (1,1,1,1)
		_RimPower ("边缘光强度", Range(0, 10)) = 1
		_RimRevert ("边缘光反向", Float) = 0
		_RimAlpha ("边缘光Alpha反向", Float) = 0
		[HideInInspector] _Use_Shear ("Use_Shear", Float) = 0
		[HideInInspector] _DropdownIndex ("功能组合", Float) = 0
		[HideInInspector] _UsedDropdown ("已选择过下拉列表", Float) = 0
		[Space(30)] [Enum(Off, 0, On, 1)] _zWrite ("ZWrite", Float) = 0
		[Enum(UnityEngine.Rendering.CompareFunction)] _zTest ("ZTest", Float) = 4
		[Enum(UnityEngine.Rendering.CullMode)] _cull ("Cull Mode", Float) = 2
		[Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend Mode", Float) = 5
		[Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend Mode", Float) = 10
		[Enum(UnityEngine.Rendering.BlendMode)] _srcAlphaBlend ("Src Alpha Blend Mode", Float) = 5
		[Enum(UnityEngine.Rendering.BlendMode)] _dstAlphaBlend ("Dst Alpha Blend Mode", Float) = 10
		_StencilRef ("Stencil Ref", Float) = 0
		[Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comp", Float) = 4
		[Enum(UnityEngine.Rendering.StencilOp)] _StencilOp ("Stencil Op", Float) = 0
		[Header(Fog__________________________________________________________________________________________________#)] [Space(3)] [Toggle(_NONUSE_FOG)] _NONUSE_FOG ("不被雾影响", Float) = 0
		[Space(30)] _BaseColor ("程序颜色", Vector) = (1,1,1,1)
	}
	SubShader
	{
		Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "RenderPipeline"="UniversalPipeline" }
		Pass
		{
			Name "FrontFace"
			Blend [_SrcBlend] [_DstBlend], [_srcAlphaBlend] [_dstAlphaBlend]
			ZWrite [_zWrite]
			ZTest [_zTest]
			Cull [_cull]

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex UberFxVert
			#pragma fragment UberFxFrag
			#pragma multi_compile_local _ _USE_MASK
			#pragma multi_compile_local _ _USE_MASK2
			#include "Assets/Shell/Resources/Ro3Shaders/Game/UberFxParticle.hlsl"
			ENDHLSL
		}
	}
}
