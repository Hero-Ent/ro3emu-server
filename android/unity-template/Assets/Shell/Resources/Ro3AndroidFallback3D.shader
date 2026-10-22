// Stand-in for the game's opaque 3D shaders (scenery, characters, props) until they are ported: the material's
// texture and colour (URP's _BaseMap/_BaseColor or the classic _MainTex/_Color, whichever it carries), lit by URP's
// main light with a little ambient, depth-tested and depth-written. The scenery comes through BatchRendererGroup
// (HappyEngine.CustomScene.BatchManager), so the shader is DOTS-instanced; plain renderers and GPU instancing work too.
// Ro3Android.ShaderFixer picks it for scenery, character and terrain shader names.
Shader "Hidden/Ro3Android/Fallback3D"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0
        // GPUDrivenTerrain reads these back from its material (CPU mode): declared so the swap keeps them.
        _HeightMap ("HeightMap", 2D) = "white" {}
        _NormalMap ("NormalMap", 2D) = "white" {}
        _SplatAtlas ("SplatAtlas", 2D) = "black" {}
        _Control ("Control", 2D) = "white" {}
        _Control2 ("Control2", 2D) = "black" {}
        _Control3 ("Control3", 2D) = "white" {}
        _Control4 ("Control4", 2D) = "black" {}
        _Index ("Index", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Off            // sky domes are seen from inside

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_local _ GPUSKIN_ON
            #pragma multi_compile_local _ _ALPHATEST_ON

            #include "Assets/Ro3Shaders/Lib/universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Ro3Shaders/Lib/universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _MainTex_ST;
                half4 _BaseColor;
                half4 _Color;
                half _Cutoff;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
            #endif

            // Characters animate through HappyEngine.DOTSAnimation: the CPU writes each skinning matrix (bone x bind pose,
            // rows 0-2, 3 RGBAFloat pixels) into _GlobalBoneMatricesTexture, laid out as SkinMatricesTextureLayout.
            // ComputePixelIndex says, and each renderer's first matrix goes in lightmapScaleOffset.x (y = 1: not skinned).
            #ifdef GPUSKIN_ON
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
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            #ifdef GPUSKIN_ON
                float4 weights : BLENDWEIGHTS;
                uint4 indices : BLENDINDICES;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert (Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                float3 position = input.positionOS.xyz;
                float3 normal = input.normalOS;
            #ifdef GPUSKIN_ON
                // Without weights or before the first upload the mesh keeps its bind pose.
                if (unity_LightmapST.y < 0.5 && _GlobalBoneMatricesTextureSize.w > 0 && dot(input.weights, 1) > 0.001)
                {
                    int first = (int)unity_LightmapST.x;
                    float3x4 skin = BoneMatrix(first + input.indices.x) * input.weights.x
                                  + BoneMatrix(first + input.indices.y) * input.weights.y
                                  + BoneMatrix(first + input.indices.z) * input.weights.z
                                  + BoneMatrix(first + input.indices.w) * input.weights.w;
                    position = mul(skin, float4(position, 1));
                    normal = mul((float3x3)skin, normal);
                }
            #endif
                o.positionCS = TransformObjectToHClip(position);
                o.uv = input.uv;
                o.normalWS = TransformObjectToWorldNormal(normal);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw)
                        * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv * _MainTex_ST.xy + _MainTex_ST.zw)
                        * _BaseColor * _Color;
                // Cut-out materials (foliage: _ALPHATEST_ON) only store _Cutoff when it differs from the game's
                // default, 0.5; this shader's own default (0) would keep every texel.
            #ifdef _ALPHATEST_ON
                clip(c.a - (_Cutoff > 0 ? _Cutoff : 0.5));
            #else
                clip(c.a - _Cutoff);
            #endif
                Light light = GetMainLight();
                half ndl = saturate(dot(normalize(i.normalWS), light.direction));
                c.rgb *= 0.45 + 0.75 * ndl * max(light.color, half3(0.6, 0.6, 0.6));
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }
    }
}
