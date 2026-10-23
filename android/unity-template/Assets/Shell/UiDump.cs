using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Diagnostics for the port's rendering work: 20, 60, 120 and 180 s after start, logs every active TextMeshPro text (content, font,
    /// material, shader and whether the GPU runs it) and every UI Graphic's shader, grouped. Reflection only: the shell
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

        private void Update()
        {
            float t = Time.realtimeSinceStartup - _start;
            if (_done < Times.Length && t > Times[_done]) { _done++; Dump(); }
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
