using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// SkillConfig._bMovement is enum_SkillLockMoveType (Lua_GameDefine.lua: 0 Lock, 1 UnLock, 2 WalkUnLock), "may the
/// caster walk while casting" (LC_Skill_RunTimeContext CheckCanSkillMove), never a displacement. Displacements are the
/// m_kPoint.m_bMovement EffectData of the skill's show, which 31112 must name (SkillStartMove ignores effectId 0).
/// </summary>
public sealed partial class SkillCombatTests
{
    private CombatService Combat => _app.Services.GetRequiredService<CombatService>();
    private static string Script(System.Text.Json.JsonElement row) =>
        row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    [ClientDataFact]
    public void DisplacementEffectsAreTheShowsMovementEffectData()
    {
        Assert.Equal((1510403, 180), Combat.MovementEffect(15104));    // BackSlide
        Assert.Equal((1300504, 100), Combat.MovementEffect(13005));    // ArrowRepel
        Assert.Equal((1332606, 160), Combat.MovementEffect(13326));    // WolvesDash
        Assert.Equal((1631405, 200), Combat.MovementEffect(16314));    // PileBunker knockback, not 1631406
        Assert.Equal((11308105, 250), Combat.MovementEffect(113081));  // PhantomThrust pull (8-digit show)
        Assert.Equal((1542903, 180), Combat.MovementEffect(15429));    // RetreatingShadows
        Assert.Equal((0, 0), Combat.MovementEffect(15112));            // Disguise: _bMovement 1, no displacement
        var data = _app.Services.GetRequiredService<GameData>();
        var skills = data.Table("SkillConfig").Values.ToList();
        Assert.Equal((1210210, 70), Combat.MovementEffect(12102));     // Jupitel Thunder knockback
        // Every displacement rule meets a skill whose show (or the show it borrows) has its effect.
        foreach (var (script, _) in CombatService.Displacements)
            Assert.Contains(skills, r => Script(r) == script
                && Combat.MovementEffect(CombatService.BorrowedMovementShows.GetValueOrDefault(script, GameData.Int(r, "_iScriptShow"))).Id != 0);
        // A show is borrowed only for a rule whose own show has no displacement effect (Spear Stab, Sightrasher).
        foreach (var (script, _) in CombatService.BorrowedMovementShows)
        {
            Assert.True(CombatService.Displacements.ContainsKey(script));
            Assert.All(skills.Where(r => Script(r) == script), r => Assert.Equal(0, Combat.MovementEffect(GameData.Int(r, "_iScriptShow")).Id));
        }
        // The skills the old reading moved (Stasis, Choral, VolleyArrows, ShieldsProtection...) are no rules.
        var unlocked = skills.Where(r => GameData.Int(r, "_bMovement") == 1).Select(r => Script(r)).ToHashSet();
        Assert.True(unlocked.Count > 40);
        Assert.Equal(["BackSlide", "RetreatingShadows", "RetreatingShadowsS1"],
            unlocked.Where(CombatService.Displacements.ContainsKey).Order().ToArray());
    }

    [ClientDataFact]
    public async Task AnUnlockedMovementSkillDoesNotMoveItsCaster()
    {
        // Disguise 1511201: _bMovement 1 and _kRange [100,...]: the old reading moved the caster 1 m.
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15112);
        var session = Session("alice");
        var start = session.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1511201, Face = 90 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var deadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast is { Resolved: false })) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(100);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillMove));
        await Loop.InvokeAsync(() => { Assert.Equal(start, session.Position); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ArrowRepelKnocksTheTargetBackWithItsEffect()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100"); await Teach("alice", 13005);
        var session = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var start = session.Position.Clone();
        // A stationary target 2 m away with walkable ground behind it.
        var (dx, dz) = new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 3, start.Z + o.Item2 * 3));
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.X = start.X + dx; target.Z = start.Z + dz; target.Hp = 1000000;
            return Task.CompletedTask;
        });
        var before = target.Pos;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300501, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)target.Uid);
        Assert.Equal(1300504, moved.EffectId);
        Assert.Equal(2, moved.MoveType); // enum_SkillMoveType.Repel
        Assert.Equal(100, moved.MoveTimeMs);
        Assert.Equal(before, moved.StartPos);
        float pushed = MonsterService.Distance(before.X, before.Z, moved.EndPos.X, moved.EndPos.Z);
        Assert.InRange(pushed, 0.1f, 6.01f); // _kDescData[3] = 6 m knockback
        Assert.True(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z) > 2f);
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, target.Pos); return Task.CompletedTask; });
    }

    /// <summary>A PvP hit moves the player it struck as it moves a monster: 31112 MoveType 2 to the target and its
    /// viewers, and the server position follows. Sightrasher has no effect of its own and borrows Pile Bunker's.</summary>
    [ClientDataTheory]
    [InlineData(3100, 13005, 1300501, 1300504, 6f)] // Arrow Repel: its own 1300504, _kDescData[3] = 6 m
    [InlineData(2200, 12101, 1210101, 1631405, 2f)] // Sightrasher: borrowed 1631405, _kDescData[4] = 2 m
    public async Task APvpHitKnocksThePlayerBack(int job, int group, int skill, int effect, float metres)
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", $"!job {job}"); await Teach("alice", group);
        await Gm("bob", "!lvl 99"); // a full HP pool that a low-level hit leaves standing
        var source = Session("alice");
        var victim = Session("bob");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(source.SceneEntry)!.Nav;
        var start = source.Position.Clone();
        // The victim 2 m away with walkable ground behind it.
        var (dx, dz) = new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 4, start.Z + o.Item2 * 4));
        await Loop.InvokeAsync(() =>
        {
            victim.Position = new Vec3 { X = start.X + dx, Y = start.Y, Z = start.Z + dz };
            Combat.PlayerHostile = (a, b) => a == source && b == victim;
            return Task.CompletedTask;
        });
        var before = victim.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = skill, TargetId = victim.Role!.Id, TargetPos = before,
            Face = MonsterService.Angle(start.X, start.Z, before.X, before.Z),
        });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(bob, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)victim.Role.Id);
        Assert.Equal(effect, moved.EffectId);
        Assert.Equal(2, moved.MoveType); // enum_SkillMoveType.Repel
        Assert.Equal(skill, moved.SkillConfigId);
        Assert.Equal(before, moved.StartPos);
        Assert.InRange(MonsterService.Distance(before.X, before.Z, moved.EndPos.X, moved.EndPos.Z), 0.1f, metres + 0.01f);
        Assert.True(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z) > 2f); // away from the caster
        // The caster, a viewer of the target, sees the same move.
        await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)victim.Role.Id);
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, victim.Position); return Task.CompletedTask; });
    }
}
