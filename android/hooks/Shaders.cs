using System.Collections.Generic;
using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// Shader.Find for the game's code (android\tools\AndroidPatch.cs redirects every call here). The PC shaders have
    /// D3D11 programs only, and many are not in the build at all; the game passes a null on
    /// (DynamicAtlasManager throws in GameMain's init, before Lua starts). The port's own build of a shader of the same
    /// name (Resources/Ro3Shaders: Unity's built-in and TextMeshPro shaders) wins; a missing shader becomes
    /// Hidden/Ro3Android/Fallback, logged once, which also lists what the port still has to provide.
    /// </summary>
    public static class Shaders
    {
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static Dictionary<string, Shader> _ours;
        private static Shader _fallback;

        public static Shader Find(string name)
        {
            if (Ours().TryGetValue(name ?? "", out var own)) return own;
            // SoftMask's variants of a shader ("Soft Mask/<name>") fall back to the port of the plain one.
            if (name != null && name.StartsWith("Soft Mask/") && Ours().TryGetValue(name.Substring("Soft Mask/".Length), out own)) return own;
            var shader = Shader.Find(name);
            // A shader this GPU cannot run would draw magenta in the material the caller builds from it: hand out the
            // shell's stand-in right away (Materials.Remap, null for shaders that run or must stay as they are).
            if (shader != null) return Materials.Remap?.Invoke(shader) ?? shader;
            if (Missing.Add(name ?? "")) Debug.LogWarning($"[Ro3Android] shader missing, using the fallback: {name}");
            if (_fallback == null) _fallback = Shader.Find("Hidden/Ro3Android/Fallback");
            return _fallback;
        }

        private static Dictionary<string, Shader> Ours()
        {
            if (_ours != null) return _ours;
            _ours = new Dictionary<string, Shader>();
            foreach (var s in Resources.LoadAll<Shader>("Ro3Shaders"))
                if (!_ours.ContainsKey(s.name)) _ours[s.name] = s;
            return _ours;
        }
    }
}
