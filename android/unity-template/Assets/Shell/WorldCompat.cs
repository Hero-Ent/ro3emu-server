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

        // The game's mobile quality profile renders small: the screen at 2080x960 (stretched to the display) and the 3D
        // at a 0.58 render scale (about 1200x560, blurry under a sharp UI). The phones the port targets have the GPU
        // headroom for native resolution (about 8 ms a frame at 0.58): keep both at full size.
        private void ForceFullResolution()
        {
            int width = Display.main.systemWidth, height = Display.main.systemHeight;
            if (width > 0 && height > 0 && (Screen.width != width || Screen.height != height))
            {
                Debug.Log($"[Ro3Android] screen {Screen.width}x{Screen.height} -> {width}x{height}");
                Screen.SetResolution(width, height, Screen.fullScreenMode);
            }
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var scale = pipeline?.GetType().GetProperty("renderScale");
            if (scale != null && scale.CanWrite && (float)scale.GetValue(pipeline) < 1f)
            {
                Debug.Log($"[Ro3Android] render scale {scale.GetValue(pipeline)} -> 1");
                scale.SetValue(pipeline, 1f);
            }
        }

        // Diagnostics: what the picture is rendered at (the world looks low resolution), logged whenever it changes.
        private string _lastResolution;

        private void LogResolution()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var scale = pipeline?.GetType().GetProperty("renderScale")?.GetValue(pipeline);
            var filter = pipeline?.GetType().GetProperty("upscalingFilter")?.GetValue(pipeline);
            var cam = Camera.main;
            var text = $"native {Display.main.systemWidth}x{Display.main.systemHeight}, screen {Screen.width}x{Screen.height}, "
                + $"current {Screen.currentResolution.width}x{Screen.currentResolution.height}, renderScale {scale}, filter {filter}, "
                + $"camera {(cam ? $"{cam.pixelWidth}x{cam.pixelHeight} rt={(cam.targetTexture ? cam.targetTexture.name : "none")}" : "none")}, "
                + $"quality {QualitySettings.GetQualityLevel()} msaa {QualitySettings.antiAliasing}, pipeline {pipeline?.name}";
            if (text == _lastResolution) return;
            _lastResolution = text;
            Debug.Log($"[Ro3Android] resolution: {text}");
        }

        // RoleShadowManager (the round shadows under characters) keeps Camera.main from its Awake, which here runs before
        // the game camera exists: it then throws in every LateUpdate (a NullReferenceException a frame, logged). Hand it
        // the camera once there is one.
        private static readonly Type RoleShadows = Type.GetType("RoleShadowManager, Render");
        private static readonly FieldInfo RoleShadowCamera = RoleShadows?.GetField("m_kMainCamera", BindingFlags.NonPublic | BindingFlags.Instance);

        // Diagnostics for the target portrait (a dark red disc): cameras that render into a texture, enabled or not (a
        // portrait camera renders on demand), and UI graphics that show a render texture; each logged once.
        private static readonly Type UiGraphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
        private readonly System.Collections.Generic.HashSet<int> _seenPortraitObjects = new System.Collections.Generic.HashSet<int>();
        private float _nextPortraitCheck;

        private void LogPortraitSources()
        {
            if (Time.realtimeSinceStartup < _nextPortraitCheck) return;
            _nextPortraitCheck = Time.realtimeSinceStartup + 5;
            foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (cam.targetTexture == null || !cam.gameObject.scene.IsValid() || !_seenPortraitObjects.Add(cam.GetInstanceID())) continue;
                Debug.Log($"[Ro3Android] rt camera '{PathOf(cam.transform)}' enabled={cam.enabled} active={cam.gameObject.activeInHierarchy} rt={cam.targetTexture.name} {cam.targetTexture.width}x{cam.targetTexture.height} clear={cam.clearFlags} bg={cam.backgroundColor} mask=0x{cam.cullingMask:X}");
            }
            if (UiGraphic == null) return;
            var mainTexture = UiGraphic.GetProperty("mainTexture");
            foreach (var g in FindObjectsOfType(UiGraphic))
            {
                var path = PathOf(((Component)g).transform);
                if (!path.Contains("TargetAvatarInfo") || !_seenPortraitObjects.Add(g.GetInstanceID())) continue;
                var tex = mainTexture?.GetValue(g) as Texture;
                var mat = UiGraphic.GetProperty("materialForRendering")?.GetValue(g) as Material;
                var baseMat = UiGraphic.GetProperty("material")?.GetValue(g) as Material;
                var sprite = g.GetType().GetProperty("sprite")?.GetValue(g) as Sprite;
                var color = UiGraphic.GetProperty("color")?.GetValue(g);
                Debug.Log($"[Ro3Android] portrait graphic {g.GetType().Name} '{path}' sprite={(sprite ? sprite.name : "none")} tex={(tex ? $"{tex.name} {tex.width}x{tex.height}" : "none")} color={color} "
                    + $"mat={(mat ? $"'{mat.name}' {mat.shader.name}" : "?")} base={(baseMat ? $"'{baseMat.name}' {baseMat.shader.name}" : "?")} kw=[{(mat ? string.Join(",", mat.shaderKeywords) : "")}]");
            }
        }

        private static string PathOf(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null && path.Length < 200; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private float _nextRoleShadowCheck;

        private void FixRoleShadowCamera()
        {
            // FindObjectsOfType walks every object: once every 15 s is enough to catch a new manager after a scene change.
            if (RoleShadowCamera == null || Camera.main == null || Time.realtimeSinceStartup < _nextRoleShadowCheck) return;
            _nextRoleShadowCheck = Time.realtimeSinceStartup + 15;
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

        // Where a long frame went: the engine's profiler markers for the previous frame (development builds), the
        // five biggest in milliseconds.
        private static readonly string[] MarkerNames =
        {
            "PlayerLoop", "Update.ScriptRunBehaviourUpdate", "PreLateUpdate.ScriptRunBehaviourLateUpdate",
            "FixedUpdate.ScriptRunBehaviourFixedUpdate", "Update.ScriptRunDelayedDynamicFrameRate", "PostLateUpdate.FinishFrameRendering",
            "Gfx.WaitForPresentOnGfxThread", "Gfx.WaitForRenderThread", "Gfx.WaitForGfxCommandsFromMainThread", "Gfx.PresentFrame",
            "WaitForTargetFPS", "Shader.CreateGPUProgram", "CreateGPUProgram", "GC.Collect", "Loading.ReadObject", "Loading.AwakeFromLoad",
            "Instantiate", "AssetBundle.LoadAsset", "Application.Integrate Assets in Background", "Physics.Processing",
            "Canvas.SendWillRenderCanvases", "UGUI.Rendering.UpdateBatches", "RenderPipelineManager.DoRenderLoop_Internal()",
            "Inl_UniversalRenderPipeline.RenderSingleCameraInternal: Main Camera", "Camera.Render",
        };
        private Unity.Profiling.ProfilerRecorder[] _markers;

        private string Markers()
        {
            if (_markers == null) return "";
            var top = new System.Collections.Generic.List<(string Name, double Ms)>();
            for (int i = 0; i < _markers.Length; i++)
                if (_markers[i].Valid && _markers[i].LastValue > 0) top.Add((MarkerNames[i], _markers[i].LastValue / 1e6));
            top.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < top.Count && i < 6; i++) sb.Append($" | {top[i].Name} {top[i].Ms:0.0}");
            return sb.ToString();
        }

        private void OnEnable()
        {
            _markers = new Unity.Profiling.ProfilerRecorder[MarkerNames.Length];
            for (int i = 0; i < MarkerNames.Length; i++)
                _markers[i] = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Internal, MarkerNames[i]);
        }

        private void OnDisable()
        {
            if (_markers != null) foreach (var m in _markers) m.Dispose();
        }

        private void Update()
        {
            // Hitch log: frames over 80 ms, with the garbage collections since the previous frame.
            int gc = System.GC.CollectionCount(0);
            if (Time.unscaledDeltaTime > 0.08f && Time.realtimeSinceStartup > 30)
                Debug.Log($"[Ro3Android] hitch {Time.unscaledDeltaTime * 1000:0} ms at {Time.realtimeSinceStartup:0.0}s (GC {gc - _gcCount}){Markers()}");
            _gcCount = gc;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Periodic();
            if (watch.ElapsedMilliseconds > 10) Debug.Log($"[Ro3Android] world compat tick {watch.ElapsedMilliseconds} ms");
        }

        private void Periodic()
        {
            if (_facade != null && _compatible != null && !(bool)_compatible.GetValue(_facade)) _compatible.SetValue(_facade, true);
            if (!_assetsLogged) LogSubsceneAssets();
            ForceTopQuality();
            FixRoleShadowCamera();
            ForceFullResolution();
            LogResolution();
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
