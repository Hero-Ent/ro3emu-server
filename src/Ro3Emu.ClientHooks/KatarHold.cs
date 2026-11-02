using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Hero Saga, not original behavior: the Guillotine Cross katar skills keep casting while their key is held, like
    /// Sonic Blow. The PC bar repeats a skill every 0.2 s while it is held only when its SkillConfig row has
    /// _iPcReleasingSkill = enum_SkillReleaseType.HoldContinue (UI_Sys_Main_JoyStickSkillWidgetBaseCtrl, the press
    /// handler starts m_kCurSkillTickTimer). The client's table sets it on Sonic Blow, Poison Blade, Rolling Cutter
    /// (15303) and 15324 only; this sets it on every level and mode row of the other katar skills. The rows' __newindex is
    /// configdefine.newIndexFunc (read-only), so the field is written with rawset; reads still pass through the row.
    /// </summary>
    public static class KatarHold
    {
        /// <summary>enum_SkillReleaseType.HoldContinue (Lua_GameDefine). An int: xLua pushes a long as an Int64 userdata,
        /// which the widget's == against the enum's Lua integer never matches.</summary>
        private const int HoldContinue = 1;

        /// <summary>SkillConfig _iSkillID groups of job 5400: hesehuanying, huixuanshizizhan, zhimingzhuahen, elingweixie.</summary>
        private static readonly long[] Groups = { 15301, 15302, 15305, 15306 };

        /// <summary>Row ids of a group: level L is ggggg00 + L, its mode-1 row 1ggggg10 + L (SkillConfig 1530101, 11530111,
        /// 1530610).</summary>
        private const int MaxLevel = 10, ModeRowBase = 10_000_010;

        public delegate void Method2(Obf_R.Obf_ff self, object a, object b);
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly Method2 InputKeyWrapper = OnInputKey;
        private static Obf_R.Obf_Ef _inputKey, _rawset;

        /// <summary>From LuaPatches.Tick, once per LuaEnv: marks the table rows, then wraps the press handler.</summary>
        internal static void Install(Obf_R.Obf_ff global, Func<string, Obf_R.Obf_ff> module)
        {
            try
            {
                var db = global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
                var made = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
                var manager = made != null && made.Length > 0 ? made[0] as Obf_R.Obf_ff : null;
                var values = manager == null ? null : PassChallenges.Values(manager, "m_kCFG_SkillConfig");
                _rawset = global.Obf_LuA<Obf_R.Obf_Ef>("rawset");
                if (values == null || _rawset == null)
                {
                    Debug.LogWarning("[Ro3Emu.KatarHold] SkillConfig not found; katar skills keep their single cast");
                    return;
                }
                int rows = 0;
                foreach (long group in Groups)
                    for (int level = 1; level <= MaxLevel; level++)
                        foreach (long id in new[] { group * 100 + level, ModeRowBase + group * 100 + level })
                        {
                            var row = values.Obf_ruA<long, Obf_R.Obf_ff>(id);
                            if (row == null) continue;
                            _rawset.Obf_HuA(row, "_iPcReleasingSkill", HoldContinue);
                            rows++;
                        }
                var bar = module("ui_sys_main_joystickskillwidgetbasectrl");
                _inputKey = bar?.Obf_RuA<Obf_R.Obf_Ef>("OnInputKey");
                if (_inputKey != null) bar.Obf_luA<string, Method2>("OnInputKey", InputKeyWrapper);
                Debug.Log("[Ro3Emu.KatarHold] " + rows + " katar skill row(s) repeat while held; press hook " + (_inputKey != null));
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.KatarHold] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void OnInputKey(Obf_R.Obf_ff self, object item, object key)
        {
            MarkHeld(self, item);
            _inputKey?.Obf_HuA(self, item, key);
        }

        /// <summary>
        /// The press handler reads the row from the bar's own SkillDataMap (UI_Sys_Main_JoystickSkillAbstractCtrl
        /// ._getSkillDataById, m_kSkill = the module's captured m_kCFG_SkillConfig:GetEntryPtr(id)), which is not the row
        /// Install marked; that row is marked too before the original handler runs.
        /// </summary>
        private static void MarkHeld(Obf_R.Obf_ff self, object item)
        {
            if (_rawset == null || !(item is Obf_R.Obf_ff widget)) return;
            try
            {
                long id = Convert.ToInt64(widget.Obf_RuA<object>("m_iOnlySkillId") ?? 0L);
                if (!IsKatarRow(id)) return;
                // An int, not a long: the id is a table key on the Lua side (see HoldContinue).
                var made = self.Obf_RuA<Obf_R.Obf_Ef>("_getSkillDataById")?.Obf_HuA(self, (int)id);
                var skill = made != null && made.Length > 0 ? (made[0] as Obf_R.Obf_ff)?.Obf_RuA<Obf_R.Obf_ff>("m_kSkill") : null;
                if (skill != null) _rawset.Obf_HuA(skill, "_iPcReleasingSkill", HoldContinue);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.KatarHold] mark: " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool IsKatarRow(long id)
        {
            long group = (id >= ModeRowBase ? id - ModeRowBase : id) / 100;
            return Array.IndexOf(Groups, group) >= 0;
        }
    }
}
