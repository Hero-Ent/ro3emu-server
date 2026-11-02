using System;
using System.Linq;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Hero Saga, not original behavior: with GS_Debug.ShowLog off (ClientPatcher, unless --verbose), Lua's
    /// Debug.LogFormat(logType, force, format, ...) still writes every forced line, each with a debug.traceback
    /// (Lua_Debug main/5 returns early only when ShowLog is false, force is false and the type is not Error). The client
    /// forces some on hot paths: LC_Scene_Sync's "[RecoveryAOI] bases={0} leaves={1} ..." on every AOI sync, and
    /// Lua_CFG_ItemConfig.GetIconPath's "can not find entry by itemId={0}" whenever a bound slot is empty (itemId 0).
    /// About 90 lines a second in a fight, which costs frames. This drops the forced Log and Warning lines; Error lines
    /// still go to the original.
    /// </summary>
    public static class LuaLogFilter
    {
        public delegate void LogFormatMethod(object logType, object force, params object[] rest);
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly LogFormatMethod LogFormatWrapper = OnLogFormat;
        private static Obf_R.Obf_Ef _logFormat;
        private static long _errorType;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global, Func<string, Obf_R.Obf_ff> module)
        {
            try
            {
                var debug = global.Obf_RuA<Obf_R.Obf_ff>("Debug") ?? module("lua_debug");
                if (debug == null) { Debug.Log("[Ro3Emu.LuaLogFilter] not hooked (Debug module missing)"); return; }
                if (debug.Obf_RuA<object>("ShowLog") is bool show && show) { Debug.Log("[Ro3Emu.LuaLogFilter] ShowLog on: Lua logs kept"); return; }
                var original = debug.Obf_RuA<Obf_R.Obf_Ef>("LogFormat");
                object error = global.Obf_LuA<object>("enum_LogType.Error");
                if (original == null || error == null) { Debug.Log("[Ro3Emu.LuaLogFilter] not hooked (LogFormat or enum_LogType missing)"); return; }
                _logFormat = original;
                _errorType = Convert.ToInt64(error);
                debug.Obf_luA<string, LogFormatMethod>("LogFormat", LogFormatWrapper);
                Debug.Log("[Ro3Emu.LuaLogFilter] forced Lua Log/Warning lines dropped");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.LuaLogFilter] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void OnLogFormat(object logType, object force, params object[] rest)
        {
            if (logType == null || Convert.ToInt64(logType) != _errorType) return;
            _logFormat.Obf_HuA(new[] { logType, force }.Concat(rest ?? Array.Empty<object>()).ToArray());
        }
    }
}
