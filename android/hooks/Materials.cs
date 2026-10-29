using System;
using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// new Material(shader) for the game's code (android\tools\AndroidPatch.cs redirects every game assembly's calls
    /// here). The scenery builds its materials from the PC shaders and then sets their textures; on a shader with no programs for
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
                FixObject(prefab);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ro3Android] material fix on load failed: {e.Message}");
            }
        }

        /// <summary>
        /// What Object.Instantiate returned in the game's code (android\tools\AndroidPatch.cs adds the call after each
        /// one). Every effect reaches the screen through it, whatever loaded its prefab: the UI effects (level up, the
        /// city name, the skill bar) come by paths that skip DoLoadCompleted, and only the once-a-second scan fixed
        /// their materials, after their first frames were drawn magenta.
        /// </summary>
        public static void FixInstance(object instance)
        {
            if (Fix == null) return;
            try
            {
                if (instance is Material material) Fix(material);
                else if (instance is GameObject go) FixObject(go);
                else if (instance is Component component && component != null) FixObject(component.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ro3Android] material fix on instantiate failed: {e.Message}");
            }
        }

        private static void FixObject(GameObject root)
        {
            if (root == null) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in renderer.sharedMaterials) Fix(m);
            if (Graphic == null || GraphicMaterial == null) return;
            foreach (var graphic in root.GetComponentsInChildren(Graphic, true))
                Fix(GraphicMaterial.GetValue(graphic) as Material);
        }

        public static Material Create(Shader shader)
        {
            var replacement = Remap?.Invoke(shader);
            if (replacement != null) Report(shader);
            return new Material(replacement != null ? replacement : shader);
        }

        // new Material(material): the terrain copies its asset's material, then sets the textures on the copy.
        public static Material Copy(Material source)
        {
            var copy = new Material(source);
            var replacement = Remap?.Invoke(copy.shader);
            if (replacement != null)
            {
                Report(copy.shader);
                copy.shader = replacement;
            }
            return copy;
        }

        private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();

        // Which shaders the game's code builds materials from at run time: each one is a material the scan may never see.
        private static void Report(Shader shader)
        {
            var name = shader ? shader.name : "null";
            if (Reported.Add(name)) Debug.Log($"[Ro3Android] new Material with shader {name}: replaced");
        }
    }
}
