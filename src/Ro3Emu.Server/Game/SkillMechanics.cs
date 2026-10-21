using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Per-skill mechanics the generic cast path cannot read from SkillConfig: when the damage lands, around what, in which
/// shape, and the side effects a description names (HP cost turned into a shield, a delayed follow-up, knockback).
/// They live in Reconstructed/skills/*.json (one file per class line, embedded), keyed by SkillConfig._iSkillID, each with
/// the description evidence in "why". Everything here is (inferred) from the skill's own description and timeline:
/// the original server's scripts are not in the client.
/// </summary>
public static class SkillMechanics
{
    public enum Anchor { Default, Caster, Origin, Target, Point }

    /// <summary>One damage moment: a template attack-point index or an explicit template time (seconds).</summary>
    /// <summary>
    /// One damage moment: a template attack-point index, a template time (seconds, scaled with the cast like the
    /// timeline), or <see cref="Ms"/> milliseconds after the cast started (unscaled, and it still lands after the cast
    /// ended or the player started another one: a lingering zone such as Magnus Exorcismus). With
    /// <see cref="AfterPoint"/> the milliseconds count from that attack point instead (where the zone appears, whatever
    /// the chant took).
    /// </summary>
    public sealed record Hit(int? Point, double? Time, int Branch, int? Split, int? Ms = null, int? AfterPoint = null);

    /// <summary>
    /// One skill's entry. Besides the hit moments, anchor and shape: <see cref="Offset"/> moves the area centre that many
    /// metres along the facing ("centered «1» meters in front of You"), <see cref="MatkPercent"/> adds MATK x n% to a
    /// physical hit ("Physical ATK*«50»%+Magic ATK*«50»%"), <see cref="AtkFromMatk"/> ("stat": "matk") makes a physical
    /// row scale with MATK ("Physical damage equal to MATK * «60»%"), and <see cref="Cleanse"/> lists the BuffConfig
    /// _iBuffID groups removed from the recipients ("Removes the [Poisoned] status") and <see cref="ResetCooldown"/> the
    /// skill groups whose cooldown the cast ends ("resetting the Cooldown of [Warg Hunt]").
    /// </summary>
    public sealed record Mechanic(
        int Group,
        int Mode,
        string Name,
        IReadOnlyList<Hit> Hits,
        bool NoDamage,
        Anchor At,
        float? Circle,
        (float Length, float Width)? Rect,
        (float Radius, float Degrees)? Fan,
        bool AimAtTarget,
        int? MaxTargets,
        IReadOnlyList<(int Buff, int Chance, int Ms)> TargetBuffs,
        IReadOnlyList<(int Buff, int Chance, int Ms)> SelfBuffs,
        float Knockback,
        float? DashStop,
        int SelfHpCostPercent,
        int? ShieldBuff,
        int ShieldMs,
        int? FollowUpSkill,
        int FollowUpDelayMs,
        int SelfHpLossPercent,
        IReadOnlyList<int> BonusRaces,
        IReadOnlyList<int> BonusElements,
        int BonusBranch,
        IReadOnlyList<int> OnlyRaces,
        IReadOnlyList<int> OnlyElements,
        float Pull,
        int? FollowUpGroup,
        bool DashToPoint,
        int? OnHitSkill,
        int OnHitChance,
        bool ExcludeTarget,
        Bounce? Chain = null,
        BranchCondition? BranchIf = null,
        float Offset = 0,
        int MatkPercent = 0,
        IReadOnlyList<int>? Cleanse = null,
        bool AtkFromMatk = false,
        IReadOnlyList<int>? ResetCooldown = null,
        float DashMinDistance = 0);

    /// <summary>
    /// After the main hit, the attack jumps <see cref="Count"/> times to a random other enemy within
    /// <see cref="Radius"/> m of the last one hit (Envenomed Blade "Then Launches a poisoned blade at a random enemy
    /// within «3» meters of the target ... up to «3» times"), each jump the same damage, <see cref="DelayMs"/> apart.
    /// </summary>
    public sealed record Bounce(float Radius, int Count, int DelayMs);

    /// <summary>
    /// Use <see cref="Branch"/> instead of the hit's own when the target's HP is at or below
    /// <see cref="TargetHpBelowBp"/> of its max (Sonic Blow "at or below «50»%"), or while the caster has a buff of
    /// group <see cref="CasterBuffGroup"/> (Steal "If cast while [Hiding]"); <see cref="Consume"/> removes that buff.
    /// </summary>
    public sealed record BranchCondition(int Branch, int TargetHpBelowBp, int? CasterBuffGroup, bool Consume);

