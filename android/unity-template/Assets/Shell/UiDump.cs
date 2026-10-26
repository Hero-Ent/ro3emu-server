using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Diagnostics for the port's rendering work: 20, 60, 120 and 180 s after start, logs every active TextMeshPro text (content, font,
    /// material, shader and whether the GPU runs it) and every UI Graphic's shader, grouped; and, on demand (a "ui-dump"
    /// file in the app's folder), every graphic on screen one by one. Reflection only: the shell
    /// does not compile against the game's DLLs.
    /// </summary>
    public class UiDump : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3UiDump");
            DontDestroyOnLoad(go);
            go.AddComponent<UiDump>();
        }

        // Login screen, then in the world (the flow scripts get there after about 100 s).
        private static readonly float[] Times = { 20, 60, 120, 180 };
        private float _start;
        private int _done;

        private void Start() => _start = Time.realtimeSinceStartup;

        // On demand, for a window that draws wrong: `adb shell touch /sdcard/Android/data/com.ro3emu.client/files/ui-dump`
        // with the window open logs every graphic on screen ("[Ro3Android] ui graphic"), then the file is removed.
        private float _nextTrigger;

        private void Update()
        {
            float t = Time.realtimeSinceStartup - _start;
            if (_done < Times.Length && t > Times[_done]) { _done++; Dump(); }
            if (Time.realtimeSinceStartup < _nextTrigger) return;
            _nextTrigger = Time.realtimeSinceStartup + 2;
            var trigger = System.IO.Path.Combine(Application.persistentDataPath, "ui-dump");
            if (!System.IO.File.Exists(trigger)) return;
            try { System.IO.File.Delete(trigger); } catch (Exception) { }
            DumpGraphics();
        }

        // Every active UI graphic: where it is, what it draws with (shader, material, texture, keywords), whether it is
        // visible (alpha, culled, size) and whether it takes touches - what an empty, misplaced or unclickable window needs.
        private static void DumpGraphics()
        {
            var graphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
            if (graphic == null) return;
            var forRendering = graphic.GetProperty("materialForRendering");
            var mainTexture = graphic.GetProperty("mainTexture");
            var raycast = graphic.GetProperty("raycastTarget");
            var color = graphic.GetProperty("color");
            var all = FindObjectsOfType(graphic).Cast<Component>().ToArray();
            Debug.Log($"[Ro3Android] ui graphics: {all.Length} active, screen {Screen.width}x{Screen.height}");
            foreach (var g in all)
            {
                var m = forRendering.GetValue(g) as Material;
                var tex = mainTexture.GetValue(g) as Texture;
                var cr = g.GetComponent<CanvasRenderer>();
                var rect = ((RectTransform)g.transform).rect;
                var game = !m ? "-"
                    : ShaderFixer.OriginalShader.TryGetValue(m.GetInstanceID(), out var original) ? original
                    : ShaderFixer.OriginalShaderByName.TryGetValue(m.name.Replace(" (Instance)", ""), out original) ? original + " (by name)" : "-";
                string F(string p) => m && m.HasProperty(p) ? m.GetFloat(p).ToString("0") : "-";
                Debug.Log($"[Ro3Android] ui graphic {g.GetType().Name} '{FullPath(g.transform)}' size={rect.width:0}x{rect.height:0} "
                    + $"color={color.GetValue(g)} alpha={(cr ? cr.GetInheritedAlpha() : -1):0.##} culled={(cr && cr.cull)} ray={raycast.GetValue(g)} "
                    + $"rectClip={(cr && cr.hasRectClipping)} stencil={F("_Stencil")}/{F("_StencilComp")}/{F("_StencilOp")}/{F("_ColorMask")} masks=[{Masks(g.transform)}] "
                    + $"shader={(m && m.shader ? m.shader.name : "none")} game={game} queue={(m ? m.renderQueue : 0)} mat='{(m ? m.name : "none")}' kw=[{(m ? string.Join(",", m.shaderKeywords) : "")}] "
                    + $"tex={(tex ? $"{tex.name} {tex.width}x{tex.height}{(tex is RenderTexture rt ? " " + rt.graphicsFormat : "")}" : "none")}"
                    + (g.GetType().GetProperty("uvRect")?.GetValue(g) is Rect uv ? $" uvRect={uv}" : ""));
            }
            DumpCameras();
        }

        private static readonly Type CameraData = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");

        // Every camera in the loaded scenes, enabled or not: the game renders its model previews (the bag's character) with
        // disabled cameras into render textures.
        private static void DumpCameras()
        {
            foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!cam.gameObject.scene.IsValid()) continue;
                var data = CameraData != null ? cam.GetComponent(CameraData) : null;
                string P(string n) => data != null ? CameraData.GetProperty(n)?.GetValue(data)?.ToString() ?? "-" : "-";
                var rt = cam.targetTexture;
                Debug.Log($"[Ro3Android] ui camera '{FullPath(cam.transform)}' enabled={cam.enabled} active={cam.gameObject.activeInHierarchy} "
                    + $"rt={(rt ? $"{rt.name} {rt.graphicsFormat}" : "screen")} clear={cam.clearFlags} bg={cam.backgroundColor} mask=0x{cam.cullingMask:X} "
                    + $"hdr={cam.allowHDR} type={P("renderType")} post={P("renderPostProcessing")} depthTex={P("requiresDepthTexture")} colorTex={P("requiresColorTexture")}");
                if (rt && cam.enabled && cam.gameObject.activeInHierarchy) DumpRenderersSeenBy(cam);
            }
        }

        // What a render-texture camera draws (the bag's character preview): each renderer on its layers, with the game's
        // shader it replaced - a backdrop or a shadow catcher drawn by the opaque stand-in shows as a solid box.
        private static void DumpRenderersSeenBy(Camera cam)
        {
            LogPixels(cam.targetTexture);
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Debug.Log($"[Ro3Android] ui camera-at {cam.transform.position} forward={cam.transform.forward} fov={cam.fieldOfView} ortho={cam.orthographic} near={cam.nearClipPlane} far={cam.farClipPlane} rect={cam.rect} pixelRect={cam.pixelRect}");
            LogRendererFeatures(cam);
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0 || !r.enabled) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, r.bounds)) continue;
                var m = r.sharedMaterial;
                var game = m && ShaderFixer.OriginalShaderByName.TryGetValue(m.name.Replace(" (Instance)", ""), out var original) ? original : "-";
                Debug.Log($"[Ro3Android] ui camera-sees {r.GetType().Name} '{FullPath(r.transform)}' layer={r.gameObject.layer} "
                    + $"shader={(m && m.shader ? m.shader.name : "none")} game={game} mat='{(m ? m.name : "none")}' queue={(m ? m.renderQueue : 0)} "
                    + $"tex={(m && m.mainTexture ? m.mainTexture.name : "none")} center={r.bounds.center} bounds={r.bounds.size} "
                    + $"scale={r.transform.lossyScale} root={ModelRoot(r.transform)}");
            }
        }

        // The model's top node under the display node (ModelDisplayNode/ModelNode/<model>): the scale the game's Lua gave it
        // (LC_UIModelManager: the export config's scale x the UI scale).
        private static string ModelRoot(Transform t)
        {
            while (t.parent != null && t.parent.name != "ModelNode") t = t.parent;
            return $"'{t.name}' localScale={t.localScale} position={t.position}";
        }

        // The passes the camera's URP renderer adds (the game's renderer features), active or not.
        private static void LogRendererFeatures(Camera cam)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var data = CameraData != null ? cam.GetComponent(CameraData) : null;
            var renderer = data != null ? CameraData.GetProperty("scriptableRenderer", any)?.GetValue(data) : null;
            if (renderer == null) { Debug.Log("[Ro3Android] ui camera-features: no renderer"); return; }
            var list = renderer.GetType().GetProperty("rendererFeatures", any)?.GetValue(renderer) as System.Collections.IList;
            var sb = new StringBuilder($"[Ro3Android] ui camera-features {renderer.GetType().Name}:");
            if (list != null)
                foreach (var f in list)
                    if (f is ScriptableObject so) sb.Append($" {so.GetType().Name}('{so.name}' active={f.GetType().GetProperty("isActive")?.GetValue(f)})");
            Debug.Log(sb.ToString());
        }

        // What the render texture holds (the grey box: drawn into it, or added when it is shown?): its corners and
        // centre, alpha included.
        private static void LogPixels(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
            try
            {
                var sb = new StringBuilder($"[Ro3Android] ui camera-pixels {rt.name}:");
                var points = new System.Collections.Generic.List<(float, float)> { (0.02f, 0.02f), (0.98f, 0.98f) };
                for (int i = 0; i < 10; i++) points.Add((0.05f + i * 0.1f, 0.5f));
                for (int i = 0; i < 10; i++) points.Add((0.35f, 0.05f + i * 0.1f));
                foreach (var (x, y) in points)
                {
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect((int)(x * (rt.width - 1)), (int)(y * (rt.height - 1)), 1, 1), 0, 0, false);
                    sb.Append($" ({x:0.##},{y:0.##})={read.GetPixel(0, 0)}");
                }
                Debug.Log(sb.ToString());
            }
            catch (Exception e)
            {
                Debug.Log($"[Ro3Android] ui camera-pixels failed: {e.Message}");
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(read);
            }
        }

        // The masking components on the graphic and its parents (Mask, RectMask2D, SoftMask...), nearest first.
        private static string Masks(Transform t)
        {
            var found = new System.Collections.Generic.List<string>();
            for (var p = t; p != null; p = p.parent)
                foreach (var c in p.GetComponents<Component>())
                    if (c != null && c.GetType().Name.Contains("Mask")) found.Add($"{c.GetType().Name}@{p.name}{(c is Behaviour b && !b.enabled ? "(off)" : "")}");
            return string.Join(",", found);
        }

        private static string FullPath(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null && path.Length < 300; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private static void Dump()
        {
            // The opaque stand-in's materials (scenery, characters): which texture each one carries into the shader.
            var stand = Resources.FindObjectsOfTypeAll<Material>().Where(m => m && m.shader && m.shader.name == "Hidden/Ro3Android/Fallback3D").ToArray();
            // One line each (logcat cuts long messages); characters (Model_*) are fine, the scenery is what is checked.
            Debug.Log($"[Ro3Android] 3D dump: {stand.Length} stand-in materials");
            foreach (var m in stand.Where(m => !m.name.StartsWith("Model_")).GroupBy(m => m.name).Select(g => g.First()).Take(40))
            {
                string Tex(string n)
                {
                    var t = m.GetTexture(n);
                    return t ? $"{n}={t.name} {(t is Texture2D t2 ? t2.format.ToString() : t.GetType().Name)} {t.width}x{t.height}" : null;
                }
                var texs = m.GetTexturePropertyNames().Select(Tex).Where(s => s != null).Take(6);
                Debug.Log($"[Ro3Android] 3D mat {m.name} queue={m.renderQueue} color={m.GetColor("_Color")} kw=[{string.Join(",", m.shaderKeywords)}] tex=[{string.Join("; ", texs)}]");
            }

            var tmp = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            var graphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
            if (tmp != null)
            {
                var texts = FindObjectsOfType(tmp);
                var sb = new StringBuilder($"[Ro3Android] UI dump: {texts.Length} active TMP texts\n");
                foreach (var o in texts.Take(40))
                {
                    var text = tmp.GetProperty("text")?.GetValue(o) as string;
                    var font = tmp.GetProperty("font")?.GetValue(o) as UnityEngine.Object;
                    var mat = tmp.GetProperty("fontSharedMaterial")?.GetValue(o) as Material;
                    var color = tmp.GetProperty("color")?.GetValue(o);
                    var shader = mat != null ? mat.shader : null;
                    sb.AppendLine($"  '{Trim(text)}' font={(font ? font.name : "null")} mat={(mat ? mat.name : "null")} shader={(shader ? shader.name : "null")} supported={(shader ? shader.isSupported : false)} color={color} path={Path(((Component)o).transform)}");
                    // Why a text with content and a supported shader draws nothing: its atlas, its mesh, its material.
                    var atlas = mat && mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") as Texture2D : null;
                    var info = tmp.GetProperty("textInfo")?.GetValue(o);
                    var chars = info?.GetType().GetField("characterCount")?.GetValue(info);
                    var mesh = tmp.GetProperty("mesh")?.GetValue(o) as Mesh;
                    string P(string n) => mat && mat.HasProperty(n) ? mat.GetFloat(n).ToString("0.###") : "-";
                    sb.AppendLine($"    atlas={(atlas ? $"{atlas.name} {atlas.format} {atlas.width}x{atlas.height}" : "null")} chars={chars} meshVerts={(mesh ? mesh.vertexCount : -1)} "
                        + $"face={(mat && mat.HasProperty("_FaceColor") ? mat.GetColor("_FaceColor").ToString() : "-")} gradScale={P("_GradientScale")} ratioA={P("_ScaleRatioA")} "
                        + $"texW={P("_TextureWidth")} dilate={P("_FaceDilate")} weight={P("_WeightNormal")} stencilComp={P("_StencilComp")} "
                        + $"keywords=[{(mat ? string.Join(",", mat.shaderKeywords) : "")}] canvasAlpha={((Component)o).GetComponent<CanvasRenderer>()?.GetAlpha()}");
                }
                Debug.Log(sb.ToString());
            }
            // The 3D side: renderers by material shader (all / enabled), and where the cameras look from.
            var renderers = FindObjectsOfType<Renderer>();
            var byShader = renderers.GroupBy(r => r.sharedMaterial && r.sharedMaterial.shader ? r.sharedMaterial.shader.name : "no material")
                .OrderByDescending(g => g.Count()).Take(30)
                .Select(g => $"  {g.Count(),4} ({g.Count(r => r.enabled && r.gameObject.activeInHierarchy)} on) x {g.Key} e.g. {g.First().GetType().Name} '{g.First().name}' at {g.First().bounds.center}");
            Debug.Log($"[Ro3Android] scene dump: {renderers.Length} renderers\n" + string.Join("\n", byShader));
            foreach (var cam in Camera.allCameras)
                Debug.Log($"[Ro3Android] camera '{cam.name}' pos={cam.transform.position} depth={cam.depth} mask=0x{cam.cullingMask:X} far={cam.farClipPlane} clear={cam.clearFlags} target={(cam.targetTexture ? cam.targetTexture.name : "screen")}");
            if (graphic != null)
            {
                var groups = FindObjectsOfType(graphic).Cast<Component>()
                    .GroupBy(g => { var m = graphic.GetProperty("materialForRendering")?.GetValue(g) as Material; return m && m.shader ? $"{m.shader.name} supported={m.shader.isSupported}" : "no material"; })
                    .Select(g => $"  {g.Count(),4} x {g.Key} ({g.First().GetType().Name})");
                Debug.Log("[Ro3Android] UI dump: graphics by shader\n" + string.Join("\n", groups));
            }
        }

        private static string Trim(string s) => s == null ? "null" : s.Length > 40 ? s.Substring(0, 40) + "..." : s.Replace("\n", " ");

        private static string Path(Transform t)
        {
            var p = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
