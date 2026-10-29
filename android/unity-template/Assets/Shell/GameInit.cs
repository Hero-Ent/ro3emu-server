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
        /// <summary>The game data lives outside the APK: the updater (android\updater, an Android-only plugin, hence the
        /// reflection) brings it up to date first and then starts the game.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var updater = Type.GetType("Ro3Android.Updater.UpdateScreen, Ro3Android.Updater");
            if (updater == null) { StartGame(); return; }
            var screen = new GameObject("Updater").AddComponent(updater);
            updater.GetField("OnReady").SetValue(screen, (Action)StartGame);
        }

        /// <summary>level0: an EventSystem for the launcher's buttons and a GameObject carrying Ro3Startup, which loads
        /// the launcher prefab from the VFS. The input module is the Input System's: only it raises
        /// BaseInputModule.OnPointerProgress, the event UIDragButton.CheckPress starts a drag from (the skill bar's
        /// "drag skills to the slots"); StandaloneInputModule never raises it, so no drag began.</summary>
        private static void StartGame()
        {
            var eventSystem = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
            var inputModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                ?? Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
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
