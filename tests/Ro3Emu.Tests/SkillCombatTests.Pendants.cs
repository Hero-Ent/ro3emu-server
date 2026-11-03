using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Blitz Beat's falcon flight is EffectData 13102200 (m_eBulletType 3, empty m_kBulletName): the client flies the
    // owner's falcon only from a PendantShow display (type 5), and draws nothing for it from a 31120.
    [ClientDataFact]
    public async Task BlitzBeatSendsTheFalconToItsTarget()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1310205, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var flight = await Next(alice, CombatService.SkillEffect, SkillEffectDisplayNotify.Parser, e => e.EffectType == 5);
        Assert.Equal(13102200, flight.EffectId);
        Assert.Equal(monster.Uid, flight.TargetId);
        Assert.Equal(Session("alice").Role!.Id, flight.SourceId);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillBullet));
    }
}
