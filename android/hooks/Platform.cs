using System.IO;
using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// The game's UNITY_PC flag (LA_Utils, read by Lua as Debug.Unity_PC: android\tools\AndroidPatch.cs redirects its getter
    /// here). The PC client has it fixed to true, which picks the PC UI (*_PC widgets, keyboard input); off, the game shows
    /// its mobile UI (joystick, skill buttons). An empty file named pc-ui in the app's data folder brings the PC UI back.
    /// </summary>
    public static class Platform
    {
        private static bool? _pcUi, _luaLog;

        /// <summary>
        /// GS_Debug.ShowLog (android\tools\AndroidPatch.cs redirects its getter here), fixed to true in the reconstructed
        /// client. Lua's Debug.Log/LogWarning/LogFormat check it and then build a debug.traceback for every line: in a
        /// fight that is hundreds a second, 70-200 ms of every frame on a phone. Off unless the app's data folder has an
        /// empty file named lua-log.
        /// </summary>
        public static bool LuaLog()
        {
            if (_luaLog == null)
            {
                try { _luaLog = File.Exists(Path.Combine(Application.persistentDataPath, "lua-log")); }
                catch { _luaLog = false; }
                Debug.Log($"[Ro3Android] Lua log: {(_luaLog.Value ? "on" : "off")}");
            }
            return _luaLog.Value;
        }

        public static bool UnityPc()
        {
            if (_pcUi == null)
            {
                try { _pcUi = File.Exists(Path.Combine(Application.persistentDataPath, "pc-ui")); }
                catch { _pcUi = false; }
                Debug.Log($"[Ro3Android] UI mode: {(_pcUi.Value ? "PC" : "mobile")}");
            }
            return _pcUi.Value;
        }
    }
}
