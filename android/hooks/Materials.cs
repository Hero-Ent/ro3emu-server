using System;
using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// new Material(shader) for the scenery code (android\tools\AndroidPatch.cs redirects HappyEngine.CustomScene's calls
    /// here). It builds its materials from the PC shaders and then sets their textures; on a shader with no programs for
    /// this GPU those SetTexture calls are lost, so the port's stand-in has to be in place before them. Remap is set by
    /// the shell (Ro3Android.ShaderFixer), which knows the stand-ins.
    /// </summary>
    public static class Materials
    {
        public static Func<Shader, Shader> Remap;

        public static Material Create(Shader shader)
        {
            var replacement = Remap?.Invoke(shader);
            return new Material(replacement != null ? replacement : shader);
        }

        // new Material(material): the terrain copies its asset's material, then sets the textures on the copy.
        public static Material Copy(Material source)
        {
            var copy = new Material(source);
            var replacement = Remap?.Invoke(copy.shader);
            if (replacement != null) copy.shader = replacement;
            return copy;
        }
    }
}