    /// <summary>
    /// A buff that releases a skill (buffTriggers): on = "expire" (it ran out: Venom Splasher's detonation on the holder),
    /// "hitTaken"/"meleeHitTaken" (its player holder was hit: Shield Reflect, Parry), "damageDealt" (its holder dealt
    /// damage: Charge Up). The skill is cast by the buff's source (expire) or holder, at the holder (expire), the attacker
    /// (hit taken) or the hit enemy (damage dealt). <see cref="Chance"/> in basis points, <see cref="CooldownMs"/> per holder,
    /// <see cref="Consume"/> removes the buff when it fires, <see cref="MaxRange"/> (m) limits the attacker distance,
    /// <see cref="WhileBuff"/> (a _iBuffID) must also be on the holder (Recoil "while [Auto Guard] is active") and
    /// <see cref="Front"/> only lets attackers in the holder's front half count (Shield Defense "an enemy in front of you").
    /// </summary>
    public sealed record BuffTrigger(int Buff, string Name, string On, int? Skill, int? Group, int Chance, int CooldownMs, bool Consume, float MaxRange,
        int? LevelFrom = null, int? WhileBuff = null, bool Front = false, bool ByGroup = false);

    private static readonly Dictionary<int, BuffTrigger> Triggers = [];
    private static readonly Dictionary<int, BuffTrigger> GroupTriggers = [];

    /// <summary>The trigger of a buff row (key "<BuffConfig id>"), else of its whole buff group (key "group:<_iBuffID>": every level).</summary>
    public static BuffTrigger? TriggerFor(int buff, int buffGroup = 0)
    {
        _ = All.Value;
        return Triggers.TryGetValue(buff, out var t) ? t : buffGroup != 0 && GroupTriggers.TryGetValue(buffGroup, out var g) ? g : null;
    }

    public static IReadOnlyCollection<BuffTrigger> EveryTrigger { get { _ = All.Value; return Triggers.Values.Concat(GroupTriggers.Values).ToList(); } }

    private static readonly Lazy<IReadOnlyDictionary<long, Mechanic>> All = new(Load);

    private static readonly List<string> Errors = [];

    /// <summary>Files or entries that did not load (they are skipped so one bad entry cannot break combat); tests assert none.</summary>
    public static IReadOnlyList<string> LoadErrors { get { _ = All.Value; return Errors; } }

    public static Mechanic? For(int group, int mode = 0) => All.Value.TryGetValue(Key(group, mode), out var m) ? m : null;

    private static long Key(int group, int mode) => (long)group * 1000 + mode;

    /// <summary>The row's talent-mode entry ("group:mode") when there is one, else the group's.</summary>
    public static Mechanic? For(JsonElement row)
    {
        int group = (int)Data.GameData.Int(row, "_iSkillID"), mode = (int)Data.GameData.Int(row, "_iMode");
        return (mode != 0 ? For(group, mode) : null) ?? For(group);
    }

    public static IReadOnlyCollection<Mechanic> Every => (IReadOnlyCollection<Mechanic>)All.Value.Values;

