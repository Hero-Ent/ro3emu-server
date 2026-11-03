using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Hero Saga: a skill with no chant that may not be cast on the move (Double Strafe, _bMovement 0) holds the
    /// caster still while it plays, as a chanted one does: the Walk forbidden bit (4) stops the client's walk and turns it to
    /// the cast, and is released when the cast ends.</summary>
    [ClientDataFact]
    public async Task AnInstantLockSkillHoldsTheCasterStillWhileItPlays()
    {
        await using var archer = await InWorld("Archer");
        await PrepareArcherAsync(archer, "archer");
        var target = await Target("archer", stationary: true);
        await CastDoubleStrafeAsync(archer, target);
        Assert.Contains(archer.Inbox.Snapshot(), e => e.IsGame(CombatService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body).ForbidInfo is { Data: { } data } && (data.Values[0] & 4) != 0);
        await Next(archer, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.ForbidInfo?.Data != null && (x.ForbidInfo.Data.Values[0] & 4) == 0);
        Assert.Equal(0, await Loop.InvokeAsync(() => Task.FromResult(Session("archer").CastWalkLock)));
    }
}
