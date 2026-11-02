using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Issue #121: a normal attack waits for the swing before it, so a burst of clicks does not hit at click speed. A press
/// during the swing is held and cast when the running skill allows it (CombatService.QueueCast, after its
/// _iInterruptedType), within CombatService.SwingJitterMs of the swing's end at the earliest.
/// </summary>
public sealed partial class SkillCombatTests
{
    private const int NoviceAttack = 1000001;
    private const int ArcherAttack = 1300001;

    [ClientDataFact]
    public async Task ANormalAttackWaitsForTheSwingBeforeIt()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });

        long end = await SwingAsync(alice, Session("alice"), NoviceAttack, target.Uid);
        await PressIsHeldUntilTheSwingEndsAsync(alice, NoviceAttack, target.Uid, end);

        // Turning to another target is held the same way: the client plays the swing out.
        var other = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { other.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = other.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task AnArcherNormalAttackWaitsForTheSwingBeforeIt()
    {
        await using var archer = await InWorld("Archer");
        await Gm("archer", "!job 3100");
        var target = await Target("archer", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });

        // HunterAttack has _iIsNormalAtk == 0, yet a press during its swing waits all the same.
        long end = await SwingAsync(archer, Session("archer"), ArcherAttack, target.Uid);
        await PressIsHeldUntilTheSwingEndsAsync(archer, ArcherAttack, target.Uid, end);
    }

    /// <summary>Casts <paramref name="skillId"/> and returns when its swing ends.</summary>
    private async Task<long> SwingAsync(BotClient bot, GameSession session, int skillId, long targetId)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetId = targetId });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        return await Loop.InvokeAsync(() => Task.FromResult(
            session.SkillCast!.StartMs + session.SkillCast.ChantMs + session.SkillCast.ActionMs));
    }

    /// <summary>A second press in the middle of the swing is not refused: it is answered once the swing ends.</summary>
    private static async Task PressIsHeldUntilTheSwingEndsAsync(BotClient bot, int skillId, long targetId, long end)
    {
        Assert.True(MonsterService.NowMs < end - CombatService.SwingJitterMs, "the test ran past the swing; nothing left to check");
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetId = targetId });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(MonsterService.NowMs >= end - CombatService.SwingJitterMs,
            $"the held press was cast {end - MonsterService.NowMs} ms before the swing ended");
    }
}
