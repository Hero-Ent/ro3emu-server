using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The Maya story ant's born crawl (LC_State_Unit_Born: NoviceDungeonBornPatrolNPCID on PublicParamConfig
    /// NoviceDungeonBornPatrolID) runs LC_Movement_PathListAgent.StartLocalPatrol, which puts the unit on the patrol's first
    /// point and walks it to the last. Patrol 25 of this client's scene (sc_plot_yhcx_002_dg01) starts on the cave floor
    /// by the glowing things and ends up the wall at point 22, so the ant left the scene up the wall. Inferred from the
    /// original run (the ant comes down the wall toward the substance), not from the client: for that patrol only, the
    /// points are walked from the last to the first, and on to the substance. Other patrols are untouched.
    /// </summary>
    public static class BornPatrolReverse
    {
        public delegate void StartMethod(Obf_R.Obf_ff self, object patrolId);
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly StartMethod StartWrapper = OnStart;
        private static Obf_R.Obf_Ef _start, _setPosition, _fromPool;
        private static long _bornPatrol;
        private static Obf_R.Obf_Cf _env;
        private static Obf_R.Obf_ff _global;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_Cf env, Obf_R.Obf_ff global, Func<string, Obf_R.Obf_ff> module)
        {
            try
            {
                _env = env;
                _global = global;
                _bornPatrol = LuaPatches.ToLong(module("publicparamconfig")?.Obf_RuA<object>("NoviceDungeonBornPatrolID"));
                _fromPool = global.Obf_LuA<Obf_R.Obf_Ef>("Vector3.GetFromPool");
                var agent = module("lc_movement_pathlistagent");
                _start = agent?.Obf_RuA<Obf_R.Obf_Ef>("StartLocalPatrol");
                _setPosition = agent?.Obf_RuA<Obf_R.Obf_Ef>("__SetPosition");
                bool ready = _start != null && _setPosition != null && _fromPool != null && _bornPatrol > 0;
                if (ready) agent.Obf_luA<string, StartMethod>("StartLocalPatrol", StartWrapper);
                Debug.Log("[Ro3Emu.BornPatrolReverse] " + (ready ? "hooked, patrol " + _bornPatrol : "not hooked (module or config missing)"));
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.BornPatrolReverse] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void OnStart(Obf_R.Obf_ff self, object patrolId)
        {
            _start.Obf_HuA(self, patrolId);
            if (LuaPatches.ToLong(patrolId) != _bornPatrol) return;
            try { Reverse(self); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.BornPatrolReverse] " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>
        /// Where the crawl ends: scene editor point 29 of sc_plot_yhcx_002_dg01, by the glowing things, where the Deniro
        /// stands from the next step (inferred from the original run: the ant walks on from the foot of the wall to the
        /// substance; the patrol itself ends at the foot of the wall).
        /// </summary>
        private static readonly double[] Substance = { 87.67, 251.8214, 324.76 };

        /// <summary>Lua field on the movement: the Born state's duration still to be set, in seconds.</summary>
        private const string PendingDuration = "ro3emu_bornSeconds";

        /// <summary>
        /// Lua field on the movement: from this patrol index on (the step onto the cave floor and the walk to the
        /// substance), the crawl does not lay the unit on the surface. There the floor collider's faces point down
        /// (measured with "!client unit"), so a surface ray that hits turns the ant upside down under the floor.
        /// </summary>
        private const string FlatFrom = "ro3emu_flatFrom";

        /// <summary>
        /// The same state StartLocalPatrol leaves (m_kPatrolPoints, m_kPatrolIndex 2, the unit on point 1), on the points in
        /// reverse order and then on to the substance. The points table is the scene's own, so it goes into a new table.
        /// The Born state ends the crawl after its duration (10 s, set right after StartLocalPatrol), shorter than this
        /// route: the next tick sets it to the route's time (BeforeTick).
        /// </summary>
        private static void Reverse(Obf_R.Obf_ff self)
        {
            var points = self.Obf_RuA<Obf_R.Obf_ff>("m_kPatrolPoints");
            if (points == null) return;
            int count = 0;
            while (points.Obf_ruA<long, object>(count + 1) != null) count++;
            if (count < 2) return;
            var route = _env.Obf_wTA();
            for (int i = 0; i < count; i++) route.Obf_luA<long, object>(i + 1, points.Obf_ruA<long, object>(count - i));
            var end = _env.Obf_wTA();
            for (int i = 0; i < 3; i++) end.Obf_luA<long, object>(i + 1, Substance[i]);
            route.Obf_luA<long, object>(count + 1, end);
            self.Obf_luA<string, object>("m_kPatrolPoints", route);
            self.Obf_luA<string, object>("m_kPatrolIndex", 2L);
            var first = route.Obf_ruA<long, Obf_R.Obf_ff>(1);
            var at = _fromPool.Obf_HuA(first.Obf_ruA<long, object>(1), first.Obf_ruA<long, object>(2), first.Obf_ruA<long, object>(3));
            if (at != null && at.Length > 0) _setPosition.Obf_HuA(self, at[0]);
            double speed = Convert.ToDouble(self.Obf_RuA<object>("m_fMoveSpeed") ?? 0.0);
            double seconds = speed > 0 ? Length(route, count + 1) / speed + 0.5 : 0;
            if (seconds > 0) self.Obf_luA<string, object>(PendingDuration, seconds);
            self.Obf_luA<string, object>(FlatFrom, (long)count);
            Debug.Log("[Ro3Emu.BornPatrolReverse] patrol " + _bornPatrol + " walked from its last point to the substance ("
                + (count + 1) + " points, " + seconds.ToString("F1") + " s)");
        }

        /// <summary>From PatrolSurfaceFallback before each patrol step: the Born state's duration once after a reversal, and the flat walk on the floor.</summary>
        internal static void BeforeTick(Obf_R.Obf_ff self)
        {
            long flatFrom = LuaPatches.ToLong(self.Obf_RuA<object>(FlatFrom));
            if (flatFrom > 0 && LuaPatches.ToLong(self.Obf_RuA<object>("m_kPatrolIndex")) >= flatFrom)
            {
                self.Obf_luA<string, object>("m_bMoveOnSurface", false);
                self.Obf_luA<string, object>(FlatFrom, null);
            }
            if (!(self.Obf_RuA<object>(PendingDuration) is double seconds)) return;
            self.Obf_luA<string, object>(PendingDuration, null);
            try
            {
                var unit = self.Obf_RuA<Obf_R.Obf_ff>("m_kUnit");
                var get = unit?.Obf_RuA<Obf_R.Obf_Ef>("GetRunTimeParameter");
                var made = get?.Obf_HuA(unit, _global.Obf_LuA<object>("enum_State.Born"));
                var born = made != null && made.Length > 0 ? made[0] as Obf_R.Obf_ff : null;
                born?.Obf_luA<string, object>("duration", seconds);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.BornPatrolReverse] duration: " + e.GetType().Name + ": " + e.Message); }
        }

        private static double Length(Obf_R.Obf_ff route, int count)
        {
            double total = 0;
            for (int i = 1; i < count; i++)
            {
                var a = route.Obf_ruA<long, Obf_R.Obf_ff>(i);
                var b = route.Obf_ruA<long, Obf_R.Obf_ff>(i + 1);
                double dx = Axis(b, 1) - Axis(a, 1), dy = Axis(b, 2) - Axis(a, 2), dz = Axis(b, 3) - Axis(a, 3);
                total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return total;
        }

        private static double Axis(Obf_R.Obf_ff point, long i) => Convert.ToDouble(point.Obf_ruA<long, object>(i));
    }
}
