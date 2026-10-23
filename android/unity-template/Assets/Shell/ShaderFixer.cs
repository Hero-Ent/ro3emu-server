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
        private readonly HashSet<int> _errorUsers = new HashSet<int>();
        private Shader _fallback, _fallback3D, _invisible;
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
            var remap = Type.GetType("Ro3Android.Hooks.Materials, Ro3Android.Hooks")?.GetField("Remap");
            remap?.SetValue(null, (Func<Shader, Shader>)Replacement);
            Debug.Log($"[Ro3Android] scenery material hook {(remap != null ? "set" : "missing")}");
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

        private static readonly Type Graphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");

        // The UI graphics (and renderers) that draw with a material, as hierarchy paths.
        private static string UsersOf(Material material)
        {
            var users = new List<string>();
            if (Graphic != null)
                foreach (var o in FindObjectsOfType(Graphic))
                {
                    var m = Graphic.GetProperty("material")?.GetValue(o) as Material;
                    var mr = Graphic.GetProperty("materialForRendering")?.GetValue(o) as Material;
                    if (m != material && mr != material) continue;
                    var tex = Graphic.GetProperty("mainTexture")?.GetValue(o) as Texture;
                    users.Add($"{o.GetType().Name} {PathOf(((Component)o).transform)} tex={(tex ? tex.name : "none")}");
                }
            foreach (var r in FindObjectsOfType<Renderer>())
                if (Array.IndexOf(r.sharedMaterials, material) >= 0) users.Add($"{r.GetType().Name} {PathOf(r.transform)}");
            return users.Count == 0 ? "nothing active" : string.Join(" | ", users);
        }

        private static string PathOf(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null && path.Length < 200; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private float _lastSlowLog;

        private void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            DisablePostProcessing();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            foreach (var material in materials)
            {
                var shader = material.shader;
                if (shader == null || shader == _fallback || shader == _fallback3D || shader == _invisible) continue;
                var name = shader.name;
                bool own = _ours.TryGetValue(name, out var replacement)
                    || (name.StartsWith("Soft Mask/") && _ours.TryGetValue(name.Substring("Soft Mask/".Length), out replacement));
                if (own && shader == replacement) continue;
                if (!own && shader.isSupported && !_pcOnly.Contains(name) && name != "Hidden/InternalErrorShader") continue;
                // Blits and post effects draw a full-screen triangle with no vertex attributes: the fallback (position,
                // UV, colour) makes an invalid pipeline there, which crashes the Vulkan driver. Those stay as they are.
                bool fallbackOk = !own && !IsFullScreen(name);
                var kind = Classify(name);
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
                    Debug.Log($"[Ro3Android] error-shader material '{material.name}' tex={(material.mainTexture ? material.mainTexture.name : "none")} queue={material.renderQueue} used by {UsersOf(material)}");
                if (_reported.Add(name)) Debug.Log($"[Ro3Android] shader {name} -> {(own ? "port build" : fallbackOk ? kind.ToString() + " stand-in" : "left unsupported (full-screen)")}");
            }
            // The target portrait draws with a nameless material whose shader failed to load: find who uses such ones.
            if (Graphic != null && Time.frameCount % 2 == 0)
                foreach (var o in FindObjectsOfType(Graphic))
                {
                    var mr = Graphic.GetProperty("materialForRendering")?.GetValue(o) as Material;
                    var m = Graphic.GetProperty("material")?.GetValue(o) as Material;
                    if ((mr == null || mr.name.Length > 0) && (m == null || m.name.Length > 0)) continue;
                    if (!_errorUsers.Add(o.GetInstanceID())) continue;
                    var tex = Graphic.GetProperty("mainTexture")?.GetValue(o) as Texture;
                    Debug.Log($"[Ro3Android] nameless-material graphic {o.GetType().Name} {PathOf(((Component)o).transform)} tex={(tex ? tex.name : "none")} shader={(mr ? mr.shader.name : "?")} base={(m ? m.shader.name : "?")}");
                }
            if (watch.ElapsedMilliseconds > 15 && Time.realtimeSinceStartup - _lastSlowLog > 10)
            {
                _lastSlowLog = Time.realtimeSinceStartup;
                Debug.Log($"[Ro3Android] shader scan {watch.ElapsedMilliseconds} ms over {materials.Length} materials");
            }
        }
    }
}
