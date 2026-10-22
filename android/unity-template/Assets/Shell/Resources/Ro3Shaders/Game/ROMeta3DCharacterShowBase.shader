// Port of ROMeta/3DCharacter/ShowBase (D3D11 only in the PC client); the code is in ROCharacter.hlsl. Properties: the
// original block (AssetRipper export), so materials that only store non-default values keep the game's defaults.
Shader "ROMeta/3DCharacter/ShowBase"
{
	Properties {
		[Header(BaseSettings________________________________________________________________________)] [Space(10)] [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("剔除模式 : 默认是Back ，Off是双面渲染", Float) = 2
		_StencilRef ("Stencil ID", Float) = 1
		[Space(10)] _BaseColor ("BaseColor", Vector) = (1,1,1,1)
		_AC_Color ("Art Color", Vector) = (1,1,1,1)
		_EmissionColor ("Emission Color", Vector) = (1,1,1,1)
		_LocalMetalStrength ("Local Metal Strength", Float) = 1
		_BaseMap ("颜色纹理，金属A", 2D) = "white" {}
		_Sequence ("Row(X) Column(Y) ID(Z)", Vector) = (1,1,0,0)
		[Header(Lighting_____________________________________________________________________________)] [Space(10)] _Cutoff ("AlphaClip", Range(0, 1.2)) = 0
		_Alpha ("Alpha", Range(0, 1)) = 1
		[NoScale] _ShadowTex ("RGB：阴影暗部颜色，A：Alpha CLip", 2D) = "white" {}
		_ShadowColor ("阴影色", Vector) = (1,1,1,1)
		_DirectionControl ("法线方向控制", Vector) = (-1,0.5,0,1)
		_LightingThr ("光照阈值", Range(-1, 1)) = 0.5
		_SoftEdge ("光照明暗线软硬", Range(0, 1)) = 0
		[Space(10)] [Header(MatCap______________________________________________________________________)] [Toggle] _MatCapToggle ("开启MatCap", Float) = 0
		[NoScaleOffset] _MatCapTex ("MatCapTex", 2D) = "black" {}
		_MatcapStrength ("Matcap 强度", Range(0, 1)) = 1
		_MatcapContrast ("Matcap 对比度", Range(0, 5)) = 1
		[Space(10)] [Header(StreamEffect____________________________________________________________________________)] [Space(10)] _StreamEffectTex ("流光 贴图", 2D) = "black" {}
		_uvStreamEffectSpeedX ("流光 uv流速X", Float) = 0
		_uvStreamEffectSpeedY ("流光 uv流速Y", Float) = -0.3
		[HDR] _StreamEffectColor ("流光颜色", Vector) = (1,1,1,0)
		_StreamEffectIntensity ("流光 强度", Float) = 0
		[Header(RemoveBloom______________________________________________________________________)] [Space(5)] [Toggle] _RemoveBloom ("RemoveBloom", Float) = 1
		_BloomThreshold ("Bloom Threshold", Range(0.5, 2)) = 1
		[Header(Outline______________________________________________________________________)] [Space(5)] _OutlineOfColor ("Outline Color", Vector) = (1,1,1,1)
		_OutlineWeight ("Outline Weight", Range(0, 1)) = 1
		_OutlineWidth ("Outline Width顶点色A能精确控制", Range(0, 30)) = 2
		[Header(Rim_____________________________________________________________________________________)] [Space(10)] _RimRange ("Rim Range", Range(0, 1)) = 0.2
		_RimIntensity ("Rim Intensity", Float) = 0.8
		[HDR] _RimLightColor ("Rim Light Color", Vector) = (1,1,1,1)
		_StencilValue ("StencilValue", Float) = 0
		[Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comp", Float) = 3
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
            #define RO_SHOWBASE
			#include "Assets/Shell/Resources/Ro3Shaders/Game/ROCharacter.hlsl"
			ENDHLSL
		}
	}
}
