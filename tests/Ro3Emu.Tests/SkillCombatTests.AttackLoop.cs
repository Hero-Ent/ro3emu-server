using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // A left click on a monster out of range: the client walks, casts the normal attack on reaching range and sends its
    // stop a moment later, a little further along the walk (in-game 22.10, any class). That stop is where the attack
    // started, not a walk away from it: the server-driven swings must go on.
    [ClientDataFact]
    public async Task AnAutoAttackStartedOnTheMoveSurvivesTheStopRightAfterIt()
    {
        await using var alice = await InWorld("Alice");
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 10_000_000; return Task.CompletedTask; });
        var start = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Position.Clone()));
        long role = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = start, Dir = 90, Face = 90, SpeedRate = 1 });
        await Task.Delay(200);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var stop = start.Clone();
        stop.X += 0.4f;
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = stop, Dir = 90, Face = 90, BStop = true });

        // The first swing is the cast itself; the loop's own swings carry new sessions.
        var swing = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SrcID == role && x.SessionId != accepted.SessionId);
        Assert.Contains(swing.Dsts, d => d.DstID == monster.Uid);
    }
}
