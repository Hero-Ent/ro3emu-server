using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The siege castle (TerritoryWarsService.SiegeObjects.cs): its gates, guardians, barricades and markers.</summary>
public sealed partial class SiegeTests
{
    private const int GatePoint = 36;
    private static readonly int[] OuterTowers = [38, 39, 40], InnerTowers = [41, 42, 43],
        Barricades = [321, 80, 81, 83, 90, 93, 97, 101, 103, 107], BuildingMarkers = [254, 255, 256, 273, 274, 308, 299];

    /// <summary>A seeded siege of 1001 with both sides loaded in: the attackers' bot is the fixture's GM account.</summary>
    private async Task<(BotClient Attack, BotClient Defense)> SeededSiege(string tag, long at = 25)
    {
        var attack = await Bot("SiegeGm");
        var defense = await Bot("SiegeGd" + tag);
        long ga = await Guild(attack, "SgGa" + tag), gd = await Guild(defense, "SgGd" + tag);
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + at);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Enter(attack, 1); await Enter(defense, 2);
        return (attack, defense);
    }

    private TerritoryWarsService.SiegeRun Run(BotClient bot) => Session(bot).SiegeBattle!;

    [ClientDataFact]
    public async Task TheCastleStandsAtItsPointsAndOnlyTheOtherCampHitsIt()
    {
        var (attack, defense) = await SeededSiege("A");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var map = Service<WorldData>().Map(2230101)!;
        int Npc(int point) => run.Objects[point].Template.NpcId;

        // NpcLevelPointRespawnConfig _iMapid 2230101: both gates, the six guardians, ten barricades and the markers.
        Assert.Equal((4900005, 4900005), (Npc(GatePoint), Npc(214)));
        Assert.All(OuterTowers, p => Assert.Equal(4901012, Npc(p)));
        Assert.All(InnerTowers, p => Assert.Equal(4901011, Npc(p)));
        Assert.All(Barricades, p => Assert.Equal(4900022, Npc(p)));
        Assert.All(BuildingMarkers, p => Assert.Equal(4903002, Npc(p)));
        Assert.Equal(4900009, Npc(44));
        // The revive point's row names point 45, a group id: it stands at UIModule_NpcData's 286.
        Assert.Equal(4900010, Npc(286));
        Assert.Equal((map.Points[286].X, map.Points[286].Z), (run.Objects[286].X, run.Objects[286].Z));
        Assert.Equal((4901021, 4901022, 4901022), (Npc(267), Npc(270), Npc(271)));
        Assert.DoesNotContain(269, run.Objects.Keys); // the contested point starts with the defenders
        Assert.DoesNotContain(46, run.Objects.Keys); // ladders, the ram and the archers are not spawned
        Assert.DoesNotContain(34, run.Objects.Keys);

        // Camp 2 objects for the attackers only; the camp 4 revive point and the unselectable markers for nobody.
        var wars = Service<TerritoryWarsService>();
        GameSession a = Session(attack), d = Session(defense);
        Assert.True(wars.CanHitObjective(a, run.Objects[38]));
        Assert.False(wars.CanHitObjective(d, run.Objects[38]));
        Assert.True(wars.CanHitObjective(a, run.Objects[321]));
        Assert.False(wars.CanHitObjective(a, run.Objects[286]) || wars.CanHitObjective(d, run.Objects[286]));
        Assert.False(wars.CanHitObjective(a, run.Objects[254]) || wars.CanHitObjective(d, run.Objects[254]));
        Assert.False(wars.CanHitObjective(d, run.Objects[270]) || wars.CanHitObjective(a, run.Objects[270]));

        // A barricade destroyed stays destroyed; a damaged guardian keeps its HP in the siege record.
        var barricade = run.Objects[321];
        await OnLoop(() => { barricade.Hp = 1; return Task.CompletedTask; });
        await OnLoop(async () =>
            await Service<SceneService>().TeleportInMapAsync(a, new MapPoint(barricade.X + 1.5f, barricade.Y, barricade.Z, 0)));
        await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = barricade.Uid });
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(attack, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(x => x.DstID == barricade.Uid && x.IsDead));
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        Assert.Contains(321, run.State.Destroyed);
        Assert.Same(barricade, run.Objects[321]); // not spawned again
    }
}
