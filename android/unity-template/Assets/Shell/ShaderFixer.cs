using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Gives the materials the game loads from its bundles a shader Android can run. Their shaders have D3D11 programs
    /// only (drawn magenta); once a second every loaded material whose shader is unsupported switches to the port's
    /// build of the same name (Resources/Ro3Shaders) or, without one, Hidden/Ro3Android/Fallback. Each shader name is
    /// logged once, so the log lists what is still to port.
    /// </summary>
    public class ShaderFixer : MonoBehaviour
    {
        private readonly Dictionary<string, Shader> _ours = new Dictionary<string, Shader>();
        // The game's shaders that only have D3D11 programs (Resources/Ro3D3DShaders.txt, written by setup-project.ps1
        // from the AssetRipper exports). Many still report isSupported (their blob simply lacks Vulkan programs, and
        // they draw nothing), so the name, not isSupported, decides.
        private readonly HashSet<string> _pcOnly = new HashSet<string>();
        private readonly HashSet<string> _reported = new HashSet<string>();
        private readonly HashSet<string> _errorMaterials = new HashSet<string>();
        private readonly HashSet<string> _lateReported = new HashSet<string>();
        private readonly Dictionary<int, Shader> _fine = new Dictionary<int, Shader>();
        private int _scans;
        private Shader _fallback, _fallback3D, _invisible;

        /// <summary>The game's shader of each material this fixer swapped (by instance id), for UiDump's diagnostics.</summary>
        internal static readonly Dictionary<int, string> OriginalShader = new Dictionary<int, string>();

        /// <summary>The same by material name: what a graphic draws with is often a copy (masking, clipping, instancing).</summary>
        internal static readonly Dictionary<string, string> OriginalShaderByName = new Dictionary<string, string>();
        private float _next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3ShaderFixer");
            DontDestroyOnLoad(go);
            go.AddComponent<ShaderFixer>();
        }

        private void Awake()
        {
            foreach (var s in Resources.LoadAll<Shader>("Ro3Shaders"))
                if (!_ours.ContainsKey(s.name)) _ours[s.name] = s;
            _fallback = Shader.Find("Hidden/Ro3Android/Fallback");
            _fallback3D = Shader.Find("Hidden/Ro3Android/Fallback3D");
            _invisible = Shader.Find("Hidden/Ro3Android/Invisible");
            var list = Resources.Load<TextAsset>("Ro3D3DShaders");
            if (list != null)
                foreach (var line in list.text.Split('\n'))
                    if (line.Trim().Length > 0) _pcOnly.Add(line.Trim());
            Debug.Log($"[Ro3Android] {_ours.Count} replacement shaders available, {_pcOnly.Count} PC-only shader names");
            // The scenery's materials are built from PC shaders and textured right away: hand them the stand-in first.
            var hooks = Type.GetType("Ro3Android.Hooks.Materials, Ro3Android.Hooks");
            var remap = hooks?.GetField("Remap");
            remap?.SetValue(null, (Func<Shader, Shader>)Replacement);
            // ...and every material or prefab the game's resource loader hands out, before its first frame.
            var fix = hooks?.GetField("Fix");
            fix?.SetValue(null, (Action<Material>)Fix);
            Debug.Log($"[Ro3Android] material hooks: remap {(remap != null ? "set" : "missing")}, fix {(fix != null ? "set" : "missing")}");
        }

        // The shader a new material should get instead of the game's (null: keep the game's).
        private Shader Replacement(Shader shader)
        {
            if (shader == null) return null;
            var name = shader.name;
            if (_ours.TryGetValue(name, out var own)) return own;
            if (shader.isSupported && !_pcOnly.Contains(name) && name != "Hidden/InternalErrorShader") return null;
            if (IsFullScreen(name)) return null;
            var kind = Classify(name);
            return kind == Kind.Invisible ? _invisible : kind == Kind.Opaque ? _fallback3D : _fallback;
        }

        // Materials whose shader failed to load get Unity's error shader (drawn magenta): they get a stand-in like any
        // other. URP's particle and Autodesk shaders draw meshes (skill effects, props), not full-screen passes.
        internal static bool IsFullScreen(string name) =>
            name != "Hidden/InternalErrorShader"
            && ((name.StartsWith("Hidden/") && !name.Contains("UI")) || name.StartsWith("PostFX/")
                || (name.StartsWith("Universal Render Pipeline/") && !name.Contains("/Particles/") && !name.Contains("/Autodesk Interactive/"))
                || name.StartsWith("Jaihk662/OSP/") || name.StartsWith("ROMeta/URP/"));

        // An unsupported shader reports the default queue, not its own, so the stand-in is chosen by name: outlines,
        // shadow blobs and depth outputs draw nothing; scenery, characters and terrain get the lit opaque stand-in;
        // everything else (UI, HUD, Spine, effects) the UI-like one.
        private enum Kind { Invisible, Opaque, Ui }

        private static Kind Classify(string name)
        {
            // HUD name tags (Happyengine/HUD/*) read a texture array with their own vertex layout: hidden until ported,
            // rather than drawn as white blocks.
            if (name.Contains("Outline") || name.Contains("Shadow") || name.Contains("LightDepth") || name.Contains("/HUD/")) return Kind.Invisible;
            if (name.StartsWith("Base/") || name.StartsWith("RO/Model") || name.StartsWith("ROMeta/3DCharacter") || name.StartsWith("RoMeta/Scene")
                || name.StartsWith("Shader Graphs/") || name.StartsWith("GPUDrivenTerrain") || name.StartsWith("GPUSkinning")
                || name.StartsWith("HappyEngine/DOTSAnimation") || name.StartsWith("Custom/Water") || name.Contains("/Autodesk Interactive/"))
                return Kind.Opaque;
            return Kind.Ui;
        }

        // The game's post stack (PostFX/PostUber, BloomPost, Fxaa...) has no Vulkan programs and wipes the whole camera
        // image (the world drew black behind the UI): post-processing stays off on every camera until it is ported.
        private static readonly Type CameraData = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        private static readonly System.Reflection.PropertyInfo RenderPost = CameraData?.GetProperty("renderPostProcessing");

        private static void DisablePostProcessing()
        {
            if (RenderPost == null) return;
            foreach (var cam in Camera.allCameras)
            {
                var data = cam.GetComponent(CameraData);
                if (data != null && (bool)RenderPost.GetValue(data)) RenderPost.SetValue(data, false);
            }
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            DisablePostProcessing();
            _scans++;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            foreach (var material in materials) Fix(material, late: true);
            if (watch.ElapsedMilliseconds > 10)
            {
                Debug.Log($"[Ro3Android] shader scan {watch.ElapsedMilliseconds} ms over {materials.Length} materials");
            }
        }

        // Gives one material a shader this GPU runs. Called by the once-a-second scan (scene objects, anything missed) and,
        // through Ro3Android.Hooks.Materials.Fix, as soon as the game's resource loader hands out a material or a prefab:
        // before its first frame, which an unsupported shader would draw magenta. late: found by the scan, so it may
        // have been on screen with the game's shader for up to a second (an effect's first frames drawn magenta).
        private void Fix(Material material) => Fix(material, late: false);

        private void Fix(Material material, bool late)
        {
            if (material == null) return;
            var shader = material.shader;
            if (shader == null || shader == _fallback || shader == _fallback3D || shader == _invisible) return;
            // Every Instantiate passes its copy's materials here: one already found fine is not looked at again (the
            // scan still checks them all, in case the game swapped a shader since).
            int id = material.GetInstanceID();
            if (!late && _fine.TryGetValue(id, out var fineShader) && fineShader == shader) return;
            var name = shader.name;
            bool own = _ours.TryGetValue(name, out var replacement)
                || (name.StartsWith("Soft Mask/") && _ours.TryGetValue(name.Substring("Soft Mask/".Length), out replacement));
            if ((own && shader == replacement) || (!own && shader.isSupported && !_pcOnly.Contains(name) && name != "Hidden/InternalErrorShader"))
            {
                _fine[id] = shader;
                return;
            }
            // Blits and post effects draw a full-screen triangle with no vertex attributes: the fallback (position,
            // UV, colour) makes an invalid pipeline there, which crashes the Vulkan driver. Those stay as they are.
            bool fallbackOk = !own && !IsFullScreen(name);
            var kind = Classify(name);
            OriginalShader[material.GetInstanceID()] = name;
            OriginalShaderByName[material.name.Replace(" (Instance)", "")] = name;
            if (own) material.shader = replacement;
            else if (fallbackOk)
            {
                int queue = material.renderQueue;
                material.shader = kind == Kind.Invisible ? _invisible : kind == Kind.Opaque ? _fallback3D : _fallback;
                if (kind == Kind.Ui && queue < 2450) queue = 3000;     // the default an unsupported shader reports
                material.renderQueue = queue;
            }
            // Which assets lost their shader (drawn magenta, then as a plain quad): the next port to make.
            if (name == "Hidden/InternalErrorShader" && _errorMaterials.Add(material.GetInstanceID().ToString()) && _errorMaterials.Count <= 20)
                Debug.Log($"[Ro3Android] error-shader material '{material.name}' tex={(material.mainTexture ? material.mainTexture.name : "none")} queue={material.renderQueue}");
            // What escaped the loader hooks: each line names a material the player may have seen magenta. The first scan
            // covers what loaded before the hooks were set, so it is left out.
            if (late && _scans > 1 && _lateReported.Add(material.name + "|" + name))
                Debug.Log($"[Ro3Android] late shader fix: material '{material.name}' shader {name} tex={(material.mainTexture ? material.mainTexture.name : "none")} -> {(own ? "port build" : fallbackOk ? kind + " stand-in" : "left unsupported")}");
            if (_reported.Add(name)) Debug.Log($"[Ro3Android] shader {name} -> {(own ? "port build" : fallbackOk ? kind.ToString() + " stand-in" : "left unsupported (full-screen)")}");
        }
    }
}
