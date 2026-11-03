using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The born crawl of the Maya story ant (LC_State_Unit_Born: NoviceDungeonBornPatrolNPCID on patrol 25 in
    /// NoviceDungeonMoveOnSurfaceDungeonsID 1000301) stood still on its first point. Each step,
    /// LC_Movement_PathListAgent.TickLocalPatrol turns the unit toward the next point (Vector3.SetForward, world up) and
    /// then lays it on the surface with PS_Physics' MoveOnSurface: a 5 m ray along the unit's up, MASK_GROUND, front faces
    /// only. In this client's cave the route's upper half lies on the back of collider_sc_plo_cavefloor_01 (its faces
    /// there point down, measured with "!client unit"), so the ray misses, GetMoveOnSurfaceInfo keeps the old position and
    /// the patrol index never advances. Inferred, not original behavior: when a step with the surface on leaves the unit
    /// exactly where it was, the same step runs again with the surface off, which moves it along the patrol's own points
    /// (the scene editor's route, laid on the walls) at its speed. A step whose ray hits is left as it is.
    /// </summary>
    public static class PatrolSurfaceFallback
    {
        public delegate void TickMethod(Obf_R.Obf_ff self, object dt);
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly TickMethod TickWrapper = OnTick;
        private static Obf_R.Obf_Ef _tick;
        private static bool _reported;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Func<string, Obf_R.Obf_ff> module)
        {
            try
            {
                _reported = false;
                var agent = module("lc_movement_pathlistagent");
                _tick = agent?.Obf_RuA<Obf_R.Obf_Ef>("TickLocalPatrol");
                if (_tick != null) agent.Obf_luA<string, TickMethod>("TickLocalPatrol", TickWrapper);
                Debug.Log("[Ro3Emu.PatrolSurfaceFallback] " + (_tick != null ? "hooked" : "not hooked (module missing)"));
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.PatrolSurfaceFallback] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void OnTick(Obf_R.Obf_ff self, object dt)
        {
            BornPatrolReverse.BeforeTick(self);
            var before = Snapshot(self);
            _tick.Obf_HuA(self, dt);
            try
            {
                if (!(self.Obf_RuA<object>("m_bMoveOnSurface") is bool onSurface) || !onSurface) return;
                if (!(self.Obf_RuA<object>("m_bLocalPatrolling") is bool patrolling) || !patrolling) return;
                if (before == null || before != Snapshot(self)) return;
                self.Obf_luA<string, object>("m_bMoveOnSurface", false);
                _tick.Obf_HuA(self, dt);
                self.Obf_luA<string, object>("m_bMoveOnSurface", true);
                if (!_reported) { _reported = true; Debug.Log("[Ro3Emu.PatrolSurfaceFallback] surface ray missed; patrol step taken along its points"); }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.PatrolSurfaceFallback] " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>The patrol index and position (m_kPatrolIndex, m_vNowPos), or null when they cannot be read.</summary>
        private static string Snapshot(Obf_R.Obf_ff self)
        {
            var pos = self.Obf_RuA<Obf_R.Obf_ff>("m_vNowPos");
            if (pos == null) return null;
            return self.Obf_RuA<object>("m_kPatrolIndex") + "|" + pos.Obf_RuA<object>("x") + "|" + pos.Obf_RuA<object>("y")
                + "|" + pos.Obf_RuA<object>("z");
        }
    }
}
