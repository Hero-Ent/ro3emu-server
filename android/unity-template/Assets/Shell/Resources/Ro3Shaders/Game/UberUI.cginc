// The code of the HappyEngine/UberUI/Main port (UberUI.shader) and of its Soft Mask variant (SoftMaskUberUI.shader,
// which defines RO3_UBERUI_SOFTMASK first).
#include "UnityCG.cginc"
#include "UnityUI.cginc"
#ifdef RO3_UBERUI_SOFTMASK
    // The client's Soft Mask/HappyEngine/UberUI/Main has no SOFTMASK_* keywords: its mask is always the simple one.
    #define SOFTMASK_SIMPLE 1
#endif
#include "Assets/Shell/Resources/Ro3Shaders/Game/SoftMask.cginc"

sampler2D _MainTex, _MaskTex, _MaskTex2, _DissolveTex;
float _Main_Intensity, _MainTex_Channel, _MainTex_Clamp, _Main_Rotation, _Main_Rotation_Speed;
float4 _Main_Tilling, _Main_Offset, _Main_Panner, _Main_Anchor;
float _USE_MASK, _Mask_Intensity, _MaskTex_Channel, _MaskTex_Clamp, _Mask_Rotation, _Mask_Rotation_Speed;
float4 _Mask_Tilling, _Mask_Offset, _Mask_Panner, _Mask_Anchor;
float _USE_MASK2, _Mask_Intensity2, _MaskTex_Channel2, _MaskTex_Clamp2, _Mask_Rotation2, _Mask_Rotation_Speed2;
float4 _Mask_Tilling2, _Mask_Offset2, _Mask_Panner2, _Mask_Anchor2;
float _USE_DISSOLVE, _Dissolve_Intensity, _DissolveTex_Channel, _DissolveStrength;
float4 _Dissolve_Tilling, _Dissolve_Offset, _Dissolve_Panner;
float4 _ClipRect;

struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float4 world : TEXCOORD1; float4 mask : TEXCOORD2; };

// Tiling/offset/panning, then a rotation (range -1..1 = -180..180 degrees, plus speed) around the anchor.
float2 Transform(float2 uv, float4 tiling, float4 offset, float4 panner, float4 anchor, float rotation, float speed, float clampMode)
{
    uv = uv * tiling.xy + offset.xy + panner.xy * _Time.y;
    float a = (rotation + speed * _Time.y) * UNITY_PI;
    float s = sin(a), c = cos(a);
    uv = mul(float2x2(c, -s, s, c), uv - anchor.xy) + anchor.xy;
    return clampMode > 0.5 ? saturate(uv) : uv;
}

float Channel(float4 t, float channel)
{
    return channel < 0.5 ? t.r : channel < 1.5 ? t.g : channel < 2.5 ? t.b : t.a;
}

v2f vert (appdata v)
{
    v2f o;
    o.world = v.vertex;
    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv = v.uv;
    o.color = v.color;
    o.mask = SoftMaskPosition(v.vertex);
    return o;
}

fixed4 frag (v2f i) : SV_Target
{
    float4 t = tex2D(_MainTex, Transform(i.uv, _Main_Tilling, _Main_Offset, _Main_Panner, _Main_Anchor, _Main_Rotation, _Main_Rotation_Speed, _MainTex_Clamp));
    // Channel 0 = RGB(A), 1..3 = R, G, B as a grey value and its alpha.
    float4 c = _MainTex_Channel < 0.5 ? t : Channel(t, _MainTex_Channel - 1).xxxx;
    c.rgb *= _Main_Intensity;
    if (_USE_MASK > 0.5)
        c.a *= saturate(Channel(tex2D(_MaskTex, Transform(i.uv, _Mask_Tilling, _Mask_Offset, _Mask_Panner, _Mask_Anchor, _Mask_Rotation, _Mask_Rotation_Speed, _MaskTex_Clamp)), _MaskTex_Channel) * _Mask_Intensity);
    if (_USE_MASK2 > 0.5)
        c.a *= saturate(Channel(tex2D(_MaskTex2, Transform(i.uv, _Mask_Tilling2, _Mask_Offset2, _Mask_Panner2, _Mask_Anchor2, _Mask_Rotation2, _Mask_Rotation_Speed2, _MaskTex_Clamp2)), _MaskTex_Channel2) * _Mask_Intensity2);
    if (_USE_DISSOLVE > 0.5)
    {
        float d = Channel(tex2D(_DissolveTex, i.uv * _Dissolve_Tilling.xy + _Dissolve_Offset.xy + _Dissolve_Panner.xy * _Time.y), _DissolveTex_Channel) * _Dissolve_Intensity;
        c.a *= step(_DissolveStrength, d);
    }
    c *= i.color;
    #ifdef UNITY_UI_CLIP_RECT
    c.a *= UnityGet2DClipping(i.world.xy, _ClipRect);
    #endif
    c.a *= SoftMaskValue(i.mask.xy);
    return c;
}
