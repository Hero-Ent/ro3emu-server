using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Allied companions (CompanionUnit, a scene actor marked "companion" in Reconstructed/scripted-scenes.json): Payon
/// Forest's Boya (5300087) and Wule (5300086), NPCConfig camp 1 with their own _kSkills and AI 5300007, fight Eddga
/// (5300001) at the scene's second step. They walk up to the nearest room monster until a ready skill reaches it, and
/// back to their points when there is none; Eddga may answer them once no player holds him; a kill of theirs counts
/// like the player's.
/// </summary>
public sealed partial class DungeonTests
{
    private const long PayonForest = 1022001;
    private const int Eddga = 5300001, Wule = 5300086, Boya = 5300087;
    /// <summary>The scene's born point (sc_pay_fild10_001_pl01 m_kBornPoints 出生点).</summary>
    private const long BornPoint = 16;

    [ClientDataFact]
    public void OnlyAPlayerSideMonsterNpcWithItsOwnSkillsCanBeACompanion()
    {
        var data = Service<GameData>();
        var monsters = Service<MonsterService>();
        var wule = CompanionUnit.Build(data, monsters, Wule)!;
        // NPCConfig _kSkills with SkillConfig _iCD and _iDistanceMax; AIConfig 5300007 _iSearchTime, _iAttackTickTime, _iViewRange.
        Assert.Equal([5353901, 5353601], wule.Skills.Select(s => s.Id));
        Assert.Equal([15000, 2000], wule.Skills.Select(s => s.CooldownMs));
        Assert.Equal([6f, 4f], wule.Skills.Select(s => s.Range));
        Assert.Equal((100, 1000, 100f), (wule.SearchMs, wule.AttackTickMs, wule.ViewRange));
        // The monster template: camp 1, level 20, HP from _iAttribute and _kAttriScale.
        Assert.Equal((1, 20), (wule.Template.Camp, wule.Template.Level));
        Assert.Equal(monsters.Template(Wule)!.MaxHp, wule.Template.MaxHp);
        Assert.NotNull(CompanionUnit.Build(data, monsters, Boya));
        Assert.Null(CompanionUnit.Build(data, monsters, Eddga)); // camp 2, no skills of its own
        Assert.Null(CompanionUnit.Build(data, monsters, 5300080)); // the hostile Wule, camp 3

        // Payon Forest's Eddga step shows the pair as companions, not as scene actors.
        var plan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = PayonForest })!;
        var step = plan.Waves[1].Step!;
        Assert.Equal([(Wule, 21L), (Boya, 23L)], step.Companions.Select(c => (c.NpcId, c.Point)));
        Assert.Empty(step.Actors);
        // A companion row that does not qualify refuses the scene.
        ChangeRow("NPCConfig", Boya.ToString(System.Globalization.CultureInfo.InvariantCulture), row => row["_kSkills"] = new JsonArray());
        Assert.Null(Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = PayonForest }));
    }

    [ClientDataFact]
    public async Task BoyaAndWuleFightEddgaAndTheirKillWinsTheStep()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        var map = Service<WorldData>().Map(PayonForest)!;
        Assert.Equal([Wule, Boya], companions.Select(c => c.NpcId).Order());
        Assert.All(companions, c => Assert.Equal((map.Points[c.Point].X, map.Points[c.Point].Z), (c.HomeX, c.HomeZ)));
        var wule = companions.Single(c => c.NpcId == Wule);
        long me = Session(alice).Role!.Id;
        var born = map.Points[BornPoint]; // about 20 m from either companion's point: Alice is not beside them
        Assert.All(companions, c => Assert.True(MonsterService.Distance(born.X, born.Z, c.HomeX, c.HomeZ) > 15));

        // A player cannot target a companion: its cast finds no monster there, and nothing hits it.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = wule.Uid });
        await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);

        // Alice stays at the born point; Eddga comes for her, and the pair walk up to him (31081) and hit him.
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => companions.Any(c => c.Uid == m.Uid) && m.MovementInfo.MoveType == MovementType.MoveTypeWalk);
        var cast = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => companions.Any(c => c.Uid == u.SrcID) && u.TargetId == eddga.Uid);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid && d.Damage > 0));
        var caster = companions.Single(c => c.Uid == cast.SrcID);
        Assert.True(await OnLoopValue(() => MonsterService.Distance(caster.X, caster.Z, caster.HomeX, caster.HomeZ) > 1),
            "the companion cast from where it walked to");
        Assert.True(await OnLoopValue(() => MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, born.X, born.Z) < 1));
        Assert.True(await OnLoopValue(() => eddga.Hp < eddga.Template.MaxHp));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == me && h.Dsts.Any(d => d.DstID == wule.Uid));
        Assert.Equal(wule.Template.MaxHp, await OnLoopValue(() => wule.Hp));

        // With no living monster in the room, each companion walks back to its point (its HP restored after).
        long eddgaHp = await OnLoopValue(() => eddga.Hp);
        await OnLoop(() => { eddga.Hp = 0; return Task.CompletedTask; });
        await Until(() => companions.All(c => MonsterService.Distance(c.X, c.Z, c.HomeX, c.HomeZ) < 0.5f), "the pair are back at their points");
        await OnLoop(() => { eddga.Hp = eddgaHp; return Task.CompletedTask; });

        // A companion's kill counts like the player's: the step completes, the run is won, the kill is Alice's.
        await OnLoop(() => { eddga.Hp = 1; return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid && d.IsDead));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        var attempt = Session(alice).DungeonAttempt!;
        Assert.Equal(1, attempt.Stats[me].KillNpcData[Eddga].LongValue);
        Assert.Equal(attempt.Spawned, attempt.Killed.Count); // the companions are not room monsters
    }

    [ClientDataFact]
    public async Task EddgaFightsBackAtACompanionThatFallsForGood()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid));

        // No player holds Eddga any more (as when she leaves his view): he turns to the companion that hit him (inferred).
        await OnLoop(() => { eddga.Target = null; return Task.CompletedTask; });
        var answer = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == eddga.Uid && h.Dsts.Any(d => companions.Any(c => c.Uid == d.DstID) && d.Damage > 0));
        var struck = companions.Single(c => answer.Dsts.Any(d => d.DstID == c.Uid));
        Assert.True(await OnLoopValue(() => struck.Hp < struck.Template.MaxHp));

        // At 0 HP it dies and leaves for good: no respawn.
        await OnLoop(() => { struck.Hp = 1; return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == eddga.Uid && h.Dsts.Any(d => d.DstID == struck.Uid && d.IsDead));
        await Until(() => !Session(alice).DungeonAttempt!.Companions.Contains(struck)
            && !Service<World>().AllUnits().Contains(struck), "the fallen companion is removed");
        Assert.True(struck.Dead);

        // The step still completes on Eddga's death.
        await Kill(alice, eddga);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.DoesNotContain(struck, Session(alice).DungeonAttempt!.Companions);
    }

    /// <summary>Payon Forest at its Eddga step: the standoff point reached, Alice back at the born point, Eddga spawned
    /// and engaged, the pair shown.</summary>
    private async Task<(MonsterUnit Eddga, IReadOnlyList<CompanionUnit> Companions)> EddgaStep(BotClient alice)
    {
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10102200"); // Payon Forest's condition [1101, 10102200]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = PayonForest }));
        long instance = await Load(alice, PayonForest);
        await Stage(alice, 1, quest: 1);
        var standoff = Service<WorldData>().Map(PayonForest)!.Points[22];
        await Gm(alice, FormattableString.Invariant($"!actors walk {standoff.X} {standoff.Z}"));
        await Stage(alice, 2, quest: 2);
        var born = Service<WorldData>().Map(PayonForest)!.Points[BornPoint];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), born));
        var eddga = (await WaveIn(instance, 1)).Single();
        Assert.Equal(Eddga, eddga.Template.NpcId);
        CompanionUnit[] shown = [];
        await Until(() => (shown = [.. Session(alice).DungeonAttempt?.Companions ?? []]).Length == 2, "the pair is shown");
        return (eddga, shown);
    }

    private async Task<T> OnLoopValue<T>(Func<T> read) => await Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await OnLoopValue(condition)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(what);
    }
}
