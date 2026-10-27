using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// The platform SDK's role report, switched off (inferred port fix: the port has no platform SDK, MetaSDKUtil logs
    /// "sdk proxy is not init" for each call). LC_DAL_PlayerBasicInfo._processPlayerBasicInfo calls OnRoleInfoReport on
    /// every player info update (each experience or HP change in combat), which builds the report through
    /// Lua_SDK_Factory's SetRoleInfo (GetSelfPlayerAreaId, GetServerNameByAreaId...): a Lua profile of a fight put it
    /// among the biggest costs of the frames that hitch. SetRoleInfo becomes a no-op on the factory and on its instance.
    /// Tried every 2 s until the Lua state has the factory, then once more after each scene load.
    /// </summary>
    public class SdkReportOff : MonoBehaviour
    {
        private const string Patch = @"
local factory = rawget(_G, 'Lua_SDK_Factory')
if type(factory) ~= 'table' then return 'wait' end
local function noop() end
local patched = {}
local function stub(target, label)
  if type(target) == 'table' and type(target.SetRoleInfo) == 'function' and target.SetRoleInfo ~= noop then
    target.SetRoleInfo = noop
    patched[#patched + 1] = label
  end
end
stub(factory, 'factory')
if type(factory.GetInstance) == 'function' then
  local ok, instance = pcall(factory.GetInstance, factory)
  if ok then stub(instance, 'instance') end
end
return 'ok ' .. (#patched > 0 and table.concat(patched, ',') or 'nothing to patch')";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3SdkReportOff");
            DontDestroyOnLoad(go);
            go.AddComponent<SdkReportOff>();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, __) => _done = false;
        }

        private static bool _done;
        private float _next;

        private void Update()
        {
            if (_done || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 2;
            var result = LuaExec.Run(Patch, "sdk report off");
            _done = result != null && result.StartsWith("ok");
        }
    }
}
