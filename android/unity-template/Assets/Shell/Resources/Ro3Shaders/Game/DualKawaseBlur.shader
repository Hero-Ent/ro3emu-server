// Port of the game's ROMeta/URP/DualKawaseBlur (D3D11 only in the PC client), the blur behind every dialog and full
// window (UICaptureBlur -> CaptureBlurManager -> Obf_gA). Left without programs (ShaderFixer keeps full-screen shaders
// as they are), its blits drew nothing: the dialogs showed the raw screen capture, sharp and upside down, because the
// capture of a frame with its UI comes flipped and the client turns it back with _FLIP on the first pass (and, on
// OpenGL, on the last), which this shader's vertex stage applies: uv.y = 1 - uv.y when _FLIP > 0.5.
// Both passes from the D3D11 programs (ShaderDis): offsets in texels of _MainTex, scaled by _Offset + 1.
//   0, down: the centre x4 plus the four diagonals at half a texel, / 8.
//   1, up: the four axis taps at one texel and the four diagonals at half a texel x2, / 12.
Shader "ROMeta/URP/DualKawaseBlur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Offset ("Offset", Float) = 1
        _FLIP ("Flip", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        float _Offset;
        float _FLIP;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        float2 FlippedUv(float2 uv)
        {
            return float2(uv.x, _FLIP > 0.5 ? 1.0 - uv.y : uv.y);
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
                float4 uv01 : TEXCOORD1;
                float4 uv23 : TEXCOORD2;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = FlippedUv(v.uv);
                float2 halfStep = _MainTex_TexelSize.xy * 0.5 * (_Offset + 1.0);
                o.uv = uv;
                o.uv01.xy = uv - halfStep;
                o.uv01.zw = uv + halfStep;
                o.uv23.xy = uv + float2(-halfStep.x, halfStep.y);
                o.uv23.zw = uv + float2(halfStep.x, -halfStep.y);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 sum = tex2D(_MainTex, i.uv) * 4.0;
                sum += tex2D(_MainTex, i.uv01.xy);
                sum += tex2D(_MainTex, i.uv01.zw);
                sum += tex2D(_MainTex, i.uv23.xy);
                sum += tex2D(_MainTex, i.uv23.zw);
                return sum * 0.125;
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uv01 : TEXCOORD1;
                float4 uv23 : TEXCOORD2;
                float4 uv45 : TEXCOORD3;
                float4 uv67 : TEXCOORD4;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = FlippedUv(v.uv);
                float scale = _Offset + 1.0;
                float2 halfStep = _MainTex_TexelSize.xy * 0.5 * scale;
                float2 fullStep = _MainTex_TexelSize.xy * scale;
                // Axis taps (weight 1) in .xy, diagonal taps (weight 2) in .zw.
                o.uv01.xy = uv + float2(-fullStep.x, 0);
                o.uv01.zw = uv + float2(-halfStep.x, halfStep.y);
                o.uv23.xy = uv + float2(0, fullStep.y);
                o.uv23.zw = uv + float2(halfStep.x, halfStep.y);
                o.uv45.xy = uv + float2(fullStep.x, 0);
                o.uv45.zw = uv + float2(halfStep.x, -halfStep.y);
                o.uv67.xy = uv + float2(0, -fullStep.y);
                o.uv67.zw = uv + float2(-halfStep.x, -halfStep.y);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 sum = tex2D(_MainTex, i.uv01.xy) + tex2D(_MainTex, i.uv01.zw) * 2.0;
                sum += tex2D(_MainTex, i.uv23.xy) + tex2D(_MainTex, i.uv23.zw) * 2.0;
                sum += tex2D(_MainTex, i.uv45.xy) + tex2D(_MainTex, i.uv45.zw) * 2.0;
                sum += tex2D(_MainTex, i.uv67.xy) + tex2D(_MainTex, i.uv67.zw) * 2.0;
                return sum * 0.0833;
            }
            ENDCG
        }
    }
}
