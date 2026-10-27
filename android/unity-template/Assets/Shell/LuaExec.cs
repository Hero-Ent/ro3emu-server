using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Diagnostics on demand: a "lua-exec.lua" file in the app's folder is run in the game's own Lua state, and what it
    /// returns is logged ("[Ro3Android] lua-exec: ..."), then the file is removed. For questions only the running game
    /// answers (a flag of the scene transition, a widget's value) without rebuilding the APK:
    ///   adb push probe.lua /sdcard/Android/data/com.ro3emu.client/files/lua-exec.lua
    /// A "lua-autorun.lua" file is kept and run in every session instead, once the Lua state is up, again each second
    /// for as long as it returns "wait" (for probes that must be in place before something happens, a scene transition).
    /// The state is LA_LuaManager's LuaEnv (its field Obf_Dc), run with the env's DoString (Obf_tTA(chunk, name, env)),
    /// the names of the client's obfuscated Assembly-CSharp. Reflection only: the shell does not compile against the game.
    /// </summary>
    public class LuaExec : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3LuaExec");
            DontDestroyOnLoad(go);
            go.AddComponent<LuaExec>();
        }

        private float _next;
        private bool _autorunDone;

        private void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1;
            if (!_autorunDone) Autorun();
            var file = Path.Combine(Application.persistentDataPath, "lua-exec.lua");
            if (!File.Exists(file)) return;
            string chunk;
            try
            {
                chunk = File.ReadAllText(file);
                File.Delete(file);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ro3Android] lua-exec: cannot read {file}: {e.Message}");
                return;
            }
            Run(chunk);
        }

        private void Autorun()
        {
            var file = Path.Combine(Application.persistentDataPath, "lua-autorun.lua");
            if (!File.Exists(file)) { _autorunDone = true; return; }
            if (Env(out _, out _) == null) return;
            var result = Run(File.ReadAllText(file), "lua-autorun");
            _autorunDone = result != "wait";
        }

        private static object Env(out MethodInfo doString, out string missing)
        {
            var manager = Type.GetType("LA_LuaManager, Assembly-CSharp");
            var instance = manager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
            var env = manager?.GetField("Obf_Dc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
            doString = env?.GetType().GetMethod("Obf_tTA");
            missing = doString == null ? "no LuaEnv (LA_LuaManager.Obf_Dc.Obf_tTA)" : null;
            return doString == null ? null : env;
        }

        /// <summary>Runs <paramref name="chunk"/> in the game's Lua state; what it returns, or null when the state is not up
        /// or the chunk failed (logged). Also used by the port's own HUD buttons (HudLayout).</summary>
        internal static string Run(string chunk, string name = "lua-exec")
        {
            try
            {
                var env = Env(out var doString, out var missing);
                if (env == null) { Debug.LogWarning($"[Ro3Android] {name}: {missing}"); return null; }
                var results = doString.Invoke(env, new object[] { chunk, name, null }) as object[];
                var text = results == null ? "(nothing)" : string.Join(" | ", results.Select(r => r?.ToString() ?? "nil"));
                if (text != "wait") Debug.Log($"[Ro3Android] {name}: {text}");
                return text;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ro3Android] {name} failed: {(e.InnerException ?? e).Message}");
                return null;
            }
        }
    }
}
