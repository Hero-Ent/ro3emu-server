using System;
using System.Text;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Diagnostics ("!client unit &lt;npcId&gt; [seconds]"): for a while, logs to Player.log the movement and born state of
    /// every client unit of that NPC config id, twice a second. Built to see why a born patrol
    /// (LC_State_Unit_Born.StateStart -> LC_Movement_PathListAgent.StartLocalPatrol / TickLocalPatrol) does not advance:
    /// the fields are the ones those functions read and write.
    /// </summary>
    public static class UnitTrace
    {
        private const float IntervalSeconds = 0.5f;
        private static long _npcId;
        private static float _until, _next;

        internal static void Arm(string args)
        {
            var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            _npcId = parts.Length > 0 && long.TryParse(parts[0], out long id) ? id : 0;
            float seconds = parts.Length > 1 && float.TryParse(parts[1], out float s) ? s : 20f;
            _until = Time.time + seconds;
            _next = 0;
            _deepDone = false;
            Debug.Log("[Ro3Emu.UnitTrace] watching npc " + _npcId + " for " + seconds + " s");
        }

        /// <summary>From UiTweaks.Tick (every 10th frame).</summary>
        internal static void Tick()
        {
            if (_npcId == 0 || Time.time < _next) return;
            if (Time.time > _until) { Debug.Log("[Ro3Emu.UnitTrace] done"); _npcId = 0; return; }
            _next = Time.time + IntervalSeconds;
            try { Trace(); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.UnitTrace] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Trace()
        {
            var global = LuaPatches.Global;
            var manager = global?.Obf_RuA<Obf_R.Obf_ff>("LC_UnitManager");
            var made = manager?.Obf_RuA<Obf_R.Obf_Ef>("GetAllUnits")?.Obf_HuA(manager);
            var units = made != null && made.Length > 0 ? made[0] as Obf_R.Obf_ff : null;
            if (units == null) { Debug.Log("[Ro3Emu.UnitTrace] no unit list"); return; }
            int seen = 0;
            for (long i = 1; units.Obf_ruA<long, object>(i) is Obf_R.Obf_ff unit; i++)
            {
                if (LuaPatches.ToLong(unit.Obf_RuA<object>("m_iNpcConfigID")) != _npcId) continue;
                seen++;
                Debug.Log("[Ro3Emu.UnitTrace] " + Describe(global, unit));
            }
            if (seen == 0) Debug.Log("[Ro3Emu.UnitTrace] npc " + _npcId + " not in the unit list");
        }

        private static string Describe(Obf_R.Obf_ff global, Obf_R.Obf_ff unit)
        {
            var sb = new StringBuilder();
            sb.Append("uuid=").Append(unit.Obf_RuA<object>("m_kUUID"));
            sb.Append(" valid=").Append(Call(unit, "IsValid"));
            sb.Append(" bornMs=").Append(unit.Obf_RuA<object>("m_iBornTimeMs"));
            var timer = global.Obf_RuA<Obf_R.Obf_ff>("TimerManagerIns");
            sb.Append(" sysTime=").Append(Call(timer, "GetSystemTime"));
            sb.Append(" mapId=").Append(global.Obf_LuA<object>("NowMapInfo.mapId"));
            var born = Call(unit, "GetRunTimeParameter", global.Obf_LuA<object>("enum_State.Born")) as Obf_R.Obf_ff;
            sb.Append(" born={duration=").Append(born?.Obf_RuA<object>("duration"))
              .Append(" localPatrolling=").Append(born?.Obf_RuA<object>("localPatrolling")).Append('}');
            var move = unit.Obf_RuA<Obf_R.Obf_ff>("m_kMovement");
            if (move == null) return sb.Append(" movement=nil").ToString();
            sb.Append(" move=").Append(move.Obf_RuA<object>("__cname"));
            foreach (var field in MoveFields) sb.Append(' ').Append(field).Append('=').Append(move.Obf_RuA<object>(field));
            var points = move.Obf_RuA<Obf_R.Obf_ff>("m_kPatrolPoints");
            sb.Append(" points=").Append(points == null ? "nil" : Count(points).ToString());
            sb.Append(" pos=").Append(Vector(move.Obf_RuA<Obf_R.Obf_ff>("m_vNowPos")));
            if (move.Obf_RuA<Transform>("transform") is { } transform) Probe(sb, transform);
            if (!_deepDone && points != null) { _deepDone = true; DeepProbe(points); }
            return sb.ToString();
        }

        private static bool _deepDone;
        private static readonly Vector3[] Axes = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left };

        /// <summary>
        /// Once per arm: for every patrol point, rays along the six axes (20 m, every layer) with back faces off and on,
        /// so the log shows where the surfaces around the route are and which way they face.
        /// </summary>
        private static void DeepProbe(Obf_R.Obf_ff points)
        {
            bool backfaces = Physics.queriesHitBackfaces;
            try
            {
                for (long i = 1; points.Obf_ruA<long, object>(i) is Obf_R.Obf_ff p; i++)
                {
                    var at = new Vector3(Convert.ToSingle(p.Obf_ruA<long, object>(1)), Convert.ToSingle(p.Obf_ruA<long, object>(2)),
                        Convert.ToSingle(p.Obf_ruA<long, object>(3)));
                    var sb = new StringBuilder("[Ro3Emu.UnitTrace] point ").Append(i).Append(' ').Append(at.ToString("F2"));
                    foreach (bool back in new[] { false, true })
                    {
                        Physics.queriesHitBackfaces = back;
                        sb.Append(back ? " | back:" : " | front:");
                        foreach (var axis in Axes)
                            sb.Append(' ').Append(axis.ToString("F0")).Append('=').Append(Hit(at, axis));
                    }
                    Debug.Log(sb.ToString());
                }
            }
            finally { Physics.queriesHitBackfaces = backfaces; }
        }

        private static string Hit(Vector3 origin, Vector3 direction) =>
            Physics.Raycast(origin, direction, out var hit, 20f, ~0)
                ? hit.collider.name.Replace("collider_sc_plo_", "") + "/" + LayerMask.LayerToName(hit.collider.gameObject.layer)
                  + "@" + hit.distance.ToString("F2") + "n" + hit.normal.ToString("F1")
                : "-";

        /// <summary>
        /// The ray of PS_Physics' surface move (LC_Movement_Base.GetMoveOnSurfaceInfo: from 2.5 m along the unit's up,
        /// 5 m back down it, UT_Layer.MASK_GROUND; a miss keeps the unit where it is), then the same with every layer and
        /// straight down, and the colliders around the unit.
        /// </summary>
        private static void Probe(StringBuilder sb, Transform transform)
        {
            Vector3 pos = transform.position, up = transform.up;
            sb.Append(" tpos=").Append(pos.ToString("F2")).Append(" up=").Append(up.ToString("F2"));
            sb.Append(" groundMask=").Append(UT_Layer.MASK_GROUND);
            sb.Append(" ray(up,ground)=").Append(Ray(pos + up * 2.5f, -up, UT_Layer.MASK_GROUND));
            sb.Append(" ray(up,all)=").Append(Ray(pos + up * 2.5f, -up, ~0));
            sb.Append(" ray(down,ground)=").Append(Ray(pos + Vector3.up * 2.5f, Vector3.down, UT_Layer.MASK_GROUND));
            sb.Append(" ray(down,all)=").Append(Ray(pos + Vector3.up * 2.5f, Vector3.down, ~0));
            var near = Physics.OverlapSphere(pos, 3f, ~0, QueryTriggerInteraction.Collide);
            sb.Append(" near=").Append(near.Length);
            for (int i = 0; i < near.Length && i < 6; i++)
                sb.Append(' ').Append(near[i].name).Append('/').Append(LayerMask.LayerToName(near[i].gameObject.layer))
                  .Append('/').Append(near[i].GetType().Name);
        }

        private static string Ray(Vector3 origin, Vector3 direction, int mask) =>
            Physics.Raycast(origin, direction, out var hit, 5f, mask)
                ? hit.collider.name + "/" + LayerMask.LayerToName(hit.collider.gameObject.layer) + "@" + hit.distance.ToString("F2")
                : "miss";

        private static readonly string[] MoveFields =
        {
            "m_bLocalPatrolling", "m_kPatrolIndex", "m_fMoveSpeed", "m_bNeedLateUpdate", "m_bMoveOnSurface",
            "m_bForceUpdate", "m_bIsMoving", "m_fWillDelayExitWalkStateTime",
        };

        private static object Call(Obf_R.Obf_ff self, string name, params object[] args)
        {
            var fn = self?.Obf_RuA<Obf_R.Obf_Ef>(name);
            if (fn == null) return "no " + name;
            var all = new object[args.Length + 1];
            all[0] = self;
            Array.Copy(args, 0, all, 1, args.Length);
            var made = fn.Obf_HuA(all);
            return made != null && made.Length > 0 ? made[0] : null;
        }

        private static int Count(Obf_R.Obf_ff list)
        {
            int n = 0;
            while (list.Obf_ruA<long, object>(n + 1) != null) n++;
            return n;
        }

        private static string Vector(Obf_R.Obf_ff v) => v == null
            ? "nil"
            : "(" + v.Obf_RuA<object>("x") + ", " + v.Obf_RuA<object>("y") + ", " + v.Obf_RuA<object>("z") + ")";
    }
}
