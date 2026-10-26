// The Soft Mask package's shader side, for the ports of the game's "Hidden/UI Default (Soft Masked)" and
// "Soft Mask/<shader>" variants (the PC client has D3D11 programs only). The mask component sets these properties on
// the material it substitutes: _SoftMask (the mask's texture), _SoftMask_WorldToMask (canvas vertex -> mask rect space),
// _SoftMask_Rect / _SoftMask_UVRect (the mask rect and its UVs), _SoftMask_ChannelWeights (which channels make the mask)
// and, for sliced and tiled sprites, _SoftMask_BorderRect / _SoftMask_UVBorderRect / _SoftMask_TileRepeat. The names
// and the SOFTMASK_SIMPLE / _SLICED / _TILED keywords are the client's (Hidden/UI Default (Soft Masked)'s blob); the
// simple path follows the disassembly of Soft Mask/HappyEngine/UberFx/Particle (android\build\shaderdis3), the sliced
// and tiled ones the package's public source (inferred: no disassembly of those variants yet).
#ifndef RO3_SOFTMASK_INCLUDED
#define RO3_SOFTMASK_INCLUDED

sampler2D _SoftMask;
float4 _SoftMask_Rect;
float4 _SoftMask_UVRect;
float4x4 _SoftMask_WorldToMask;
float4 _SoftMask_ChannelWeights;
float4 _SoftMask_BorderRect;
float4 _SoftMask_UVBorderRect;
float2 _SoftMask_TileRepeat;

#if defined(SOFTMASK_SIMPLE) || defined(SOFTMASK_SLICED) || defined(SOFTMASK_TILED)
    #define RO3_SOFTMASK_ON 1
#endif

// Where a canvas-space vertex lies in the mask's rect (the vertex shader passes it on to the fragment).
float4 SoftMaskPosition(float4 vertex)
{
    return mul(_SoftMask_WorldToMask, vertex);
}

// a between a1 and a2 -> the matching point between u1 and u2 (a zero-width span maps to u1).
float2 SoftMaskInset(float2 a, float2 a1, float2 a2, float2 u1, float2 u2)
{
    float2 w = a2 - a1;
    return lerp(u1, u2, w != 0 ? (a - a1) / w : 0);
}

// Tiled: the centre span repeats its UVs _SoftMask_TileRepeat times.
float2 SoftMaskInset(float2 a, float2 a1, float2 a2, float2 u1, float2 u2, float2 repeat)
{
    float2 w = a2 - a1;
    float2 d = w != 0 ? (a - a1) / w : 0;
    return lerp(u1, u2, repeat != 0 ? frac(d * repeat) : d);
}

float2 SoftMaskUV(float2 p)
{
#if defined(SOFTMASK_SLICED) || defined(SOFTMASK_TILED)
    // Nine-slice: pick the border or centre span p falls in, on each axis.
    float2 s1 = step(_SoftMask_BorderRect.xy, p), s2 = step(_SoftMask_BorderRect.zw, p);
    float2 low = (1 - s1) * (1 - s2), mid = s1 * (1 - s2), high = s1 * s2;
    float2 a1 = _SoftMask_Rect.xy * low + _SoftMask_BorderRect.xy * mid + _SoftMask_BorderRect.zw * high;
    float2 a2 = _SoftMask_BorderRect.xy * low + _SoftMask_BorderRect.zw * mid + _SoftMask_Rect.zw * high;
    float2 u1 = _SoftMask_UVRect.xy * low + _SoftMask_UVBorderRect.xy * mid + _SoftMask_UVBorderRect.zw * high;
    float2 u2 = _SoftMask_UVBorderRect.xy * low + _SoftMask_UVBorderRect.zw * mid + _SoftMask_UVRect.zw * high;
    #if defined(SOFTMASK_TILED)
    return SoftMaskInset(p, a1, a2, u1, u2, mid * _SoftMask_TileRepeat);
    #else
    return SoftMaskInset(p, a1, a2, u1, u2);
    #endif
#else
    return SoftMaskInset(p, _SoftMask_Rect.xy, _SoftMask_Rect.zw, _SoftMask_UVRect.xy, _SoftMask_UVRect.zw);
#endif
}

// The mask's opacity at p: the weighted channels of its texture inside the rect, 0 outside.
float SoftMaskValue(float2 p)
{
#ifdef RO3_SOFTMASK_ON
    float mask = dot(tex2D(_SoftMask, SoftMaskUV(p)) * _SoftMask_ChannelWeights, 1);
    float2 inside = step(_SoftMask_Rect.xy, p) * step(p, _SoftMask_Rect.zw);
    return mask * inside.x * inside.y;
#else
    return 1;
#endif
}

#endif
