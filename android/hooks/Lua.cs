using System;
using System.IO;
using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// Lua modules the port replaces. android\tools\AndroidPatch.cs makes NewResourceManager.LoadLuaFromFile and
    /// LoadLuaBytesFromFile (xLua's loaders, LA_LuaLoader; the second gets only the file name) ask OverridePath /
    /// OverrideBytes first: for a module listed here they load the port's source, embedded in this
    /// DLL and written once to the app's data folder (the game's loader reads files), instead of the client's chunk.
    /// Plain Lua source goes through libxlua's chunk decoder untouched (ro3_chunk.c only takes the client's format).
    /// </summary>
    public static class Lua
    {
        // Module name as the loader passes it -> embedded resource (android\hooks\Lua\*.lua).
        private static readonly string[] Replaced = { "Framework/Core/Lua_LazyClass" };
        private static int _loggedNames;

        public static string OverridePath(string module)
        {
            if (module == null) return null;
            var name = module.Replace('\\', '/');
            if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            name = name.Replace('.', '/');      // require's dotted form
            if (_loggedNames < 40)
            {
                _loggedNames++;
                Debug.Log($"[Ro3Android] Lua loader asks for '{module}'");
            }
            // Matched by file name: the loaders pass a bare name, a module path or a chunk path.
            foreach (var replaced in Replaced)
                if (string.Equals(Path.GetFileName(name), Path.GetFileName(replaced), StringComparison.OrdinalIgnoreCase))
                    return Extract(Path.GetFileName(replaced) + ".lua");
            return null;
        }

        /// <summary>The same for NewResourceManager.LoadLuaBytesFromFile (LA_LuaLoader's byte[] loader): the file's bytes.</summary>
        public static byte[] OverrideBytes(string module)
        {
            var path = OverridePath(module);
            return path != null ? File.ReadAllBytes(path) : null;
        }

        private static string Extract(string file)
        {
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "ro3android-lua");
                var path = Path.Combine(dir, file);
                using (var source = typeof(Lua).Assembly.GetManifestResourceStream("Ro3Android.Lua." + file))
                {
                    if (source == null) { Debug.LogError($"[Ro3Android] Lua override {file} not embedded"); return null; }
                    var bytes = new MemoryStream();
                    source.CopyTo(bytes);
                    Directory.CreateDirectory(dir);
                    // Rewritten every time: a new APK brings a new version of the file.
                    File.WriteAllBytes(path, bytes.ToArray());
                }
                Debug.Log($"[Ro3Android] Lua override: {file}");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Ro3Android] Lua override {file} failed, using the client's: {e.Message}");
                return null;
            }
        }
    }
}
