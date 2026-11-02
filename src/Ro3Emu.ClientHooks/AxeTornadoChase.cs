using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Hero Saga, not original behavior: auto-combat keeps walking toward its target while Axe Tornado spins. The skill may be
    /// cast on the move (SkillConfig _bMovement 1) and hits around the caster for 3.9 s, but auto-combat stands still for the
    /// whole spin: LC_AutoAttackAutoState._fnBlockChooseSkill blocks while the owner is in enum_State.Skill and the running
    /// skill cannot be interrupted, and LC_AutoAttack_AttackSubState.TryUpdate -> _runSkillCircle -> _trySkillAtSlot returns
    /// on that block before its chase (_evalTryCastSkill -> _findNearestPos -> _tryChaseToTarget). This wraps TryUpdate:
    /// while a spin row runs, it picks the target as the sub-state does (GetCurTarget) and runs the same evaluation and
    /// chase against the spin's own range (LC_AutoAttackHelper.GetSkillCastRange: the FixCircle indicator's radius).
    /// </summary>
    public static class AxeTornadoChase
    {
        /// <summary>CombatService.SpinScript: every Axe Tornado mode row's _kScriptLogic starts with it.</summary>
        private const string SpinScript = "AxeTornado";

        /// <summary>How often the chase is re-evaluated during a spin (inferred: the sub-state itself caches enemies by
        /// SearchEnemyInterval; per frame would only repeat the same pick).</summary>
        private const float IntervalSeconds = 0.25f;

        public delegate bool TryUpdateMethod(Obf_R.Obf_ff self, object ctx);
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly TryUpdateMethod TryUpdateWrapper = OnTryUpdate;
        private static Obf_R.Obf_Ef _tryUpdate, _castRange, _castHeight;
        private static Obf_R.Obf_ff _skillRows;
        private static int _skillState;
        private static float _nextCheck;
        private static readonly Dictionary<long, bool> SpinRows = new Dictionary<long, bool>();

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global, Func<string, Obf_R.Obf_ff> module)
        {
            try
            {
                SpinRows.Clear();
                var db = global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
                var made = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
                var manager = made != null && made.Length > 0 ? made[0] as Obf_R.Obf_ff : null;
                _skillRows = manager == null ? null : PassChallenges.Values(manager, "m_kCFG_SkillConfig");
                var helper = module("lc_autoattackhelper");
                _castRange = helper?.Obf_RuA<Obf_R.Obf_Ef>("GetSkillCastRange");
                _castHeight = helper?.Obf_RuA<Obf_R.Obf_Ef>("GetSkillCastHeight");
                // An int: xLua pushes a long as an Int64 userdata, which IsSettingState's == never matches (KatarHold).
                _skillState = Convert.ToInt32(global.Obf_LuA<object>("enum_State.Skill") ?? 0L);
                var attack = module("lc_autoattack_attacksubstate");
                _tryUpdate = attack?.Obf_RuA<Obf_R.Obf_Ef>("TryUpdate");
                bool ready = _skillRows != null && _castRange != null && _castHeight != null && _skillState != 0 && _tryUpdate != null;
                if (ready) attack.Obf_luA<string, TryUpdateMethod>("TryUpdate", TryUpdateWrapper);
                Debug.Log("[Ro3Emu.AxeTornadoChase] auto-combat chase during Axe Tornado " + (ready ? "hooked" : "not hooked (module or config missing)"));
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.AxeTornadoChase] " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool OnTryUpdate(Obf_R.Obf_ff self, object ctx)
        {
            var made = _tryUpdate.Obf_HuA(self, ctx);
            bool result = made != null && made.Length > 0 && made[0] is bool b && b;
            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + IntervalSeconds;
                try { ChaseDuringSpin(self); }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.AxeTornadoChase] chase: " + e.GetType().Name + ": " + e.Message); }
            }
            return result;
        }

        /// <summary>
        /// The steps of _trySkillAtSlot past its block, for the running spin row: its range and height
        /// (m_fCurrentSkillRange/Sqr/Height, chase range = cast range as SetCurSkillChaseRange(0) sets it), the target
        /// (GetCurTarget), then _evalTryCastSkill, which fills m_kCachedBestTargetPos through _findNearestPos only when the
        /// target is out of range and reachable; in that case _tryChaseToTarget walks there (enum_AutoMoveType.BattleAuto).
        /// </summary>
        private static void ChaseDuringSpin(Obf_R.Obf_ff self)
        {
            var owner = self.Obf_RuA<Obf_R.Obf_ff>("m_tCtx")?.Obf_RuA<Obf_R.Obf_ff>("kOwner");
            int skillId = owner == null ? 0 : RunningSpin(owner);
            if (skillId == 0) return;
            var mount = owner.Obf_RuA<object>("m_kMountID");
            double range = Number(_castRange.Obf_HuA(skillId, mount));
            self.Obf_luA<string, double>("m_fCurrentSkillRange", range);
            self.Obf_luA<string, double>("m_fCurrentSkillRangeSqr", range * range);
            self.Obf_luA<string, double>("m_fCurrentSkillHeight", Number(_castHeight.Obf_HuA(skillId)));
            Call(self, "SetCurSkillChaseRange", 0);
            var found = Call(self, "GetCurTarget", skillId);
            if (!(found is Obf_R.Obf_ff target) || ReferenceEquals(target, owner)) return;
            self.Obf_luA<string, Obf_R.Obf_ff>("m_kTarget", target);
            var best = self.Obf_RuA<Obf_R.Obf_ff>("m_kCachedBestTargetPos");
            if (best == null) return;
            foreach (var axis in new[] { "x", "y", "z" }) best.Obf_luA<string, double>(axis, 0);
            Call(self, "_evalTryCastSkill", owner, target, skillId);
            if (best.Obf_RuA<double>("x") != 0 || best.Obf_RuA<double>("y") != 0 || best.Obf_RuA<double>("z") != 0)
                Call(self, "_tryChaseToTarget");
        }

        /// <summary>The spin row the owner is casting now, or 0: owner:IsSettingState(enum_State.Skill) and
        /// m_kSkillContext:getSkillId(), as _fnBlockChooseSkill reads them.</summary>
        private static int RunningSpin(Obf_R.Obf_ff owner)
        {
            if (!(Call(owner, "IsSettingState", _skillState) is bool casting) || !casting) return 0;
            var context = owner.Obf_RuA<Obf_R.Obf_ff>("m_kSkillContext");
            long id = context == null ? 0 : Convert.ToInt64(Call(context, "getSkillId") ?? 0L);
            return id > 0 && id <= int.MaxValue && IsSpinRow(id) ? (int)id : 0;
        }

        /// <summary>A row whose script is the spin's and that may be cast on the move (_bMovement 1).</summary>
        private static bool IsSpinRow(long id)
        {
            if (SpinRows.TryGetValue(id, out bool spin)) return spin;
            var row = _skillRows.Obf_ruA<long, Obf_R.Obf_ff>(id);
            string script = row?.Obf_RuA<string>("_kScriptLogic");
            spin = script != null && script.StartsWith(SpinScript, StringComparison.Ordinal)
                && Convert.ToInt64(row.Obf_RuA<object>("_bMovement") ?? 0L) == 1;
            SpinRows[id] = spin;
            return spin;
        }

        /// <summary>self:Method(args...), its first result.</summary>
        private static object Call(Obf_R.Obf_ff self, string method, params object[] args)
        {
            var function = self.Obf_RuA<Obf_R.Obf_Ef>(method);
            if (function == null) return null;
            var all = new object[args.Length + 1];
            all[0] = self;
            Array.Copy(args, 0, all, 1, args.Length);
            var made = function.Obf_HuA(all);
            return made != null && made.Length > 0 ? made[0] : null;
        }

        private static double Number(object[] made) =>
            made != null && made.Length > 0 && made[0] != null ? Convert.ToDouble(made[0]) : 0;
    }
}
