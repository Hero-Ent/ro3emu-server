// Port of the game's terrain shader (Base/GPUDrivenTerrainAtlas, D3D11 only in the PC client) from its DXBC
// disassembly (android\tools\ShaderDis.cs): the CPU-mode path HappyEngine.GPUDrivenTerrain takes without compute
// shaders. Pass 0 draws the patches (DrawMeshInstancedProcedural, _PatchPosition/_PatchLODTrans per instance), "Bake"
// paints the splat layers into the clipmap virtual texture pass 0 reads (_ClipmapVTs0). Left out: rain/snow, the
// "faker" point lights, the blind effect, fog and the 16-layer bake (Bake handles _Control/_Control2, 8 layers).
Shader "Base/GPUDrivenTerrainAtlas"
{
    Properties
    {
        [HDR] _Color ("Tint", Color) = (1,1,1,1)
        _HeightMap ("HeightMap", 2D) = "white" {}
        _NormalMap ("NormalMap", 2D) = "white" {}
        _UsePixelFog ("Pixel fog", Float) = 0
        _SplatAtlas ("SplatAtlas", 2D) = "black" {}
        _Control ("Control", 2D) = "white" {}
        _Control2 ("Control2", 2D) = "black" {}
        _Control3 ("Control3", 2D) = "white" {}
        _Control4 ("Control4", 2D) = "black" {}
        _Index ("Index", 2D) = "black" {}
        _MipmapCtrl ("Mipmap", Range(0.0125, 1)) = 0.0125
        _BorderPixels ("Border pixels", Float) = 1
        _CustomShadowMaskTex ("Custom ShadowMask", 2D) = "white" {}
        _HighResLightmap ("High-res lightmap", 2D) = "black" {}
        _HighResShadowmask ("High-res shadowmask", 2D) = "white" {}
        _HighResUVRect ("High-res UV rect", Vector) = (0,0,0,0)
        _HighResLightmapST ("High-res lightmap ST", Vector) = (1,1,0,0)
        _TerrainDataSize ("_TerrainDataSize", Vector) = (0,0,0,0)
        _SplatCount ("Splat Count", Float) = 8
        _EvaluationFactor ("Evaluation Factor", Float) = 1
        _SplatSize ("Splat Size", Float) = 512
    }

    HLSLINCLUDE
    #include "Assets/Ro3Shaders/Lib/core/ShaderLibrary/Common.hlsl"

    float4x4 unity_MatrixVP;
    float4 _Control_ST;
    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        // Pass 0 (p0 vs/ps, LIGHTMAP_ON _SIMPLE_RVT PROCEDURAL_INSTANCING_ON): the terrain patches.
        Pass
        {
            Name "RVT"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            CBUFFER_START(PatchListFullback)
                float4 _PatchPosition[512];     // xy: patch centre (terrain space), z: LOD
                float4 _PatchLODTrans[512];     // LOD step of the neighbour on each side (x left, y bottom, z right, w top)
            CBUFFER_END
            int _PatchQuadNum;
            float3 _SquareTerrainSize;
            float4x4 _ObjectToWorldMatrix;
            float4 patchScale;
            float4 _TerrainLightmapST;
            float4 _HighResUVRect;
            float4 _HighResLightmapST;
            float4 _MainLightPosition;
            float4 _MainLightColor;

            TEXTURE2D(_HeightMap); SAMPLER(sampler_HeightMap);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
            TEXTURE2D(_ClipmapVTs0); SAMPLER(sampler_ClipmapVTs0);
            TEXTURE2D(unity_Lightmap); SAMPLER(samplerunity_Lightmap);
            TEXTURE2D(unity_ShadowMask); SAMPLER(samplerunity_ShadowMask);
            TEXTURE2D(_HighResLightmap); SAMPLER(sampler_HighResLightmap);
            TEXTURE2D(_HighResShadowmask); SAMPLER(sampler_HighResShadowmask);

            struct Attributes
            {
                float4 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 rvtUV : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 lightmapUV : TEXCOORD2;
            };

            // A patch edge that borders a coarser patch snaps its odd vertices onto the coarser grid (no cracks).
            float2 StitchedGrid(float2 v, uint4 trans)
            {
                uint n = (uint)_PatchQuadNum;
                uint gz = (uint)floor(v.y + n * 0.5 + 0.01);
                uint gx = (uint)floor(v.x + n * 0.5 + 0.01);
                float x = v.x, z = v.y;
                if (trans.x > 0 && gx == 0)
                {
                    uint step = (uint)exp2((float)trans.x);
                    uint up = (step - gz % step) % step;
                    if (up != 0) return float2(x, z + up);
                }
                if (trans.y > 0 && gz == 0)
                {
                    uint step = (uint)exp2((float)trans.y);
                    uint up = (step - gx % step) % step;
                    if (up != 0) return float2(x + up, z);
                }
                if (trans.z > 0 && gx == n)
                {
                    uint step = (uint)exp2((float)trans.z);
                    uint up = (step - gz % step) % step;
                    if (up != 0) return float2(x, z - (step - up));
                }
                if (trans.w > 0 && gz == n)
                {
                    uint step = (uint)exp2((float)trans.w);
                    uint up = (step - gx % step) % step;
                    if (up != 0) x -= step - up;
                }
                return float2(x, z);
            }

            Varyings vert (Attributes input)
            {
                Varyings o;
                float4 patch = _PatchPosition[input.instanceID];
                uint lod = (uint)patch.z;
                float2 grid = StitchedGrid(input.positionOS.xz, (uint4)_PatchLODTrans[input.instanceID]);
                float2 xz = grid * exp2((float)lod) + patch.xy;
                float2 terrainUV = xz / _SquareTerrainSize.xz;
                float height = SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, terrainUV, 0).r * _SquareTerrainSize.y;
                float3 positionWS = mul(_ObjectToWorldMatrix, float4(xz.x, height, xz.y, 1)).xyz;
                o.positionCS = mul(unity_MatrixVP, float4(positionWS, 1));
                o.rvtUV = positionWS.xz / patchScale.xy * _Control_ST.xy + _Control_ST.zw;
                o.normalWS = SAMPLE_TEXTURE2D_LOD(_NormalMap, sampler_NormalMap, terrainUV, lod).xyz * 2 - 1;
                o.lightmapUV = terrainUV * _TerrainLightmapST.xy + _TerrainLightmapST.zw;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half3 albedo = SAMPLE_TEXTURE2D(_ClipmapVTs0, sampler_ClipmapVTs0, i.rvtUV).rgb;
                half3 lightmap;
                half shadow;
                if (all(i.lightmapUV >= _HighResUVRect.xy) && all(i.lightmapUV < _HighResUVRect.zw))
                {
                    float2 uv = i.lightmapUV * _HighResLightmapST.xy + _HighResLightmapST.zw;
                    lightmap = SAMPLE_TEXTURE2D(_HighResLightmap, sampler_HighResLightmap, uv).rgb;
                    shadow = SAMPLE_TEXTURE2D(_HighResShadowmask, sampler_HighResShadowmask, uv).r;
                }
                else
                {
                    lightmap = SAMPLE_TEXTURE2D(unity_Lightmap, samplerunity_Lightmap, i.lightmapUV).rgb;
                    shadow = SAMPLE_TEXTURE2D(unity_ShadowMask, samplerunity_ShadowMask, i.lightmapUV).r;
                }
                half ndl = saturate(dot(normalize(i.normalWS), _MainLightPosition.xyz));
                half3 light = lightmap * 2 + shadow * ndl * _MainLightColor.rgb;
                return half4(albedo * light, 1);
            }
            ENDHLSL
        }

        // "Bake" (p3): one clipmap tile of the splat layers, top-down (_DownViewMatrix / _DownProjectionMatrix).
        Pass
        {
            Name "Bake"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            float4 patchScaleOffset;
            float4 patchScale;
            float4x4 _DownProjectionMatrix;
            float4x4 _DownViewMatrix;
            float4 _TerrainDataSize;
            int _SplatCount;
            float _BorderPixels;
            float4 _SplatAtlas_TexelSize;
            float _SplatSize;
            float _ClipmapID;
            float4 _tilingOffset[16];
            float4 _TerrainHeightWeight[16];
            float4 _TintColor[16];
            float4 _SplatLod[16];

            TEXTURE2D(_Control); SAMPLER(sampler_Control);
            TEXTURE2D(_Control2); SAMPLER(sampler_Control2);
            TEXTURE2D(_SplatAtlas); SAMPLER(sampler_SplatAtlas);

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 controlUV : TEXCOORD0;
                float2 terrainUV : TEXCOORD1;
            };

            Varyings vert (Attributes input)
            {
                Varyings o;
                float2 xz = input.positionOS.xz * patchScaleOffset.xy + patchScaleOffset.zw;
                float4 view = mul(_DownViewMatrix, float4(xz.x, input.positionOS.y, xz.y, 1));
                o.positionCS = mul(_DownProjectionMatrix, view);
                o.terrainUV = (xz - patchScale.zw) / _TerrainDataSize.xz;
                o.controlUV = o.terrainUV * _Control_ST.xy + _Control_ST.zw;
                return o;
            }

            half4 SampleLayer(int layer, float2 terrainUV)
            {
                float rows = 2 * ceil(_SplatCount * 0.125);
                float2 cell = float2(0.25, 1 / rows);
                float2 corner = float2((layer & 3) * 0.25, floor(layer * 0.25) * cell.y);
                int clipmap = (int)_ClipmapID;
                float mip = clipmap >= 0 ? clipmap : (int)_SplatLod[layer].x;
                float scale = exp2(mip);
                float inner = _SplatSize / scale;
                float tile = inner + 2;
                float2 atlas = floor(_SplatAtlas_TexelSize.zw / scale) * cell / tile;
                float2 f = frac(terrainUV * _tilingOffset[layer].xy + _tilingOffset[layer].zw) * (inner / tile);
                float2 uv = (f * atlas + _BorderPixels / tile) * cell + corner;
                return SAMPLE_TEXTURE2D_LOD(_SplatAtlas, sampler_SplatAtlas, uv, mip);
            }

            half4 frag (Varyings i) : SV_Target
            {
                float4 c1 = SAMPLE_TEXTURE2D(_Control, sampler_Control, i.controlUV);
                float4 c2 = SAMPLE_TEXTURE2D(_Control2, sampler_Control2, i.controlUV);
                c1 *= step(0.005, c1);
                c2 *= step(0.005, c2);
                float weights[8] = { c1.x, c1.y, c1.z, c1.w, c2.x, c2.y, c2.z, c2.w };
                // The four strongest layers, first one on ties.
                int index[4];
                float4 w;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int best = 0;
                    float bestW = -1;
                    [unroll] for (int l = 0; l < 8; l++)
                    {
                        bool taken = false;
                        [unroll] for (int t = 0; t < k; t++) taken = taken || index[t] == l;
                        if (!taken && weights[l] > bestW) { best = l; bestW = weights[l]; }
                    }
                    index[k] = best;
                    w[k] = bestW;
                }
                w /= max(w.x + w.y + w.z + w.w, 1e-5);
                half4 tex[4];
                float4 h;
                [unroll] for (int s = 0; s < 4; s++) { tex[s] = SampleLayer(index[s], i.terrainUV); h[s] = w[s] * tex[s].a; }
                float top = max(max(h.x, h.y), max(h.z, h.w));
                float4 d;
                [unroll] for (int s2 = 0; s2 < 4; s2++) d[s2] = saturate(h[s2] - top + 1 - _TerrainHeightWeight[index[s2]].x);
                float4 blend = w * d;
                float sum = blend.x + blend.y + blend.z + blend.w;
                blend = sum == 0 ? float4(1, 0, 0, 0) : blend / sum;
                int layers = min(_SplatCount, 4);
                half3 color = 0;
                [unroll] for (int s3 = 0; s3 < 4; s3++)
                    if (s3 < layers) color += blend[s3] * tex[s3].rgb * _TintColor[index[s3]].rgb;
                return half4(saturate(color), 1);
            }
            ENDHLSL
        }
    }
}
