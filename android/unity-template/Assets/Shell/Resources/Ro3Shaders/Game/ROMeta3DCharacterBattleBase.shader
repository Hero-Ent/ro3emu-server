// Port of ROMeta/3DCharacter/BattleBase (D3D11 only in the PC client); the code is in ROCharacter.hlsl. Properties: the
// original block (AssetRipper export), so materials that only store non-default values keep the game's defaults.
Shader "ROMeta/3DCharacter/BattleBase"
{
	Properties {
		[Header(BaseSettings________________________________________________________________)] [Space(5)] [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("剔除模式 : 默认是Back, Off是双面渲染", Float) = 2
		[Space(5)] _BaseColor ("BaseColor", Vector) = (1,1,1,1)
		_AC_Color ("Art Color", Vector) = (1,1,1,1)
		_DarkColor ("DarkColor", Vector) = (1,1,1,1)
		[Toggle] _GlitterToggle ("Glitter Toggle", Float) = 0
		_GlitterPower ("Glitter Power", Range(0.01, 50)) = 1
		[HDR] _GlitterColor ("Glitter Color", Vector) = (0,0,0,1)
		_BaseMap ("   颜色纹理", 2D) = "white" {}
		_Sequence ("Row(X) Column(Y) ID(Z)", Vector) = (1,1,0,0)
		_LocalMetalStrength ("  局部金属强度", Float) = 1
		[Toggle] _BeFrozen ("   被冻住", Float) = 0
		[Header(Lighting_____________________________________________________________________)] [Space(5)] _ShadowColor ("阴影色", Vector) = (0.784314,0.784314,0.784314,1)
		_DirectionControl ("法线方向控制", Vector) = (-1,0.3,-1,1)
		_LightingThr ("光照阈值", Range(-1, 1)) = 0.5
		_SoftEdge ("光照明暗线软硬", Range(0, 1)) = 0
		[Space(5)] [HDR] _EmissionColor ("自发光颜色", Vector) = (1,1,1,0)
		[Header(MatCap______________________________________________________________________)] [Toggle] _MatCapToggle ("开启MatCap", Float) = 0
		[NoScaleOffset] _MatCapTex ("MatCapTex", 2D) = "black" {}
		_MatcapStrength ("Matcap 强度", Range(0, 1)) = 1
		_MatcapContrast ("Matcap 对比度", Range(0, 5)) = 1
		[Header(StreamEffect________________________________________________________________)] [Space(5)] [NoScaleOffset] _StreamEffectTex ("R流光,G石化,B无,A无", 2D) = "black" {}
		_StreamTilingOffset ("流光 Tiling Offset", Vector) = (1,1,0,0)
		_uvStreamEffectSpeedX ("流光 uv流速X", Float) = 0
		_uvStreamEffectSpeedY ("流光 uv流速Y", Float) = 0
		[HDR] _StreamEffectColor ("流光颜色", Vector) = (1,1,1,0)
		_StreamEffectIntensity ("流光强度", Float) = 0
		[Space(10)] [Toggle] _isStone ("石化开关", Float) = 0
		_StoneTilingOffset ("石化 Tiling Offset", Vector) = (1,1,0,0)
		[HDR] _StoneBrightnessColor ("Stone Brightness Color", Vector) = (1,1,1,1)
		_StoneShadowskColor ("Stone Shadows Color", Vector) = (1,1,1,1)
		[Header(Dissolve)] [Space(10)] [Toggle] _Dissolve ("消融", Float) = 0
		_DissolveStrength ("消融阈值", Range(0, 1)) = 0
		_DissolveEdgeWidth ("消融宽度", Range(0, 1)) = 0.3
		[HDR] _DissolveColor ("消融边缘颜色", Vector) = (1,0.6,0,0)
		_DissolveColorIntensity ("消融颜色强度", Float) = 2
		[Header(Rim______________________________________________________________________)] [Space(5)] _RimRange ("Rim Range", Range(0, 1)) = 0.2
		_RimIntensity ("Rim Intensity", Float) = 0.8
		_RimLightColor ("Rim Light Color", Vector) = (1,1,1,1)
		[Header(AO Type______________________________________________________________________)] [Enum(Off, 0, On, 1)] _AOType ("AO Type", Float) = 0
		_YHeight ("Height value", Float) = 30
		_YWidth ("Width value", Range(0, 1)) = 0.1
		_AOIntensity ("AO 强度", Range(0, 1)) = 0.5
		[Header(Stencil______________________________________________________________________)] [Space(5)] _StencilParam ("Stencil ID", Float) = 31
		[Enum(UnityEngine.Rendering.CompareFunction)] _StencilCompParam ("Stencil Comp", Float) = 8
		[Header(CutOff______________________________________________________________________)] [Space(5)] _Clip ("Clip", Range(0, 1)) = 0.5
		_Alpha ("Alpha", Range(0, 1)) = 1
		[Header(RemoveBloom______________________________________________________________________)] [Space(5)] [Toggle] _RemoveBloom ("RemoveBloom", Float) = 1
		_BloomThreshold ("Bloom Threshold", Range(0.5, 2)) = 1
		[Header(Selected______________________________________________________________________)] [Toggle] _Selected ("Selected", Float) = 0
		[HDR] _SelectedOutColor ("Selected Out Color", Vector) = (0,0,0,1)
		_FresnelOffset ("FresnelOffset", Range(-1, 1)) = 0
		_FresnelScale ("FresnelScale", Range(0, 1)) = 0.74
		[Header(Part Fresnel______________________________________________________________________)] [Toggle] _Part ("Part Fresnel", Float) = 0
		[HDR] _PartOutColor ("Part Out Color", Vector) = (0,0,0,1)
		_PartOffset ("PartFresnelOffset", Range(-1, 1)) = 0
		_PartScale ("PartFresnelScale", Range(0, 1)) = 0.74
		[Header(ShodowData______________________________________________________________________)] _ShodowIntenisty ("Shodow Intenisty", Range(0, 1)) = 0
		[Toggle] _Use_Shear ("错切", Float) = 0
		[HideInInspector] _isEnableCharRain ("是否启用角色下雨效果", Float) = 1
		[HideInInspector] _isBlackRole ("是否角色黑色（或者变暗）", Float) = 0
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
		Pass
		{
			Name "Forward"
			Tags { "LightMode"="UniversalForward" }
			ZWrite On
			Cull Back

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex CharacterVert
			#pragma fragment CharacterFrag
			#pragma multi_compile_local _ GPUSKIN_ON

			#include "Assets/Shell/Resources/Ro3Shaders/Game/ROCharacter.hlsl"
			ENDHLSL
		}
	}
}
