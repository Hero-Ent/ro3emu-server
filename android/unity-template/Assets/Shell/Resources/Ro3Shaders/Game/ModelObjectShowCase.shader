// Port of the game's Base/ModelObjectShowCase (D3D11 only in the PC client): RoleUIStencilRenderFeature draws a cube
// with it around each character shown in the UI (the bag's preview, ModelDisplayCamera) to mark its area in the
// stencil. It never writes colour; the opaque stand-in drew the cube lit, an opaque grey box over the bag window.
// State from the client's only pass (LightMode StencilObject): ColorMask 0, ZTest LEqual, no ZWrite, back-face culling,
// Stencil Ref _StencilValue, Comp GEqual, Pass Replace. Its program outputs nothing the colour mask keeps.
Shader "Base/ModelObjectShowCase"
{
    Properties
    {
        _StencilValue ("Stencil Value", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        Pass
        {
            Tags { "LightMode"="StencilObject" }
            ColorMask 0
            ZTest LEqual
            ZWrite Off
            Cull Back
            Stencil
            {
                Ref [_StencilValue]
                Comp GEqual
                Pass Replace
            }
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
