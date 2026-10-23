using System;
using System.Reflection;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Switches the game's world renderer to the paths it keeps for GPUs without its compute shaders (which are D3D11
    /// only here): the scenery batches (HappyEngine.CustomScene BatchManager) upload with GraphicsBuffer.SetData instead
    /// of the UploadBuffer compute kernel, and the GPU-driven terrain runs in CPU mode (WorldStreamFacade.CompatibleMode).
    /// The sky's shader is not ported either, so cameras that clear to the skybox clear to a plain sky colour.
    /// </summary>
    public class WorldCompat : MonoBehaviour
    {
        // The game camera looks down on the field and the terrain is not drawn yet: a grass colour reads as ground.
        private static readonly Color Sky = new Color(0.36f, 0.5f, 0.27f);
        private static readonly Color MenuSky = new Color(0.55f, 0.72f, 0.9f);
        private PropertyInfo _world;
        private static readonly Type CameraData = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        private static readonly PropertyInfo RequiresDepth = CameraData?.GetProperty("requiresDepthTexture");
        private static readonly PropertyInfo RequiresColor = CameraData?.GetProperty("requiresColorTexture");
        private PropertyInfo _compatible;
        private object _facade;
        private float _next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3WorldCompat");
            DontDestroyOnLoad(go);
            go.AddComponent<WorldCompat>();
        }

        private void Awake()
        {
            var uploader = Type.GetType("HappyEngine.CustomScene.Runtime.Rendering.RawBufferUploader, HappyEngine.CustomScene");
            var allow = uploader?.GetField("AllowRawBufferUploader", BindingFlags.Public | BindingFlags.Static);
            allow?.SetValue(null, false);
            var facade = Type.GetType("HappyEngine.WorldStream.WorldStreamTool.WorldStreamFacade, Happyengine.WorldStream.Runtime");
            _facade = facade?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _compatible = facade?.GetProperty("CompatibleMode");
            _world = facade?.GetProperty("World");
            Debug.Log($"[Ro3Android] world compat: raw uploader off={allow != null}, terrain CPU mode={_facade != null && _compatible != null}");

            // The game logs a lot (Lua logs every step with a traceback). Keep the lines (the port's test scripts read
            // them) but drop what makes each one expensive: the engine's stack trace capture, and the game's copy of
            // every line to a file on the device.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
            var gsDebug = Type.GetType("HappyEngine.GS_Debug, Happyengine.Base.Runtime");
            var saveToFile = gsDebug?.GetField("m_bSaveLogToLocal", BindingFlags.NonPublic | BindingFlags.Static);
            saveToFile?.SetValue(null, false);
            Debug.Log($"[Ro3Android] log cost: stack traces off, save to file off={saveToFile != null}");
        }

        // Diagnostics, once the world has subscenes: what each subscene's asset list (meshes, shaders, textures the
        // scenery materials index into) holds, and how many slots are empty.
        private bool _assetsLogged;

        private void LogSubsceneAssets()
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var world = _facade?.GetType().GetProperty("World", any)?.GetValue(_facade);
            var manager = world?.GetType().GetProperty("SubsceneManager", any)?.GetValue(world);
            if (!(manager?.GetType().GetProperty("Subscenes", any)?.GetValue(manager) is System.Collections.IEnumerable subscenes)) return;
            int logged = 0;
            foreach (var sub in subscenes)
            {
                if (!(sub.GetType().GetField("Assets", any)?.GetValue(sub) is System.Collections.Generic.List<UnityEngine.Object> assets) || assets.Count == 0) continue;
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var a in assets)
                {
                    var key = a ? a.GetType().Name : "null";
                    counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                }
                Debug.Log($"[Ro3Android] subscene assets: {assets.Count} = {string.Join(", ", System.Linq.Enumerable.Select(counts, kv => kv.Key + " " + kv.Value))}");
                _assetsLogged = true;
                if (++logged == 3) break;
            }
        }

        // Diagnostics: UI images that show a render texture (the target portrait comes up magenta) and the cameras that
        // render into one, each logged once with what draws it.
        private static readonly Type RawImage = Type.GetType("UnityEngine.UI.RawImage, UnityEngine.UI");
        private readonly System.Collections.Generic.HashSet<int> _seenTargets = new System.Collections.Generic.HashSet<int>();

        private void LogRenderTextures()
        {
            if (RawImage != null)
                foreach (var o in FindObjectsOfType(RawImage))
                {
                    var tex = RawImage.GetProperty("texture")?.GetValue(o) as Texture;
                    var mat = RawImage.GetProperty("material")?.GetValue(o) as Material;
                    if (!(tex is RenderTexture) || !_seenTargets.Add(o.GetInstanceID())) continue;
                    var t = ((Component)o).transform;
                    var path = t.name;
                    for (var p = t.parent; p != null && path.Length < 160; p = p.parent) path = p.name + "/" + path;
                    Debug.Log($"[Ro3Android] RawImage '{path}' rt={tex.name} {tex.width}x{tex.height} mat={(mat ? mat.name + " / " + mat.shader.name : "none")}");
                }
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture == null || !_seenTargets.Add(cam.GetInstanceID())) continue;
                var data = CameraData != null ? cam.GetComponent(CameraData) : null;
                var renderer = data != null ? CameraData.GetProperty("scriptableRenderer")?.GetValue(data) : null;
                Debug.Log($"[Ro3Android] camera '{cam.name}' -> rt={cam.targetTexture.name} {cam.targetTexture.width}x{cam.targetTexture.height} clear={cam.clearFlags} bg={cam.backgroundColor} mask=0x{cam.cullingMask:X} renderer={renderer?.GetType().Name} post={(data != null && RenderPostValue(data))}");
            }
        }

        private static bool RenderPostValue(Component data) => (bool)(CameraData.GetProperty("renderPostProcessing")?.GetValue(data) ?? false);

        // RoleShadowManager (the round shadows under characters) keeps Camera.main from its Awake, which here runs before
        // the game camera exists: it then throws in every LateUpdate (a NullReferenceException a frame, logged). Hand it
        // the camera once there is one.
        private static readonly Type RoleShadows = Type.GetType("RoleShadowManager, Render");
        private static readonly FieldInfo RoleShadowCamera = RoleShadows?.GetField("m_kMainCamera", BindingFlags.NonPublic | BindingFlags.Instance);

        private void FixRoleShadowCamera()
        {
            if (RoleShadowCamera == null || Camera.main == null) return;
            foreach (var manager in FindObjectsOfType(RoleShadows))
                if ((RoleShadowCamera.GetValue(manager) as Camera) == null)
                {
                    RoleShadowCamera.SetValue(manager, Camera.main);
                    Debug.Log($"[Ro3Android] RoleShadowManager camera -> {Camera.main.name}");
                }
        }

        // The scenery's detail comes from per-quality asset sets ({subscene}_{quality}_resources: meshes and textures),
        // not from LODs, and the game's settings pick a low one here (low-poly world): keep the world on the top one.
        private void ForceTopQuality()
        {
            if (_facade == null) return;
            var type = _facade.GetType();
            int count = (int)(type.GetProperty("NumQualities")?.GetValue(_facade) ?? 0);
            int quality = (int)(type.GetProperty("Quality")?.GetValue(_facade) ?? 0);
            if (count <= 0 || quality >= count - 1) return;
            type.GetMethod("ChangeQuality")?.Invoke(_facade, new object[] { count - 1 });
            Debug.Log($"[Ro3Android] world quality {quality} -> {count - 1} (of {count})");
        }

        // Once a second: the game may reset the mode, and cameras come and go with the scenes.
        private int _gcCount;

        private void Update()
        {
            // Hitch log: frames over 80 ms, with the garbage collections since the previous frame.
            int gc = System.GC.CollectionCount(0);
            FrameTimingManager.CaptureFrameTimings();
            if (Time.unscaledDeltaTime > 0.08f && Time.realtimeSinceStartup > 30)
            {
                var timing = new FrameTiming[1];
                string split = FrameTimingManager.GetLatestTimings(1, timing) > 0
                    ? $" main {timing[0].cpuMainThreadFrameTime:0} render {timing[0].cpuRenderThreadFrameTime:0} gpu {timing[0].gpuFrameTime:0}"
                    : "";
                Debug.Log($"[Ro3Android] hitch {Time.unscaledDeltaTime * 1000:0} ms at {Time.realtimeSinceStartup:0.0}s (GC {gc - _gcCount}){split}");
            }
            _gcCount = gc;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            if (_facade != null && _compatible != null && !(bool)_compatible.GetValue(_facade)) _compatible.SetValue(_facade, true);
            if (!_assetsLogged) LogSubsceneAssets();
            ForceTopQuality();
            FixRoleShadowCamera();
            // The game's mobile settings cap the frame rate at 30 (Lua QS_RenderLevel_FPS); the phones the port targets
            // keep 60.
            if (Application.targetFrameRate < 60)
            {
                Debug.Log($"[Ro3Android] frame rate {Application.targetFrameRate} -> 60 (vSync {QualitySettings.vSyncCount} -> 0)");
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
            foreach (var cam in Camera.allCameras)
            {
                // In the world the camera looks down on the field (grass reads as ground); menus get a sky colour.
                bool inWorld = _facade != null && _world?.GetValue(_facade) != null;
                if (cam.clearFlags == CameraClearFlags.Skybox || (cam.clearFlags == CameraClearFlags.SolidColor && cam.backgroundColor == (inWorld ? MenuSky : Sky)))
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = inWorld ? Sky : MenuSky;
                }
                // The water (Base/RoThreeWater) reads the scene's depth and colour behind it: make URP produce both
                // for the world camera (not the UI one, which only renders layer 5).
                if (CameraData == null || cam.cullingMask == 1 << 5) continue;
                var data = cam.GetComponent(CameraData);
                if (data == null) continue;
                if (!(bool)RequiresDepth.GetValue(data)) RequiresDepth.SetValue(data, true);
                if (!(bool)RequiresColor.GetValue(data)) RequiresColor.SetValue(data, true);
            }
        }
    }
}
