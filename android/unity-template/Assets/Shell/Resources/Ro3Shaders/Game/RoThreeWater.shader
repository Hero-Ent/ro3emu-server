// Port of the game's water (Base/RoThreeWater, D3D11 only in the PC client) from its DXBC disassembly
// (android\tools\ShaderDis.cs), variant _USE_WATER_CAUSTIC _USE_WATER_FOAM _USE_WATER_WAVE _TWO_WAVE (what the
// field's materials enable; with a feature off its texture or weight simply contributes nothing). Left out: rain
// ripples, ice, the blind effect and fog. Needs the camera's depth and opaque textures (Ro3Android.WorldCompat).
Shader "Base/RoThreeWater"
{
    Properties
    {
        _Water01 ("Direction (XY) speed (Z) depth (W)", Vector) = (1,1,0.1,3.8)
        _LihgtDirection ("Specular direction (XYZ) brightness (W)", Vector) = (0,0,0,0.18)
        _Distort ("Distortion", Range(0, 1)) = 0.05
        _SpecularColor ("Specular colour", Color) = (1,1,1,1)
        _SpecularIntensity ("Specular intensity", Range(0, 3)) = 1
        _SpecularSmoothness ("Specular size", Range(0, 30)) = 14.5
        _WaterColor ("Deep water colour", Color) = (1,1,1,1)
        _DepthFade ("Depth fade", Range(0.01, 30)) = 3.5
        _NormalTex ("NormalTex", 2D) = "white" {}
        _NormalScale ("Normal scale", Range(0.01, 1)) = 0.05
        _CausticTex ("Caustic (RG) noise (B) foam (A)", 2D) = "black" {}
        _CausticIntensity ("Caustic intensity", Range(0, 2)) = 1.5
        _HeightCaustic ("Caustic height", Range(0, 1)) = 0.25
        _FoamColor ("Foam colour", Color) = (0.1686,0.1686,0.1686,1)
        _FoamXSize ("Foam X size", Float) = 0.15
        _FoamYSize ("Foam Y size", Float) = 0.3
        _FlipFoam ("Flip foam", Float) = 0
        _FoamSpeed ("Foam speed", Range(-1, 1)) = 0.1
        _FoamRange ("Foam range", Range(0, 2)) = 0.1
        _FoamNoise ("Foam noise", Range(-1, 1)) = -0.25
        _WaveXSize ("Wave X size", Float) = 0.1
        _WaveYSize ("Wave Y size", Float) = 0.1
        _DepthEdgeTransparency ("Edge transparency", Float) = 0.1
        _WaveLoopPower ("Wave power", Range(0, 2)) = 0.5
        _WaveLoopSpeed ("Wave speed", Range(0, 4)) = 1
        _WaveLoopRandom ("Wave randomness", Range(0, 1)) = 1
        _WaveScale ("Wave (tiling 1, tiling 2, weight, strength)", Vector) = (0.063,0.25,0.5,1)
        _WaveSpeed ("Flow (1x,1y,2x,2y)", Vector) = (19,9,-16,-7)
        _Wave2Brightness ("Wave 2 brightness", Range(0, 4)) = 1.5
        _Wave2LoopPower ("Wave 2 range", Range(0, 7)) = 3.5
        _Wave2LoopRandom ("Wave 2 width", Range(1, 6)) = 3.5
        _WaveTex ("WaveTex", 2D) = "black" {}
        _ReflectionTex ("Reflection", Cube) = "black" {}
        _MipMapStrength ("Reflection mip", Range(0, 10)) = 1
        _ReflectionTwistStrength ("Reflection distortion", Range(0, 1)) = 0.25
        _WaterRampTexture ("Water ramp", 2D) = "white" {}
        _LowWaterColor ("Low-end colour", Color) = (1,1,1,1)
        [HideInInspector] _BoxMin ("Box Min", Vector) = (0,0,0,0)
        [HideInInspector] _BoxMax ("Box Max", Vector) = (0,0,0,0)
        [HideInInspector] _BoxPosition ("Probe Position", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Water"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Assets/Ro3Shaders/Lib/universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _NormalTex_ST;
                float4 _CausticTex_ST;
                float4 _Water01;
                float4 _FoamColor;
                float4 _SpecularColor;
                float _DepthEdgeTransparency;
                float4 _BoxMin;
                float4 _BoxMax;
                float4 _BoxPosition;
                float _MipMapStrength;
                float _ReflectionTwistStrength;
                float4 _WaveScale;
                float4 _WaveSpeed;
                float _WaveLoopSpeed;
                float _WaveLoopRandom;
                float _WaveLoopPower;
                float _FoamXSize;
                float _FoamYSize;
                float _WaveXSize;
                float _WaveYSize;
                float _HeightCaustic;
                float _FlipFoam;
                float _FoamSpeed;
                float _FoamRange;
                float _FoamNoise;
                float _Distort;
                float _CausticIntensity;
                float _SpecularIntensity;
                float _SpecularSmoothness;
                float _NormalScale;
                float4 _WaterColor;
                float4 _LihgtDirection;
                float _DepthFade;
                float _Wave2LoopPower;
                float _Wave2LoopRandom;
                float _Wave2Brightness;
                float4 _LowWaterColor;
            CBUFFER_END
            float _IndirectLightScale;

            TEXTURE2D(_NormalTex); SAMPLER(sampler_NormalTex);
            TEXTURE2D(_WaveTex); SAMPLER(sampler_WaveTex);
            TEXTURECUBE(_ReflectionTex); SAMPLER(sampler_ReflectionTex);
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            TEXTURE2D(_WaterRampTexture); SAMPLER(sampler_WaterRampTexture);
            TEXTURE2D(_CameraDepthTexture); SAMPLER(sampler_CameraDepthTexture);
            TEXTURE2D(_CameraOpaqueTexture); SAMPLER(sampler_CameraOpaqueTexture);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 foamUV : TEXCOORD0;
                float4 normalUV : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 positionVS : TEXCOORD3;
                float2 flow : TEXCOORD4;
                float3 normalWS : TEXCOORD5;
                float3 tangentWS : TEXCOORD6;
                float3 bitangentWS : TEXCOORD7;
                float4 screenPos : TEXCOORD8;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float3 world = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(world);
                o.foamUV = float2(world.x * _FoamXSize, world.z * _FoamYSize);
                float t = _Time.y * _Water01.z;
                float2 uv = world.xz * _NormalScale * _NormalTex_ST.xy + _NormalTex_ST.zw;
                float2 flow = t * _Water01.xy;
                o.normalUV = float4(flow + uv, flow * float2(-1.07, 1.13) + uv);
                o.flow = flow;
                o.positionWS = world;
                o.positionVS = TransformWorldToView(world);
                o.normalWS = normalize(TransformObjectToWorldNormal(v.normalOS));
                o.tangentWS = normalize(TransformObjectToWorldDir(v.tangentOS.xyz));
                float handedness = v.tangentOS.w * (unity_WorldTransformParams.w >= 0 ? 1 : -1);
                o.bitangentWS = cross(o.normalWS, o.tangentWS) * handedness;
                o.screenPos = ComputeScreenPos(o.positionCS);
                return o;
            }

            float3 UnpackWater(float4 s)
            {
                float2 xy = float2(s.x * s.z, s.y) * 2 - 1;
                return float3(xy, max(sqrt(1 - min(dot(xy, xy), 1)), 0));
            }

            float EyeDepth(float2 uv) { return 1 / (_ZBufferParams.z * SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r + _ZBufferParams.w); }

            float Smooth(float x) { return x * x * (3 - 2 * x); }

            half4 frag (Varyings i) : SV_Target
            {
                float3 n1 = UnpackWater(SAMPLE_TEXTURE2D(_NormalTex, sampler_NormalTex, i.normalUV.xy));
                float3 n2 = UnpackWater(SAMPLE_TEXTURE2D(_NormalTex, sampler_NormalTex, i.normalUV.zw));
                float3 n = saturate(n1 + n2);
                float3 d = n * _Distort;
                float3 distorted = d.x * i.tangentWS + d.y * i.bitangentWS + d.z * i.normalWS;
                float3 normalWS = n.x * i.tangentWS + n.y * i.bitangentWS + n.z * i.normalWS;
                float3 L = normalize(_LihgtDirection.xyz);
                float spec = pow(saturate(dot(normalWS, L)), _SpecularSmoothness) * _SpecularIntensity;
                float3 toCamera = _WorldSpaceCameraPos - i.positionWS;
                float3 V = normalize(toCamera);
                float fresnel = pow(1 - saturate(dot(i.normalWS, V)), 3);

                // Reflection: box-projected cube map.
                float3 R = reflect(-V, n2 * _ReflectionTwistStrength + distorted);
                float3 inv = 1 / R;
                float3 far = max((_BoxMax.xyz - i.positionWS) * inv, (_BoxMin.xyz - i.positionWS) * inv);
                R = R * min(min(far.x, far.y), far.z) + i.positionWS - _BoxPosition.xyz;
                half3 reflection = SAMPLE_TEXTURECUBE_LOD(_ReflectionTex, sampler_ReflectionTex, R, _MipMapStrength).rgb;

                // Water depth below the surface, from the camera depth texture.
                float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float depth = EyeDepth(screenUV) + i.positionVS.z;
                float depthFactor = saturate(depth / _DepthFade);
                float wave2Fade = Smooth(saturate(1 - depth / _Wave2LoopPower));
                float wave2Width = min(depth, _Wave2LoopRandom) / _Wave2LoopRandom;
                float2 refractUV = n.xy * _Distort + screenUV;
                if (EyeDepth(refractUV) + i.positionVS.z < 0) refractUV = screenUV;

                // Edge transparency and foam.
                float2 noiseUV = frac(((_WaveSpeed.xy * _Time.x + i.positionWS.xz) * _WaveScale.x + float2(0, 0.5)) * float2(_WaveXSize, _WaveYSize));
                float noise = SAMPLE_TEXTURE2D(_CausticTex, sampler_CausticTex, noiseUV).z;
                float loop = frac((noise * TWO_PI * _WaveLoopRandom + _Time.y * _WaveLoopSpeed) * 0.159155) * TWO_PI;
                float edge = max(depth - _WaveLoopPower * (sin(loop) * 0.5 + 0.5), 0);
                float alpha = edge * _DepthEdgeTransparency;
                float foamTex = SAMPLE_TEXTURE2D(_CausticTex, sampler_CausticTex, n.xy * 0.05 + i.foamUV + _Time.y * _FoamSpeed).w;
                float foamMask = _FlipFoam != 0 ? 1 - foamTex : foamTex;
                float foam = Smooth(saturate((pow(abs(foamMask), _FoamNoise) - edge * _DepthEdgeTransparency) / (alpha * _FoamRange)));

                // Second wave: a band rolling towards the shore.
                float phase = noise * 0.5 + _Time.y;
                float roll = 1 - pow(1 - frac(phase * 0.183333), 2);
                float bend = (sin(i.positionWS.x * 0.166667 + phase * 1.1) * 1.1 + 0.6) * 0.5 + 0.5;
                float waveTex = SAMPLE_TEXTURE2D(_WaveTex, sampler_WaveTex, float2(pow(roll, bend) + wave2Width, 1)).x;
                float crest = max(foamTex >= 1 - waveTex ? 0.4 : 0, Smooth(saturate((waveTex * foamTex + waveTex - 0.77) * 20)));
                float wave2 = roll * (1 - roll) * 4 * crest * wave2Fade * _Wave2Brightness;
                foam = max(max(wave2, foam) * 0.5, foam);

                // Refraction, depth colour (ramp) and caustics on the bottom.
                float rampU = depth * _Water01.w;
                half3 rampShallow = SAMPLE_TEXTURE2D(_WaterRampTexture, sampler_WaterRampTexture, float2(rampU, 0)).rgb;
                half3 rampDeep = SAMPLE_TEXTURE2D(_WaterRampTexture, sampler_WaterRampTexture, float2(rampU, 1)).rgb;
                half3 color = SAMPLE_TEXTURE2D(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, refractUV).rgb;
                color += rampShallow * _LihgtDirection.w;
                float bottomEye = EyeDepth(refractUV);
                float2 bottomXY = bottomEye * i.positionVS.xy / -i.positionVS.z;
                float3 bottom = mul(unity_CameraToWorld, float4(bottomXY, bottomEye, 1)).xyz;
                float4 causticUV = bottom.y * float4(0.2, 0.2, 0.1, 0.1) + bottom.xzxz * _CausticTex_ST.xyxy;
                half4 c1 = SAMPLE_TEXTURE2D(_CausticTex, sampler_CausticTex, causticUV.xy + i.flow);
                half4 c2 = SAMPLE_TEXTURE2D(_CausticTex, sampler_CausticTex, i.flow * float2(1.07, 1.43) + causticUV.zw);
                half3 caustic = float3(min(c2.x, c1.x), min(c2.y, c1.y), min(c2.x, c1.y)) * _CausticIntensity;
                caustic -= caustic * Smooth(saturate((depth * _HeightCaustic - 0.35) * 2.222222));
                color += caustic;
                color = lerp(color * rampDeep, _WaterColor.rgb, depthFactor);
                color += foam * _FoamColor.rgb + reflection * fresnel + spec * _SpecularColor.rgb;
                float indirect = _IndirectLightScale > 0 ? _IndirectLightScale : 1;
                return half4(color * indirect, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
