// Port of the game's UI/UIDynamicGridMask (D3D11 only in the PC client): the world map's fog over the areas not
// explored yet (UIDynamicGridMaskMat on MaskNode_RTransform, fed by CP_UIDynamicGridMaskController). From the
// disassembly (android\tools\ShaderDis.cs, pass 0): colour = _MaskTex at the UVs through _MainTex_ST, times the vertex
// colour; RectMask2D clipping; blend, ZTest Always, no ZWrite, back-face culling and no stencil as in the client's pass
// state. The grid properties are only the material's: no program reads them. The plain stand-in sampled _MainTex (an
// empty Image: white) and covered the whole map in black.
Shader "UI/UIDynamicGridMask"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _MaskTex ("Grid Mask", 2D) = "white" {}
        _Color ("Tint Color", Color) = (1,1,1,1)
        _GridCols ("Grid Columns", Float) = 20
        _GridRows ("Grid Rows", Float) = 18
        _GridWidth ("Grid Width", Float) = 4000
        _GridHeight ("Grid Height", Float) = 3600
        _SingleGridWidth ("Single Grid Width", Float) = 200
        _SingleGridHeight ("Single Grid Height", Float) = 200
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha, SrcAlpha OneMinusSrcAlpha
        ZTest Always
        ZWrite Off
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            sampler2D _MaskTex;
            float4 _MainTex_ST;
            float4 _ClipRect;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 world : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.world = v.vertex;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MaskTex, i.uv) * i.color;
                #ifdef UNITY_UI_CLIP_RECT
                c.a *= UnityGet2DClipping(i.world.xy, _ClipRect);
                #endif
                return c;
            }
            ENDCG
        }
    }
}
