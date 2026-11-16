using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>SkillMechanics buffTriggers / periodic zones: skills released by a buff (expiry, hit taken, damage dealt) and lingering areas.</summary>
public sealed partial class SkillCombatTests
{
    private BuffService Buffs => _app.Services.GetRequiredService<BuffService>();

    [ClientDataFact]
    public async Task VenomSplasherDetonatesAroundTheTargetWhenTheBombRunsOut()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15110);
        var bombed = await Target("alice", stationary: true);
        var beside = await Target("alice", stationary: true);
        var origin = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            foreach (var m in new[] { bombed, beside }) { m.Hp = 50_000_000; m.Moving = false; }
            bombed.X = origin.X; bombed.Z = origin.Z + 1;
            beside.X = origin.X + 2; beside.Z = origin.Z + 1; // 2 m from the bombed enemy: inside the 3 m blast
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1511001, TargetId = bombed.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Task.Delay(1000);
        Assert.Equal(50_000_000, bombed.Hp); // planting deals nothing
        var blast = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511301);
        var ids = blast.Dsts.Select(d => d.DstID).ToHashSet();
        Assert.Contains(bombed.Uid, ids);
        Assert.Contains(beside.Uid, ids);
        Assert.True(bombed.Hp < 50_000_000);
    }

    // Parry's Auto Counter is the upstream's hit reaction now (CombatService.HitReactions; SkillCombatTests.AClassBuffStrikesBackOnceWithinItsCooldown).

    [ClientDataFact]
    public async Task ChargeUpIsSpentByTheNextDamageNotByTheSkillThatGrantedIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1410"); await Teach("alice", 11602);
        var s = Session("alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var target = await Target("alice", stationary: true);
        var origin = s.Position.Clone();
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 1;
            await Buffs.AddAsync(s, 1160401, 8000, s.Role!.Id);
            // Heavy Cannon Shot's own hit keeps it.
            foreach (var handler in Combat.DealtAsync) await handler(s, target, data.Row("SkillConfig", 1160201)!.Value, 100);
        });
        Assert.Contains(await BuffsOf(s), b => b.ConfigId == 1160401);
        await Loop.InvokeAsync(async () =>
        {
            foreach (var handler in Combat.DealtAsync) await handler(s, target, data.Row("SkillConfig", 1160101)!.Value, 100);
        });
        Assert.DoesNotContain(await BuffsOf(s), b => b.ConfigId == 1160401);
        var strike = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1162201);
        Assert.Contains(strike.Dsts, d => d.DstID == target.Uid);
    }

    [ClientDataFact]
    public async Task MagnusExorcismusTicksEightTimesAfterTheCast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109);
        var target = await Target("alice", stationary: true);
        var origin = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() => { target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 2; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1410901, TargetPos = target.Pos.Clone(), Face = 0 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        DateTime first = default;
        for (int tick = 0; tick < 8; tick++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Count > 0);
            Assert.Contains(hit.Dsts, d => d.DstID == target.Uid);
            if (tick == 0) first = DateTime.UtcNow;
        }
        Assert.InRange((DateTime.UtcNow - first).TotalMilliseconds, 3000, 6000); // 7 gaps of 0.5 s
    }
}
