using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's allies (ReconstructedDungeons.ScriptedStep.Allies): NPCConfig units of the player's side, such as
/// the airship's Wule 5300086, Boya 5300087 and Eden Group Chief 5300088 (camp 1, AI 5300007, whose script is not in the
/// client), who fight the boarders and the Evil Boss beside the player in the official video. Rebuilt (inferred): each
/// ally goes for the nearest monster of the room that fights, swings its normal skill (the one of its NPCConfig _kSkills
/// with the shortest _iCD) within that skill's reach and its special one whenever that one's cooldown allows; the hit
/// lands halfway through the swing for a share of the monster's maximum HP (AllyHitPercent, AllySpecialPercent),
/// credited to a participant (CombatService.AllyHitMonsterAsync). Monsters keep fighting the participants: nothing in
/// the engine lets them target a scene unit, so the allies are never hurt. Allies stay from step to step while the next
/// step names them too, like the actors; a cue may walk them (DungeonService.Cues).
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>How often the allies look for something to do, in ms of the step's clock.</summary>
    private const int AllyTickMs = 300;
    /// <summary>(inferred) A normal hit takes this share (percent) of the monster's maximum HP; a special one more.</summary>
    private const int AllyHitPercent = 1, AllySpecialPercent = 3;

    /// <summary>Shows exactly the step's allies: one already shown with the same NPC stays, the others leave, the new ones
    /// enter with their NPCConfig _kRespawnSkill.</summary>
    private async Task SyncAlliesAsync(Attempt a, IReadOnlyList<ReconstructedDungeons.SceneActor> wanted)
    {
        var shown = a.Allies.ToList();
        var added = new List<ReconstructedDungeons.SceneActor>();
        foreach (var spec in wanted)
        {
            var same = shown.FirstOrDefault(u => u.NpcId == spec.NpcId);
            if (same == null) added.Add(spec);
            else shown.Remove(same);
        }
        foreach (var gone in shown)
        {
            a.Allies.Remove(gone);
            a.AllyBusyUntil.Remove(gone.Uid);
            a.AllySpecialAt.Remove(gone.Uid);
            await _instances.RemovePropAsync(a.Room, gone);
        }
        foreach (var spec in added)
        {
            if (a.Settled || a.Room.Closed) return;
            if (_catalog.ScenePoint(a.Plan.SceneEntry, spec.Point) is not { } at
                || SceneUnit(a, spec.NpcId, spec.Point, at with { X = at.X + spec.Dx, Z = at.Z + spec.Dz }) is not { } unit)
                continue;
            AddBornBuffs(unit);
            if (!await _instances.AddPropAsync(a.Room, unit)) continue;
            a.Allies.Add(unit);
            int entrance = await EntranceAsync(unit, spec.NpcId);
            a.AllyBusyUntil[unit.Uid] = MonsterService.NowMs + entrance;
        }
    }

    private void StartAllies(Attempt a)
    {
        int generation = a.StepGeneration;
        StepLater(a, generation, AllyTickMs, () => AllyTickAsync(a, generation), whileFighting: true);
    }

    private async Task AllyTickAsync(Attempt a, int generation)
    {
        if (a.Allies.Count == 0) return;
        StepLater(a, generation, AllyTickMs, () => AllyTickAsync(a, generation), whileFighting: true);
        long now = MonsterService.NowMs;
        foreach (var ally in a.Allies.ToArray())
        {
            if (ally.Removed || a.AllyBusyUntil.GetValueOrDefault(ally.Uid) > now) continue;
            var foe = a.Room.Monsters.Where(m => !m.Dead && m.Hp > 0 && !m.ScriptDriven)
                .MinBy(m => MonsterService.Distance(ally.X, ally.Z, m.X, m.Z));
            if (foe == null || NPCConfig.Get(_data, ally.NpcId) is not { } npc) continue;
            SkillConfig[] skills = [.. npc.Skills.Select(id => SkillConfig.Get(_data, id)).OfType<SkillConfig>()
                .Where(r => r.SkillId > 0).OrderBy(r => r.Cd)];
            if (skills.Length == 0) continue;
            var normal = skills[0];
            SkillConfig? special = skills.Length > 1 ? skills[1] : null;
            float distance = MonsterService.Distance(ally.X, ally.Z, foe.X, foe.Z);
            float reach = Math.Clamp(normal.DistanceMax / 100f, 1.5f, 4f);
            if (distance > reach)
            {
                // Up to the foe, stopping a little inside the reach.
                float speed = ally.SpeedCm / 100f, go = distance - reach * 0.7f;
                if (speed <= 0) continue;
                float tx = ally.X + (foe.X - ally.X) * go / distance, tz = ally.Z + (foe.Z - ally.Z) * go / distance;
                int ms = (int)Math.Max(1, go / speed * 1000);
                a.AllyBusyUntil[ally.Uid] = now + ms;
                await _world.MoveUnitAsync(ally, ally.Walk(tx, foe.Y, tz, now, now + ms));
                continue;
            }
            bool useSpecial = special is { Cd: > 0 } && a.AllySpecialAt.GetValueOrDefault(ally.Uid) <= now;
            var skill = useSpecial ? special.GetValueOrDefault() : normal;
            if (useSpecial) a.AllySpecialAt[ally.Uid] = now + skill.Cd;
            int action = (int)Math.Clamp(skill.ActionTime, 500, 3000);
            a.AllyBusyUntil[ally.Uid] = now + action + 300;
            ally.Face = MonsterService.Angle(ally.X, ally.Z, foe.X, foe.Z);
            await _world.ToUnitViewersAsync(ally, CombatService.UseInSkill, new UseInSkillInfo
            {
                SrcID = ally.Uid, SkillID = (int)skill.Id, TargetId = foe.Uid, TargetPos = foe.Pos, Face = ally.Face,
                SessionId = Interlocked.Increment(ref _cueSession), SkillMs = action, StartTime = now, PlayAct = true,
            });
            long amount = Math.Max(1, foe.Template.MaxHp * (useSpecial ? AllySpecialPercent : AllyHitPercent) / 100);
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(action / 2), () => AllyStrikeAsync(a, ally, foe, amount, (int)skill.Id));
        }
    }

    private Task AllyStrikeAsync(Attempt a, SceneActorUnit ally, MonsterUnit foe, long amount, int skill)
    {
        if (a.Settled || a.Room.Closed || ally.Removed || foe.Dead || foe.Hp <= 0
            || Present(a).FirstOrDefault(m => a.Loaded.Contains(m) && !m.SceneLoading && !m.Dead && m.Role != null) is not { } credit)
            return Task.CompletedTask;
        return _combat.AllyHitMonsterAsync(credit, foe, amount, ally.Uid, skill);
    }
}
