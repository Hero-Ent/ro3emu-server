using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Cast rules the client relies on (walking, repeated presses), Double Attack and stealth.</summary>
public sealed partial class SkillCombatTests
{
    /// <summary>Every roll lands and every proc fires; damage stays a normal roll.</summary>
    private sealed class AlwaysProcRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 0.999;
        public override int Proc(int max) => 0;
    }

    [ClientDataFact]
    public async Task ARepeatedPressWaitsForTheRunningSwingInsteadOfCuttingIt()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var origin = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() => { target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 1; return Task.CompletedTask; });
        // Two presses of the basic attack back to back (a held key re-sends every 0.2 s).
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        var first = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        var second = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal((0, 0), (first.Result, second.Result));
        Assert.NotEqual(first.SessionId, second.SessionId);
        // Both swings land: the second was held until the first ended, it did not cancel it before its hit.
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == second.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillEnd)
            && SkillEndNotify.Parser.ParseFrom(e.Body) is { Type: SkillEndNotify.Types.SkillEndType.Interrupted } end && end.SessionId == first.SessionId);
    }

    [ClientDataFact]
    public async Task WalkingDuringACastDoesNotInterruptIt()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var s = Session("alice");
        var origin = s.Position.Clone();
        await Loop.InvokeAsync(() => { target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 1; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = origin.X + 0.6f, Y = origin.Y, Z = origin.Z }, SpeedRate = 1 });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
    }

    [ClientDataFact]
    public async Task DoubleAttackReleasesItsExtraHitWithADagger()
    {
        Combat.Rolls = new AlwaysProcRolls();
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100");
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        var origin = s.Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 1;
            s.Progress!.Skills[15002] = 10;
            s.Items.Add(new InvItem { Guid = 990001, Entry = 2010120, Stack = 1, Grid = 1 }); // a Dagger in the weapon slot
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1500001, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        // The swing itself lands twice: one hit message with 2 segments (the client shows both and their total).
        var swing = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Count > 0);
        Assert.Equal(2, swing.Split);
        Assert.Contains(swing.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1511501);
    }

    [ClientDataFact]
    public async Task CloakingHidesFromMonstersAndEndsAfterTheHitsItAllows()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        var monster = await Target("alice", stationary: true);
        var monsters = app.Services.GetRequiredService<MonsterService>();
        await Loop.InvokeAsync(async () =>
        {
            monster.Hp = 50_000_000; monster.Moving = false;
            await Buffs.AddAsync(s, 1500610, 10000, s.Role!.Id); // Disguise lv10: "revealed after taking damage «3» times"
            monsters.Engage(monster, s);
        });
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(BuffService.Hidden(s))));
        Assert.Null(await Loop.InvokeAsync(() => Task.FromResult(monster.Target))); // an ordinary monster does not see it
        await Loop.InvokeAsync(async () =>
        {
            for (int i = 0; i < 2; i++) foreach (var handler in Combat.TakenAsync) await handler(monster, s, 10);
        });
        Assert.Contains(await BuffsOf(s), b => b.ConfigId == 1500610);
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.TakenAsync) await handler(monster, s, 10); });
        Assert.DoesNotContain(await BuffsOf(s), b => b.ConfigId == 1500610);
        Assert.False(await Loop.InvokeAsync(() => Task.FromResult(BuffService.Hidden(s))));
    }
}