    private static IReadOnlyDictionary<long, Mechanic> Load()
    {
        var result = new Dictionary<long, Mechanic>();
        var assembly = typeof(SkillMechanics).Assembly;
        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("Ro3Emu.Server.Reconstructed.skills.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("buffTriggers", out var triggers))
                    foreach (var entry in triggers.EnumerateObject())
                    {
                        try
                        {
                            bool byGroup = entry.Name.StartsWith("group:", StringComparison.Ordinal);
                            if (!int.TryParse(byGroup ? entry.Name[6..] : entry.Name, out int buff)) throw new InvalidDataException($"buff key '{entry.Name}' is not a BuffConfig id or group:<_iBuffID>");
                            var table = byGroup ? GroupTriggers : Triggers;
                            if (table.ContainsKey(buff)) throw new InvalidDataException($"buff trigger {entry.Name} is defined twice");
                            var e = entry.Value;
                            string on = e.GetProperty("on").GetString() ?? "";
                            if (on is not ("expire" or "hitTaken" or "meleeHitTaken" or "damageDealt")) throw new InvalidDataException($"buff trigger {buff}: on '{on}'");
                            int? skill = e.TryGetProperty("skill", out var sk) ? sk.GetInt32() : null, group = e.TryGetProperty("group", out var g) ? g.GetInt32() : null;
                            if (skill == null && group == null) throw new InvalidDataException($"buff trigger {buff}: needs skill or group");
                            table[buff] = new BuffTrigger(buff, e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", on, skill, group,
                                e.TryGetProperty("chance", out var c) ? c.GetInt32() : 10000, e.TryGetProperty("cooldownMs", out var cd) ? cd.GetInt32() : 0,
                                e.TryGetProperty("consume", out var cs) && cs.ValueKind == JsonValueKind.True, e.TryGetProperty("maxRange", out var mr) ? mr.GetSingle() : 0,
                                e.TryGetProperty("levelFrom", out var lf) ? lf.GetInt32() : null,
                                e.TryGetProperty("whileBuff", out var wb) ? wb.GetInt32() : null,
                                e.TryGetProperty("front", out var fr) && fr.ValueKind == JsonValueKind.True, byGroup);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
                        {
                            lock (Errors) Errors.Add($"{name}: buffTriggers {entry.Name}: {ex.Message}");
                        }
                    }
                if (!doc.RootElement.TryGetProperty("skills", out var skills)) continue;
                foreach (var entry in skills.EnumerateObject())
                {
                    try
                    {
                        // "group" or "group:mode" (a talent mode row overrides its group's entry).
                        var parts = entry.Name.Split(':');
                        if (!int.TryParse(parts[0], out int group) || parts.Length > 2) throw new InvalidDataException($"skill key '{entry.Name}' is not a SkillConfig _iSkillID[:_iMode]");
                        int mode = parts.Length == 2 && int.TryParse(parts[1], out int m) && m is > 0 and < 1000 ? m
                            : parts.Length == 2 ? throw new InvalidDataException($"skill key '{entry.Name}': bad mode") : 0;
                        if (result.ContainsKey(Key(group, mode))) throw new InvalidDataException($"skill {entry.Name} is defined twice");
                        result[Key(group, mode)] = Parse(group, mode, entry.Value, name);
                    }
                    catch (Exception e) when (e is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
                    {
                        lock (Errors) Errors.Add($"{name}: {entry.Name}: {e.Message}");
                    }
                }
            }
            catch (JsonException e)
            {
                lock (Errors) Errors.Add($"{name}: {e.Message}");
            }
        }
        foreach (string error in Errors) Console.Error.WriteLine($"[SkillMechanics] skipped {error}");
        return result;
    }

    private static Mechanic Parse(int group, int mode, JsonElement e, string file)
    {
        var hits = new List<Hit>();
        bool none = false;
        if (e.TryGetProperty("hits", out var h))
        {
            none = h.TryGetProperty("none", out var n) && n.ValueKind == JsonValueKind.True;
            int branch = h.TryGetProperty("branch", out var b) ? b.GetInt32() : 0;
            int? split = h.TryGetProperty("split", out var sp) ? sp.GetInt32() : null;
            if (h.TryGetProperty("points", out var points))
                foreach (var p in points.EnumerateArray()) hits.Add(new Hit(p.GetInt32(), null, branch, split));
            if (h.TryGetProperty("times", out var times))
                foreach (var t in times.EnumerateArray()) hits.Add(new Hit(null, t.GetDouble(), branch, split));
            if (h.TryGetProperty("every", out var every))
            {
                double from = every.GetProperty("from").GetDouble(), to = every.GetProperty("to").GetDouble();
                int count = every.GetProperty("count").GetInt32();
                for (int i = 0; i < count; i++) hits.Add(new Hit(null, from + (to - from) * i / Math.Max(1, count), branch, split));
            }
            int? after = h.TryGetProperty("afterPoint", out var ap) ? ap.GetInt32() : null;
            if (h.TryGetProperty("ms", out var msList))
                foreach (var v in msList.EnumerateArray()) hits.Add(new Hit(null, null, branch, split, v.GetInt32(), after));
            if (h.TryGetProperty("everyMs", out var everyMs))
            {
                int from = everyMs.GetProperty("from").GetInt32(), to = everyMs.GetProperty("to").GetInt32(), count = everyMs.GetProperty("count").GetInt32();
                for (int i = 0; i < count; i++) hits.Add(new Hit(null, null, branch, split, from + (to - from) * i / Math.Max(1, count), after));
            }
            if (h.TryGetProperty("final", out var final))
                hits.Add(new Hit(final.TryGetProperty("point", out var fp) ? fp.GetInt32() : null,
                    final.TryGetProperty("time", out var ft) ? ft.GetDouble() : null,
                    final.TryGetProperty("branch", out var fb) ? fb.GetInt32() : branch,
                    final.TryGetProperty("split", out var fs) ? fs.GetInt32() : null));
            if (!none && hits.Count == 0) throw new InvalidDataException($"{file}: skill {group} has 'hits' without points/times/every/final/none");
        }
        var at = Anchor.Default;
        if (e.TryGetProperty("anchor", out var anchor))
            at = anchor.GetString() switch
            {
                "caster" => Anchor.Caster, "origin" => Anchor.Origin, "target" => Anchor.Target, "point" => Anchor.Point,
                var other => throw new InvalidDataException($"{file}: skill {group} anchor '{other}'"),
            };
        float? circle = null; (float, float)? rect = null; (float, float)? fan = null;
        if (e.TryGetProperty("shape", out var shape))
        {
            if (shape.TryGetProperty("circle", out var c)) circle = c.GetSingle();
            if (shape.TryGetProperty("rect", out var r)) rect = (r[0].GetSingle(), r[1].GetSingle());
            if (shape.TryGetProperty("fan", out var f)) fan = (f[0].GetSingle(), f[1].GetSingle());
        }
        static List<(int, int, int)> Buffs(JsonElement e, string key) => e.TryGetProperty(key, out var list)
            ? list.EnumerateArray().Select(t => (t[0].GetInt32(), t[1].GetInt32(), t[2].GetInt32())).ToList() : [];
        return new Mechanic(
            group, mode,
            e.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            hits, none, at, circle, rect, fan,
            e.TryGetProperty("aim", out var aim) && aim.GetString() == "target",
            e.TryGetProperty("maxTargets", out var max) ? max.GetInt32() : null,
            Buffs(e, "targetBuffs"), Buffs(e, "selfBuffs"),
            e.TryGetProperty("knockback", out var kb) ? kb.GetSingle() : 0,
            e.TryGetProperty("dash", out var dash) ? dash.GetProperty("stop").GetSingle() : null,
            e.TryGetProperty("selfHpCost", out var cost) ? cost.GetProperty("percentMaxHp").GetInt32() : 0,
            e.TryGetProperty("selfHpCost", out var cost2) && cost2.TryGetProperty("shieldBuff", out var sb) ? sb.GetInt32() : null,
            e.TryGetProperty("selfHpCost", out var cost3) && cost3.TryGetProperty("shieldMs", out var sm) ? sm.GetInt32() : 0,
            e.TryGetProperty("followUp", out var follow) && follow.TryGetProperty("skill", out var fsk) ? fsk.GetInt32() : null,
            e.TryGetProperty("followUp", out var follow2) ? follow2.GetProperty("delayMs").GetInt32() : 0,
            e.TryGetProperty("selfHpLoss", out var loss) ? loss.GetProperty("percentMaxHp").GetInt32() : 0,
            Ints(e, "branchVs", "races"), Ints(e, "branchVs", "elements"),
            e.TryGetProperty("branchVs", out var vs) && vs.TryGetProperty("branch", out var vb) ? vb.GetInt32() : 1,
            Ints(e, "onlyVs", "races"), Ints(e, "onlyVs", "elements"),
            e.TryGetProperty("pull", out var pull) ? pull.GetSingle() : 0,
            e.TryGetProperty("followUp", out var follow3) && follow3.TryGetProperty("group", out var fg) ? fg.GetInt32() : null,
            e.TryGetProperty("dash", out var dash2) && dash2.TryGetProperty("to", out var dt) && dt.GetString() == "point",
            e.TryGetProperty("onHitCast", out var ohc) ? ohc.GetProperty("skill").GetInt32() : null,
            e.TryGetProperty("onHitCast", out var ohc2) && ohc2.TryGetProperty("chance", out var ohcc) ? ohcc.GetInt32() : 10000,
            e.TryGetProperty("excludeTarget", out var ex) && ex.ValueKind == JsonValueKind.True,
            e.TryGetProperty("bounce", out var bounce)
                ? new Bounce(bounce.GetProperty("radius").GetSingle(), bounce.GetProperty("count").GetInt32(),
                    bounce.TryGetProperty("delayMs", out var bd) ? bd.GetInt32() : 0)
                : null,
            e.TryGetProperty("branchIf", out var bif)
                ? new BranchCondition(bif.GetProperty("branch").GetInt32(),
                    bif.TryGetProperty("targetHpBelowBp", out var hb) ? hb.GetInt32() : 0,
                    bif.TryGetProperty("casterBuffGroup", out var cbg) ? cbg.GetInt32() : null,
                    bif.TryGetProperty("consume", out var bc) && bc.ValueKind == JsonValueKind.True)
                : null,
            e.TryGetProperty("offset", out var off) ? off.GetSingle() : 0,
            e.TryGetProperty("matkPercent", out var mp) ? mp.GetInt32() : 0,
            e.TryGetProperty("cleanse", out var cl) ? cl.EnumerateArray().Select(v => v.GetInt32()).ToList() : null,
            e.TryGetProperty("stat", out var st) && (st.GetString() is "matk" ? true : throw new InvalidDataException($"{file}: skill {group} stat '{st.GetString()}'")),
            e.TryGetProperty("resetCooldown", out var rc) ? rc.EnumerateArray().Select(v => v.GetInt32()).ToList() : null,
            e.TryGetProperty("dash", out var dash3) && dash3.TryGetProperty("minDistance", out var dmin) ? dmin.GetSingle() : 0);
    }

    private static List<int> Ints(JsonElement e, string block, string key) =>
        e.TryGetProperty(block, out var b) && b.TryGetProperty(key, out var list) ? list.EnumerateArray().Select(v => v.GetInt32()).ToList() : [];
}
