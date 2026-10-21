using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
            // Field monsters that roam by would be fair bounce targets: move them out of the way.
            var mine = new[] { locked.Uid, near.Uid, nearer.Uid, far.Uid };
            foreach (var other in app.Services.GetRequiredService<World>().Map(Session("alice").WorldScene).Units.OfType<MonsterUnit>()
                         .Where(m => !mine.Contains(m.Uid) && MathF.Abs(m.X - origin.X) < 40 && MathF.Abs(m.Z - origin.Z) < 40))
            { other.X = origin.X + 200; other.Moving = false; }
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

    [ClientDataFact]
    public void EveryAxeTornadoAndHighEnergyImpactModeUsesItsSchedule()
    {
        var data = app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
        foreach (var row in data.Table("SkillConfig").Values)
        {
            long group = Ro3Emu.Server.Data.GameData.Int(row, "_iSkillID");
            if (group == 16304) { Assert.True(CombatService.IsSpin(row)); Assert.True(CombatService.SpinTicks(row) > 0); }
            if (group == 16307) Assert.True(CombatService.IsLeapSlam(row));
        }
    }

    [ClientDataFact]
    public async Task VulcanArmLoadsFirepowerOnlyAtFifteenStacks()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16313);
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        var origin = s.Position.Clone();
        await Loop.InvokeAsync(() => { target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 1; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1631301, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Count > 0);
        await Task.Delay(200);
        var first = await BuffsOf(s);
        Assert.Contains(first, b => b.ConfigId == 1631301 && b.PileCount == 1); // the counter grows...
        Assert.DoesNotContain(first, b => b.ConfigId == 1631701);               // ...and Firepower Loaded waits
        await Loop.InvokeAsync(async () =>
        {
            await Buffs.SetPileCountAsync(s, 1631301, 15000, s.Role!.Id, 14);
            s.SkillCds.Clear(); s.SkillModeCds.Clear();
        });
        await Task.Delay(2500); // the first cast's action time (a press before it ends would be held)
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1631301, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Count > 0);
        await Task.Delay(200);
        var loaded = await BuffsOf(s);
        Assert.DoesNotContain(loaded, b => b.ConfigId == 1631301);
        Assert.Contains(loaded, b => b.ConfigId == 1631701 && b.PileCount == 3);
    }

    /// <summary>A snapshot of the buffs, taken on the game loop that mutates them.</summary>
    private Task<List<ActiveBuff>> BuffsOf(GameSession s) => Loop.InvokeAsync(() => Task.FromResult(s.Buffs.ToList()));
}
