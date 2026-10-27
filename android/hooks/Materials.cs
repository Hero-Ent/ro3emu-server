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

        /// <summary>The shell's per-material fix (ShaderFixer): the stand-in or the port for a shader this GPU cannot run.</summary>
        public static Action<Material> Fix;

        private static readonly Type Graphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
        private static readonly System.Reflection.PropertyInfo GraphicMaterial = Graphic?.GetProperty("material");

        /// <summary>
        /// Every asset the game's resource loader hands out (android\tools\AndroidPatch.cs calls this at the start of
        /// HappyEngine's AssetElement.DoLoadCompleted, before the callbacks): a material, or a prefab's renderers and UI
        /// graphics, gets its shader fixed before anything is drawn with it. Without this the once-a-second scan let new
        /// effects and widgets show magenta for a moment.
        /// </summary>
        public static void FixLoaded(object asset)
        {
            if (Fix == null || asset == null) return;
            try
            {
                if (asset is Material material) { Fix(material); return; }
                if (!(asset is GameObject prefab)) return;
                Clicks.EnableDeadClickTargets(prefab);
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in renderer.sharedMaterials) Fix(m);
                if (Graphic == null || GraphicMaterial == null) return;
                foreach (var graphic in prefab.GetComponentsInChildren(Graphic, true))
                    Fix(GraphicMaterial.GetValue(graphic) as Material);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ro3Android] material fix on load failed: {e.Message}");
            }
        }

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
