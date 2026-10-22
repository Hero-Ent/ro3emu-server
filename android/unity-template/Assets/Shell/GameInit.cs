using System;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Starts the game the way the PC client's level0 did. The game's own [RuntimeInitializeOnLoadMethod]s need nothing
    /// from here: Unity registers them from the plugin DLLs at build time (android\tools\AndroidPatch.cs removes the ones
    /// that must not run on Android).
    /// </summary>
    public static class GameInit
    {
        /// <summary>level0: an EventSystem for the launcher's buttons and a GameObject carrying Ro3Startup, which loads
        /// the launcher prefab from the VFS.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartGame()
        {
            var eventSystem = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
            var inputModule = Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
            if (eventSystem != null && UnityEngine.Object.FindObjectOfType(eventSystem) == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent(eventSystem);
                if (inputModule != null) es.AddComponent(inputModule);
                UnityEngine.Object.DontDestroyOnLoad(es);
            }
            var startup = Type.GetType("Ro3Startup, Assembly-CSharp");
            if (startup == null) { Debug.LogError("[Ro3Android] Ro3Startup not found: the game assemblies are missing from the build"); return; }
            Debug.Log("[Ro3Android] starting the game (Ro3Startup)");
            new GameObject("Startup").AddComponent(startup);
        }
    }
}
