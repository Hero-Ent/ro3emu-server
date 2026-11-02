using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Server-triggered client inspection (game message 77012, UTF-8 command line; GM "!client ..."), written to
    /// Player.log. Runs on the main thread from the chat adapter's pump.
    /// </summary>
    public static class Diagnostics
    {
        public const uint MsgId = 77012;

        public static void Run(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            try
            {
                switch (parts[0])
                {
                    case "find":
                        Find(parts.Length > 1 ? parts[1] : "weapon");
                        break;
                    case "prefab":
                    case "prefabs":
                        Prefabs(parts.Length > 1 ? parts[1] : "Login");
                        break;
                    case "alltex":
                        AllTextures(parts.Length > 1 ? parts[1] : "");
                        break;
                    case "dumpalltex":
                        DumpAllTextures(parts.Length > 1 ? parts[1] : "");
                        break;
                    case "panels":
                        Panels();
                        break;
                    case "terrain":
                        Terrain();
                        break;
                    case "dumptex":
                        DumpTextures(parts.Length > 1 ? parts[1] : "Txt");
                        break;
                    case "sprites":
                        Sprites(parts.Length > 1 ? parts[1] : "icon");
                        break;
                    case "spritepng":
                        foreach (var name in (parts.Length > 1 ? parts[1] : "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            SpritePng(name);
                        break;
                    default:
                        Debug.Log("[Ro3Emu.Diag] unknown command " + line);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Diag] " + line + " failed: " + e);
            }
        }

        /// <summary>Every transform whose name contains <paramref name="filter"/>: path, state, renderers.</summary>
        private static void Find(string filter)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in all)
            {
                if (t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.gameObject.scene.name == null) continue; // prefab assets, not scene objects
                if (++hits > 60) break;
                sb.Append("\n  ").Append(Path(t))
                  .Append(" active=").Append(t.gameObject.activeInHierarchy)
                  .Append(" layer=").Append(t.gameObject.layer)
                  .Append(" pos=").Append(t.position.ToString("F2"))
                  .Append(" scale=").Append(t.lossyScale.ToString("F3"));
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    sb.Append("\n      renderer ").Append(r.GetType().Name).Append(' ').Append(r.name)
                      .Append(" enabled=").Append(r.enabled)
                      .Append(" visible=").Append(r.isVisible)
                      .Append(" layer=").Append(r.gameObject.layer)
                      .Append(" bounds=").Append(r.bounds.size.ToString("F2"))
                      .Append(" mat=").Append(r.sharedMaterial != null ? r.sharedMaterial.name + "/" + (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-") : "null");
                }
            }
            Debug.Log("[Ro3Emu.Diag] find '" + filter + "': " + hits + " hit(s)" + sb);
        }

        /// <summary>Prefabs and loaded GameObjects matching <paramref name="filter"/>: components and images.</summary>
        private static void Prefabs(string filter)
        {
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var go in all)
            {
                if (go.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++hits > 60) break;
                sb.Append("\n  go ").Append(go.name).Append(" (scene=").Append(go.scene.name ?? "asset").Append(')');
                foreach (var r in go.GetComponentsInChildren<RawImage>(true))
                    sb.Append("\n      raw ").Append(r.name).Append(" tex=").Append(r.texture != null ? r.texture.name + " (" + r.texture.width + "x" + r.texture.height + ")" : "null");
                foreach (var img in go.GetComponentsInChildren<Image>(true))
                    sb.Append("\n      img ").Append(img.name).Append(" sprite=").Append(img.sprite != null ? img.sprite.name : "null").Append(" tex=").Append(img.sprite != null && img.sprite.texture != null ? img.sprite.texture.name : "null");
            }
            Debug.Log("[Ro3Emu.Diag] prefabs '" + filter + "': " + hits + sb);
        }

        /// <summary>All loaded textures matching <paramref name="filter"/>.</summary>
        private static void AllTextures(string filter)
        {
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (tex == null) continue;
                if (!string.IsNullOrEmpty(filter) && tex.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++hits > 200) break;
                sb.Append("\n  tex ").Append(tex.name).Append(" (").Append(tex.width).Append('x').Append(tex.height).Append(") ").Append(tex.GetType().Name);
            }
            Debug.Log("[Ro3Emu.Diag] alltex '" + filter + "': " + hits + sb);
        }

        /// <summary>Dumps loaded Texture2D objects matching <paramref name="filter"/> to ro3emu/dump/.</summary>
        private static void DumpAllTextures(string filter)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu", "dump");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (tex == null) continue;
                if (!string.IsNullOrEmpty(filter) && tex.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++hits > 60) break;
                string safe = tex.name;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                string file = System.IO.Path.Combine(dir, (safe.Length > 0 ? safe : "tex" + hits) + ".png");
                try { SavePng(tex, file); }
                catch (Exception e) { file = "save failed: " + e.Message; }
                sb.Append("\n  ").Append(tex.name).Append(" (").Append(tex.width).Append('x').Append(tex.height).Append(") -> ").Append(file);
            }
            Debug.Log("[Ro3Emu.Diag] dumpalltex '" + filter + "': " + hits + sb);
        }

        /// <summary>Open UI views: active objects named UI_Sys_* / UI_Com_* (the view prefabs' roots), outermost only.</summary>
        /// <summary>
        /// The streamed terrain's collider baker (WorldStreamFacade._instance.World.TerrainColliderManager): its bake mode,
        /// compute shader, height map, load/unload counts and slot states, read by reflection.
        /// </summary>
        private static void Terrain()
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object Get(object o, string name)
            {
                if (o == null) return null;
                for (var t = o as Type ?? o.GetType(); t != null; t = t.BaseType)
                {
                    var target = o is Type ? null : o;
                    var f = t.GetField(name, all) ?? t.GetField("<" + name + ">k__BackingField", all);
                    if (f != null) return f.GetValue(target);
                    var p = t.GetProperty(name, all);
                    if (p != null) return p.GetValue(target);
                }
                return null;
            }
            Type facade = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                facade = facade ?? assembly.GetType("HappyEngine.WorldStream.WorldStreamTool.WorldStreamFacade");
            var manager = Get(Get(Get(facade, "_instance"), "World"), "TerrainColliderManager");
            var baker = Get(manager, "_colliderBaker");
            var context = Get(manager, "_context");
            var executor = Get(manager, "_commandExecutor");
            var slots = Get(baker, "_slotManager");
            var sb = new StringBuilder("[Ro3Emu.Diag] terrain: manager=" + (manager != null));
            sb.Append(" mode=").Append(Get(baker, "_bakeMode"));
            sb.Append(" strategy=").Append(Get(baker, "_bakeStrategy")?.GetType().Name);
            sb.Append(" computeShader=").Append(Get(context, "ComputeShader") is UnityEngine.Object cs && cs != null ? cs.name : "null");
            sb.Append(" heightMap=").Append(Get(context, "HeightMapWidth")).Append('x').Append(Get(context, "HeightMapHeight"));
            sb.Append(" emptyHeightData=").Append(Get(baker, "_heightmapDataViewer")?.GetType().GetMethod("IsEmptyData", all)?.Invoke(Get(baker, "_heightmapDataViewer"), null));
            sb.Append(" terrainSize=").Append(Get(context, "TerrainSize")).Append(" terrainHeight=").Append(Get(context, "TerrainHeight"));
            sb.Append(" patch=").Append(Get(context, "PatchSize")).Append(" gridPixels=").Append(Get(context, "GridPixelSizeX")).Append('x').Append(Get(context, "GridPixelSizeZ"));
            var stat = Get(executor, "_loadCommandStat");
            sb.Append(" commands total=").Append(Get(stat, "totalCount")).Append(" load=").Append(Get(stat, "loadCount")).Append(" unload=").Append(Get(stat, "unloadCount"));
            sb.Append(" activeSlots=").Append(Get(Get(slots, "_activeSlotIndices"), "_activeCount"));
            if (Get(slots, "_bakeSlots") is Array bakeSlots)
            {
                var states = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var slot in bakeSlots)
                {
                    string state = Get(slot, "bakeState")?.ToString() ?? "?";
                    states[state] = states.TryGetValue(state, out int n) ? n + 1 : 1;
                }
                foreach (var pair in states) sb.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            }
            if (Get(Get(manager, "_colliderBaker"), "_lifecycleManager") is object life && Get(life, "_loadedColliders") is GameObject[] loaded)
            {
                int live = 0, withMesh = 0, vertices = 0;
                foreach (var go in loaded)
                {
                    if (go == null) continue;
                    live++;
                    var mesh = Get(go.GetComponent("MeshCollider"), "sharedMesh") as Mesh; // PhysicsModule is not referenced
                    if (mesh == null) continue;
                    withMesh++;
                    vertices = mesh.vertexCount;
                }
                sb.Append(" colliders=").Append(live).Append(" withMesh=").Append(withMesh).Append(" lastVertices=").Append(vertices);
            }
            Debug.Log(sb.ToString());
        }

        private static void Panels()
        {
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy) continue;
                if (!t.name.StartsWith("UI_Sys_", StringComparison.Ordinal) && !t.name.StartsWith("UI_Com_", StringComparison.Ordinal)) continue;
                bool nested = false;
                for (var p = t.parent; p != null && !nested; p = p.parent)
                    nested = p.name.StartsWith("UI_Sys_", StringComparison.Ordinal) || p.name.StartsWith("UI_Com_", StringComparison.Ordinal);
                if (nested) continue;
                if (++hits > 120) break;
                sb.Append("\n  ").Append(Path(t)).Append(" children=").Append(t.childCount);
            }
            Debug.Log("[Ro3Emu.Diag] panels: " + hits + sb);
        }

        /// <summary>
        /// The textures of RawImage / Image components under transforms whose name contains <paramref name="filter"/>
        /// (also inactive ones): name and size in the log, a PNG copy in &lt;client&gt;/ro3emu/dump/.
        /// </summary>
        private static void DumpTextures(string filter)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu", "dump");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Texture tex = null;
                var raw = t.GetComponent<RawImage>(); // Unity objects: no ?. (destroyed objects compare equal to null)
                if (raw != null) tex = raw.texture;
                if (tex == null) { var img = t.GetComponent<Image>(); if (img != null && img.sprite != null) tex = img.sprite.texture; }
                if (tex == null || ++hits > 40) continue;
                string safe = tex.name;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                string file = System.IO.Path.Combine(dir, (safe.Length > 0 ? safe : "tex" + hits) + ".png");
                try { SavePng(tex, file); }
                catch (Exception e) { file = "save failed: " + e.Message; }
                sb.Append("\n  ").Append(Path(t)).Append(" tex=").Append(tex.name).Append(' ').Append(tex.width).Append('x').Append(tex.height)
                  .Append(" -> ").Append(file);
                var sprite = t.GetComponent<Image>();
                if (sprite != null && sprite.sprite != null)
                    sb.Append(" sprite=").Append(sprite.sprite.name).Append(" rect=").Append(sprite.sprite.textureRect.ToString("F0"))
                      .Append(" size=").Append(((RectTransform)t).rect.size.ToString("F0")).Append(" color=").Append(sprite.color);
            }
            Debug.Log("[Ro3Emu.Diag] dumptex '" + filter + "': " + hits + sb);
        }

        /// <summary>
        /// Sprite names containing <paramref name="filter"/>: from every loaded sprite atlas (also sprites no view
        /// shows yet) and every loaded sprite object.
        /// </summary>
        private static void Sprites(string filter)
        {
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var atlas in Resources.FindObjectsOfTypeAll<UnityEngine.U2D.SpriteAtlas>())
            {
                var sprites = new Sprite[atlas.spriteCount];
                atlas.GetSprites(sprites);
                foreach (var s in sprites)
                {
                    if (s == null || s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (++hits > 200) break;
                    sb.Append("\n  atlas ").Append(atlas.name).Append(": ").Append(s.name.Replace("(Clone)", ""));
                    UnityEngine.Object.Destroy(s); // GetSprites hands out clones
                }
            }
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (s.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 || s.name.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                if (++hits > 400) break;
                sb.Append("\n  sprite ").Append(s.name).Append(" tex=").Append(s.texture != null ? s.texture.name : "-");
            }
            Debug.Log("[Ro3Emu.Diag] sprites '" + filter + "': " + hits + sb);
        }

        /// <summary>One sprite (by exact name, from any loaded atlas) cut out of its atlas page into &lt;client&gt;/ro3emu/dump/.</summary>
        private static void SpritePng(string name)
        {
            var sprite = FindSprite(name);
            if (sprite == null) { Debug.Log("[Ro3Emu.Diag] spritepng " + name + ": not found"); return; }
            var tex = sprite.texture;
            var rect = sprite.textureRect;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(rect, 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu", "dump");
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, name + ".png");
            File.WriteAllBytes(file, ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
            Debug.Log("[Ro3Emu.Diag] spritepng " + name + " " + rect.ToString("F0") + " -> " + file);
        }

        /// <summary>A sprite by exact name: a loaded sprite object first, else from any loaded atlas (a clone).</summary>
        internal static Sprite FindSprite(string name)
        {
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s.name == name) return s;
            foreach (var atlas in Resources.FindObjectsOfTypeAll<UnityEngine.U2D.SpriteAtlas>())
            {
                var s = atlas.GetSprite(name);
                if (s != null) return s;
            }
            return null;
        }

        private static readonly string AutoDumpFilter = Environment.GetEnvironmentVariable("RO3EMU_DUMPTEX");
        private static int _autoDumpFrame;

        /// <summary>
        /// With RO3EMU_DUMPTEX=&lt;filter&gt; in the client's environment: dumptex every ~5 s from the frame pump, for screens
        /// before world entry (login, role select) where the GM "!client" command cannot reach.
        /// </summary>
        public static void AutoDump()
        {
            if (string.IsNullOrEmpty(AutoDumpFilter) || ++_autoDumpFrame % 300 != 0) return;
            try { DumpTextures(AutoDumpFilter); } catch (Exception e) { Debug.LogError("[Ro3Emu.Diag] autodump: " + e); }
        }

        /// <summary>Any texture (also non-readable / compressed) through a render texture into a PNG.</summary>
        internal static void SavePng(Texture tex, string file)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(file, ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
