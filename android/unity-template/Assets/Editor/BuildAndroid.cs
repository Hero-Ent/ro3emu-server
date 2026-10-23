using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Ro3Android.Editor
{
    /// <summary>
    /// Batchmode entry point of android\build-apk.ps1: sets the Android player up (IL2CPP ARM64, Vulkan + GLES3, .NET
    /// Framework profile the game's DLLs need, cleartext HTTP to the emulator) and builds android\out\ro3emu.apk.
    /// </summary>
    public static class BuildAndroid
    {
        private const string BootScene = "Assets/Shell/Boot.unity";

        public static void Build()
        {
            try
            {
                Configure();
                EnsureBootScene();
                var output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "out", "ro3emu.apk"));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                // Development: logcat gets the full managed stack traces while the port is young; -ro3Release
                // (build-apk.ps1 -Release) drops it (no "Development Build" mark on screen, a little faster). -ro3ScriptsOnly
                // (build-apk.ps1 -ScriptsOnly) reuses the previous build's scenes and assets and only recompiles code.
                var args = Environment.GetCommandLineArgs();
                var options = Array.IndexOf(args, "-ro3Release") >= 0 ? BuildOptions.None : BuildOptions.Development;
                if (Array.IndexOf(args, "-ro3ScriptsOnly") >= 0) options |= BuildOptions.BuildScriptsOnly;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { BootScene },
                    locationPathName = output,
                    target = BuildTarget.Android,
                    options = options,
                });
                var summary = report.summary;
                Debug.Log($"[Ro3Android] build {summary.result}: {output} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalErrors} errors, {summary.totalTime})");
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        private static void Configure()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            PlayerSettings.companyName = "RO3Emu";
            PlayerSettings.productName = "RO3Emu";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.ro3emu.client");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Unity_4_8);
            // Minimal is the lowest IL2CPP allows; the generated link.xml keeps every game assembly whole.
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Minimal);
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.allowUnsafeCode = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            // Vulkan only: every shader variant is compiled once per API, and the port's targets (current phones) all
            // run Vulkan; GLES3 doubled the build's shader time.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.Android.forceInternetPermission = true;
            // The emulator speaks plain HTTP; this also sets usesCleartextTraffic in the manifest.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            // FrameTimingManager (CPU main/render thread and GPU time per frame) for the port's hitch log.
            PlayerSettings.enableFrameTimingStats = true;

            // The scenery is drawn through BatchRendererGroup: keep the DOTS-instancing variants (Fallback3D's included),
            // which the build strips by default when Entities Graphics is not installed.
            var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (graphics.Length > 0)
            {
                var gso = new SerializedObject(graphics[0]);
                var brg = gso.FindProperty("m_BrgStripping");
                if (brg != null) { brg.intValue = 2; gso.ApplyModifiedProperties(); }     // 2 = Keep All
            }

            // The game ships UnityEngine.InputSystem: let both the old and the new input backends run.
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settings.Length > 0)
            {
                var so = new SerializedObject(settings[0]);
                var input = so.FindProperty("activeInputHandler");
                if (input != null) { input.intValue = 2; so.ApplyModifiedProperties(); }
            }
        }

        private static void EnsureBootScene()
        {
            if (File.Exists(BootScene)) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, BootScene);
        }
    }
}
