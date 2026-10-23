// Port of the game's Hidden/UIOverlay/Composite (UIOverlay.UIOverlayFeature): cmd.Blit of the UI/model render
// texture over the camera colour. Without it Ro3Android.Hooks.Shaders handed the feature the generic fallback, which
// drew that texture upside down on Vulkan (a mirrored copy of the HUD on the upper half): render textures there start
// at the top, so V is flipped here. Premultiplied-alpha composite, as UI rendered into a texture comes out.
Shader "Hidden/UIOverlay/Composite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" }
        ZTest Always
        ZWrite Off
        Cull Off
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1 - o.uv.y;
                #endif
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }
    }
}
