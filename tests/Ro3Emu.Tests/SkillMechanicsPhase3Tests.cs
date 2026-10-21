using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Phase 3 of the skill mechanics: attribute scaling, bounces, conditional branches.</summary>
public sealed partial class SkillCombatTests
{
    [Fact]
    public void DamageParamExtraScalesTheRatioWithAspdAndLevel()
    {
        static double Scale(string extra, long aspd, int level, long dex = 0)
        {
            using var doc = JsonDocument.Parse($"{{\"_kDamageParamExtra\": {extra}}}");
            return CombatService.StatScale(doc.RootElement, id => id == 115 ? aspd : id == 5 ? dex : 0, level);
        }
        // Rolling Cutter "PATK * «40»%*(«1»+ASPD*«0.5»%)": ASPD 100% -> x1.5; capped at 500% ASPD -> x3.5.
        Assert.Equal(1.5, Scale("[[50, 115, 10000, 500]]", 100, 1), 6);
        Assert.Equal(3.5, Scale("[[50, 115, 10000, 500]]", 800, 1), 6);
        Assert.Equal(1.3, Scale("[[30, 115, 10000]]", 100, 1), 6);
        // Heal "Each Base Level increases Healing Volume by «0.1»%": level 50 -> x1.05; DEX Resonance «0.1»%.
        Assert.Equal(1.05, Scale("[[1, 10, 0, 1]]", 100, 50), 6);
        Assert.Equal(1.08, Scale("[[1, 10, 5, 1, 100]]", 100, 50, dex: 80), 6);
        // Unknown layouts (Death Bound's reflect row, revive percentages) change nothing.
        Assert.Equal(1, Scale("[[1, 3, 0, 1, 1200, 1, 1200]]", 100, 50), 6);
        Assert.Equal(1, Scale("[[2, 5000, 101, 3]]", 100, 50), 6);
    }

    [ClientDataFact]
    public async Task EnvenomedBladeBouncesToTheEnemiesNearTheTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15105);
        var locked = await Target("alice", stationary: true);
        var near = await Target("alice", stationary: true);
        var nearer = await Target("alice", stationary: true);
        var far = await Target("alice", stationary: true);
        var origin = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            foreach (var m in new[] { locked, near, nearer, far }) { m.Hp = 50_000_000; m.Moving = false; }
            locked.X = origin.X; locked.Z = origin.Z + 1;
            nearer.X = origin.X + 2; nearer.Z = origin.Z + 1;   // 2 m from the target
            near.X = origin.X + 4; near.Z = origin.Z + 1;       // 2 m from nearer, 4 m from the target: reached by the second jump
            far.X = origin.X + 20; far.Z = origin.Z + 1;        // out of every jump's 3 m
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510501, TargetId = locked.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Task.Delay(2500);
        var struck = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .Where(h => h.SessionId == accepted.SessionId).SelectMany(h => h.Dsts.Select(d => d.DstID)).ToList();
        Assert.Equal([locked.Uid, nearer.Uid, near.Uid], struck);
        Assert.Equal(50_000_000, far.Hp);
    }
}
