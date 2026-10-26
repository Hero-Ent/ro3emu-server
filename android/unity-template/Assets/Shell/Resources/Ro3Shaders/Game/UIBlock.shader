// Port of the game's HappyEngine/UIBlock (D3D11 only in the PC client): a depth-only blocker. From the disassembly its
// program outputs (0,0,0,0) and the pass state is ColorMask 0, ZWrite On, ZTest LEqual, no culling, no stencil; the UI
// stand-in drew it as a visible surface.
Shader "HappyEngine/UIBlock"
{
    Properties
    {
        _ZTest ("ZTest", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert (float4 vertex : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(vertex);
            }

            fixed4 frag () : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
}
