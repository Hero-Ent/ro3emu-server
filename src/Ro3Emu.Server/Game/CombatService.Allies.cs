using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scene ally's hit on a monster (DungeonService.Allies: the airship's Wule, Boya and Eden Group Chief fighting beside
/// the player). The ally is a scene unit, not a player: its hit is settled like a periodic tick, credited to a participant
/// (the kill, its rewards and the room's count are theirs), and shown as a hit from the ally's own unit.
/// </summary>
public sealed partial class CombatService
{
    internal async Task AllyHitMonsterAsync(GameSession s, MonsterUnit m, long amount, long sourceId, int skillId)
    {
        var role = s.Role;
        if (role == null || amount <= 0 || s.SceneLoading || s.Dead || s.WorldScene != m.WorldScene || m.Dead || m.Hp <= 0
            || !CanHitMonster(s, m)) return;
        var attachment = s.Attachment;
        long generation = s.SceneGeneration, scene = s.WorldScene;
        bool Current() => ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment) && !s.SceneLoading
            && s.SceneGeneration == generation && s.WorldScene == scene && _sessions.All().Contains(s);
        long before = m.Hp;
        m.Hp = Math.Max(0, before - amount);
        bool lethal = m.Hp == 0; // reserved before the awaited settlement, like a skill hit
        try
        {
            try { await SettleMonsterHitAsync(s, m, before, amount); }
            catch (Exception e) when (e is not EventSettlementOutcomeUnknownException)
            {
                _log.LogDebug(e, "Ally hit of {Source} on {Uid} not settled", sourceId, m.Uid);
                return;
            }
            var hit = new SkillHitInfo
            {
                SrcID = sourceId, SkillId = skillId, SessionId = ++_sessionIds, SrcElement = 1,
                ShowPoint = SkillConfig.Get(_data, skillId) is { ScriptShow: > 0 and var show } ? HitEffect(show) : 0,
                Dsts = { new DamageInfo { DstID = m.Uid, Damage = amount, DstElement = m.Template.Element, IsDead = m.Hp == 0 } },
            };
            foreach (var viewer in m.Viewers.Append(s).Distinct().ToArray()) await viewer.SendGameAsync(SkillHit, hit);
            if (m.Hp == 0 && !m.DeathDurable) return; // another hit is still committing that death
            await _world.ToUnitViewersAsync(m, AoiAttrChange, MonsterService.Hp(m));
            await FightingAsync(m);
            if (m.Hp > 0 || !lethal) return;
            _log.LogInformation("Ally {Source} killed {Monster} ({Npc}) for {Name}", sourceId, m.Uid, m.Template.NpcId, role.Name);
            await _monsters.KillAsync(m);
            foreach (var other in _sessions.All().Where(o => o.Attack?.Target == m))
            {
                other.Attack = null;
                await other.SendGameAsync(SkillEnd, new SkillEndNotify
                {
                    SrcID = other.Role!.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                    InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                });
            }
            await PublishDurableKillAsync(s, m, Current);
        }
        finally
        {
            if (lethal && m.Hp == 0 && m.DeathDurable)
            {
                if (!m.Dead)
                    try { await _monsters.KillAsync(m); }
                    catch (Exception e) { _log.LogError(e, "Could not finalize the ally kill of {Uid}", m.Uid); }
                await PublishDurableKillAsync(s, m, Current);
                await FinalizeLethalPublicationAsync(m);
            }
        }
    }
}
