using System;
using System.Linq;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// M1 probe: proves the game's assemblies survived IL2CPP by resolving a few of its types by name and logging the
    /// result to logcat (tag Unity, "[Ro3Android]"). Reflection only, so the shell compiles without referencing them:
    /// the game's own Assembly-CSharp.dll is a plugin, and this project must not produce one.
    /// </summary>
    public static class ShellProbe
    {
        private static readonly string[] Probes =
        {
            "Ro3Startup, Assembly-CSharp",
            "HappyEngine.HLAsset.AssetBundleLoader, HappyEngine.Resource",
            "HappyEngine.GS_Debug, Happyengine.Base.Runtime",
            "HybridCLR.RuntimeApi, HybridCLR.Runtime",
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Run()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Debug.Log($"[Ro3Android] {assemblies.Length} assemblies loaded: " + string.Join(", ", assemblies.Select(a => a.GetName().Name).OrderBy(n => n)));
            foreach (var name in Probes)
            {
                Type type = null;
                try { type = Type.GetType(name, throwOnError: false); }
                catch (Exception e) { Debug.LogWarning($"[Ro3Android] {name}: {e.GetType().Name} {e.Message}"); continue; }
                Debug.Log($"[Ro3Android] {name}: " + (type == null ? "MISSING" : $"ok ({type.GetMethods().Length} methods)"));
            }
            Debug.Log($"[Ro3Android] platform={Application.platform} gfx={SystemInfo.graphicsDeviceType} gpu={SystemInfo.graphicsDeviceName} "
                + $"bc7={SystemInfo.SupportsTextureFormat(TextureFormat.BC7)} dxt5={SystemInfo.SupportsTextureFormat(TextureFormat.DXT5)} "
                + $"astc={SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_6x6)}");
        }
    }
}
