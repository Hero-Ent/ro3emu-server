// Stand-in that draws nothing, for the game's outline, shadow-blob and depth-output shaders until they are ported:
// the generic fallbacks would paint their inverted hulls and projected quads as solid shapes over the models.
Shader "Hidden/Ro3Android/Invisible"
{
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            ColorMask 0
            ZWrite Off
            ZTest Never
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert (float4 v : POSITION) : SV_POSITION { return float4(2, 2, 2, 1); }
            fixed4 frag () : SV_Target { return 0; }
            ENDCG
        }
    }
}
