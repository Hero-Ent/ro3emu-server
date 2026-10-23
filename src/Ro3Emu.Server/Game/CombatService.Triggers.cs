using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills that something else releases (SkillMechanics buffTriggers / onHitCast): a buff running out (Venom Splasher's
/// detonation), its holder being hit (Shield Reflect, Parry, Death Bound) or dealing damage (Charge Up), and a skill's
/// chance to cast another on each enemy it hits (Dragon Breath's Burn). The released skill is its own independent cast
/// (timeline + hits) aimed at the enemy concerned; it never re-triggers damage-dealt triggers itself.
/// </summary>
public sealed partial class CombatService
{
    private readonly Dictionary<(long Holder, int Buff), long> _triggerReady = new();

    private void WireSkillTriggers()
    {
        _buffs.Expired.Add(BuffExpiredAsync);
        TakenAsync.Add(HitTakenTriggersAsync);
        DealtAsync.Add(DamageDealtTriggersAsync);
    }

    private bool TriggerReady(long holder, SkillMechanics.BuffTrigger trigger)
    {
        long now = Now;
        if (_triggerReady.TryGetValue((holder, trigger.Buff), out long ready) && ready > now) return false;
        if (trigger.Chance < 10000 && Rolls.Proc(10000) >= trigger.Chance) return false;
        if (trigger.CooldownMs > 0) _triggerReady[(holder, trigger.Buff)] = now + trigger.CooldownMs;
        return true;
    }

    /// <summary>The trigger's row: a fixed one, or its group at the level of the skill that granted the buff (else 1).</summary>
    private int? TriggerRow(SkillMechanics.BuffTrigger trigger, GameSession caster) =>
        trigger.Skill ?? (trigger.Group is int g ? FollowUpRow(g, caster.Progress is { } p ? Math.Max(1, _skills.Level(p, trigger.LevelFrom ?? g)) : 1) : null);

    private SkillMechanics.BuffTrigger? TriggerOf(ActiveBuff buff) =>
        SkillMechanics.TriggerFor(buff.ConfigId, _data.Row("BuffConfig", buff.ConfigId) is { } row ? (int)GameData.Int(row, "_iBuffID") : 0);

    private async Task BuffExpiredAsync(GameSession? holder, WorldUnit? unit, ActiveBuff buff)
    {
        if (TriggerOf(buff) is not { On: "expire" } trigger) return;
        // Cast by whoever applied the buff, at the holder: a detonation on a monster, or the holder's own release.
        var caster = holder ?? _sessions.All().FirstOrDefault(s => s.Role?.Id == buff.SourceId);
        if (caster?.Role == null || caster.Dead || caster.SceneLoading) return;
        var monster = unit as MonsterUnit;
        if (monster != null && (monster.Dead || monster.WorldScene != caster.WorldScene)) return;
        if (!TriggerReady(caster.Role.Id, trigger) || TriggerRow(trigger, caster) is not int row) return;
        await CastTriggeredAsync(caster, row, monster, monster?.Pos ?? caster.Position);
    }

    private async Task HitTakenTriggersAsync(MonsterUnit attacker, GameSession holder, long damage)
    {
        if (holder.Role == null || holder.Dead || damage <= 0) return;
        bool melee = attacker.Template.AttackRange <= 4f; // (inferred) a monster swinging from melee reach
        foreach (var buff in holder.Buffs.Where(b => b.ExpireMs > Now).ToArray())
        {
            if (TriggerOf(buff) is not { On: "hitTaken" or "meleeHitTaken" } trigger) continue;
            if (trigger.On == "meleeHitTaken" && !melee) continue;
            if (trigger.MaxRange > 0 && MonsterService.Distance(attacker, holder) > trigger.MaxRange) continue;
            if (trigger.Front && !InFront(holder, attacker.Pos)) continue;
            if (trigger.WhileBuff is int needed && !HasBuffGroup(holder, needed)) continue;
            if (!TriggerReady(holder.Role.Id, trigger) || TriggerRow(trigger, holder) is not int row) continue;
            if (trigger.Consume) await _buffs.RemoveAsync(holder, buff);
            await CastTriggeredAsync(holder, row, attacker, attacker.Pos);
        }
    }

    /// <summary>The point lies in the holder's front half (within 90° of the way it faces).</summary>
    internal static bool InFront(GameSession holder, Vec3 point)
    {
        float toPoint = MonsterService.Angle(holder.Position.X, holder.Position.Z, point.X, point.Z);
        double delta = Math.Abs(((toPoint - holder.Face) % 360 + 540) % 360 - 180);
        return delta <= 90;
    }

    private bool HasBuffGroup(GameSession holder, int buffGroup) =>
        holder.Buffs.Any(b => b.ExpireMs > Now && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == buffGroup);

    private async Task DamageDealtTriggersAsync(GameSession holder, MonsterUnit target, JsonElement row, long damage)
    {
        // Read before any await: ApplyEffectsAsync sets it right before calling the handlers for this hit.
        bool fromRelease = _dealingIndependent;
        if (holder.Role == null || holder.Dead || damage <= 0 || fromRelease) return;
        foreach (var buff in holder.Buffs.Where(b => b.ExpireMs > Now).ToArray())
        {
            if (TriggerOf(buff) is not { On: "damageDealt" } trigger) continue;
            // "The next time you deal DMG": the skill that grants the buff does not spend it with its own hits.
            if (Tuples(row, "_kBuff1").Any(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 && t[0].GetInt32() == buff.ConfigId)) continue;
            if (trigger.WhileBuff is int needed && !HasBuffGroup(holder, needed)) continue;
            if (!TriggerReady(holder.Role.Id, trigger) || TriggerRow(trigger, holder) is not int released) continue;
            if (trigger.Consume) await _buffs.RemoveAsync(holder, buff);
            await CastTriggeredAsync(holder, released, target, target.Pos);
        }
        // A skill's own chance to release another on each enemy it hits (Dragon Breath "«50»% Chance to Burn").
        if (SkillMechanics.For(row) is { OnHitSkill: int onHit } mechanic && (mechanic.OnHitChance >= 10000 || Rolls.Proc(10000) < mechanic.OnHitChance))
            await CastTriggeredAsync(holder, onHit, target, target.Pos);
    }

    /// <summary>Hero Saga's own Double Attack (below) stays off: the upstream passive rule (d0cf8b47) handles it with
    /// the talents (decision D1 of the 2026-10-22 upstream sync). Kept so it can be compared or restored.</summary>
    internal static bool LegacyDoubleAttack => false;

    /// <summary>
    /// Double Attack (15002): "When a Dagger is equipped, each Basic Attack has a «1..10»% Chance to deal «1» Extra hits
    /// of ... PATK * «50»%". The chance is BuffConfig group 15002 at the learned level (_kLogicData 100..1000 bp;
    /// SpecialEffect 16530012 raises "the trigger Chance of [Double Attack]" through that buff). On a proc this returns
    /// the extra hit's row 1511501 (ThiefAttack2, P1 50%): its damage is added to the same swing, shown as two hits and
    /// their total like a two-hit skill (the player's report of the official display), not as a separate skill number.
    /// </summary>
    private JsonElement? DoubleAttackRolls(GameSession s, JsonElement row) =>
        GameData.Int(row, "_iIsNormalAtk") == 1 && s.Progress is { } progress
        && _skills.Level(progress, DoubleAttackGroup) is var level and > 0 && WieldsWeapon(s, DaggerSubType)
        && Rolls.Proc(10000) < DoubleAttackChanceBp(level)
            ? _data.Row("SkillConfig", DoubleAttackRow) : null;

    private const int DoubleAttackRow = 1511501, DaggerSubType = 1; // DoubleAttackGroup (15002): CombatService.Passives
    private Dictionary<int, int>? _doubleAttackBp;

    private int DoubleAttackChanceBp(int level) => (_doubleAttackBp ??= _data.Table("BuffConfig").Values
            .Where(r => GameData.Int(r, "_iBuffID") == DoubleAttackGroup)
            .GroupBy(r => (int)GameData.Int(r, "_iLevel"))
            .ToDictionary(g => g.Key, g => (int)Numbers(g.First(), "_kLogicData").FirstOrDefault()))
        .GetValueOrDefault(level);

    /// <summary>An equipped weapon (ItemConfig _iType 5) of that _iSubType (1 Dagger, 12 Katar).</summary>
    private bool WieldsWeapon(GameSession s, int subType) => s.Items.Any(i => i.Grid > 0 && _data.Row("ItemConfig", i.Entry) is { } item
        && GameData.Int(item, "_iType") == 5 && GameData.Int(item, "_iSubType") == subType);

    /// <summary>The hit being reported to DealtAsync comes from a released (independent) cast: it must not release more.</summary>
    private bool _dealingIndependent;

    /// <summary>An independent cast of <paramref name="rowId"/> by <paramref name="caster"/> at an enemy/point (no cost, no cooldown);
    /// <paramref name="extra"/> plays it as an extra timeline (31121) beside whatever the caster is doing.</summary>
    private async Task CastTriggeredAsync(GameSession caster, int rowId, MonsterUnit? target, Vec3 point, bool extra = false)
    {
        if (caster.Role == null || _data.Row("SkillConfig", rowId) is not { } row) return;
        await StartCastAsync(caster, row, new CastSkillReq
        {
            SkillId = rowId, TargetId = target?.Uid ?? 0, TargetPos = point.Clone(), Face = caster.Face,
            IsExtraSkill = extra
        }, ++_sessionIds, independent: true);
    }
}
