using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>SkillMechanics (Reconstructed/skills/*.json): the data loads and points at real rows; Mechanic skills behave as described.</summary>
public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public void SkillMechanicsLoadAndReferenceRealData()
    {
        Assert.Empty(SkillMechanics.LoadErrors);
        var data = _app.Services.GetRequiredService<GameData>();
        var skills = data.Table("SkillConfig");
        var problems = new List<string>();
        foreach (var m in SkillMechanics.Every)
        {
            var rows = skills.Values.Where(r => GameData.Int(r, "_iSkillID") == m.Group).ToList();
            if (rows.Count == 0) { problems.Add($"{m.Group} {m.Name}: no SkillConfig rows"); continue; }
            var top = rows.Where(r => GameData.Int(r, "_iMode") == m.Mode).DefaultIfEmpty(rows[0]).MaxBy(r => GameData.Int(r, "_iLevel"));
            var template = data.SkillTemplate(GameData.Int(top, "_iScriptShow"));
            int points = template is { } t && t.TryGetProperty("m_kAtkPoints", out var p) ? p.GetArrayLength() : 0;
            foreach (var hit in m.Hits)
            {
                if (hit.Point is int index && index >= points) problems.Add($"{m.Group} {m.Name}: attack point {index} but the template has {points}");
                if (hit.AfterPoint is int after && after >= points) problems.Add($"{m.Group} {m.Name}: afterPoint {after} but the template has {points}");
            }
            foreach (var (buff, _, _) in m.TargetBuffs.Concat(m.SelfBuffs))
                if (data.Row("BuffConfig", buff) == null) problems.Add($"{m.Group} {m.Name}: buff {buff} missing");
            if (m.ShieldBuff is int shield && data.Row("BuffConfig", shield) == null) problems.Add($"{m.Group} {m.Name}: shield buff {shield} missing");
            if (m.FollowUpSkill is int follow && data.Row("SkillConfig", follow) == null) problems.Add($"{m.Group} {m.Name}: follow-up {follow} missing");
            if (m.FollowUpGroup is int followGroup && !skills.Values.Any(r => GameData.Int(r, "_iSkillID") == followGroup))
                problems.Add($"{m.Group} {m.Name}: follow-up group {followGroup} missing");
            if (m.Mode != 0 && !rows.Any(r => GameData.Int(r, "_iMode") == m.Mode)) problems.Add($"{m.Group}:{m.Mode} {m.Name}: no row with that mode");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        foreach (var t in SkillMechanics.EveryTrigger)
        {
            if (t.ByGroup ? !data.Table("BuffConfig").Values.Any(r => GameData.Int(r, "_iBuffID") == t.Buff) : data.Row("BuffConfig", t.Buff) == null)
                problems.Add($"trigger {t.Buff} {t.Name}: buff missing");
            if (t.WhileBuff is int w && !data.Table("BuffConfig").Values.Any(r => GameData.Int(r, "_iBuffID") == w)) problems.Add($"trigger {t.Buff} {t.Name}: whileBuff group {w} missing");
            if (t.LevelFrom is int lf && !skills.Values.Any(r => GameData.Int(r, "_iSkillID") == lf)) problems.Add($"trigger {t.Buff} {t.Name}: levelFrom {lf} missing");
            if (t.Skill is int s && data.Row("SkillConfig", s) == null) problems.Add($"trigger {t.Buff} {t.Name}: skill {s} missing");
            if (t.Group is int g && !skills.Values.Any(r => GameData.Int(r, "_iSkillID") == g)) problems.Add($"trigger {t.Buff} {t.Name}: group {g} missing");
        }
        foreach (var m in SkillMechanics.Every)
        {
            if (m.OnHitSkill is int onHit && data.Row("SkillConfig", onHit) == null) problems.Add($"{m.Group} {m.Name}: onHitCast {onHit} missing");
            foreach (int g in m.ResetCooldown ?? [])
                if (!skills.Values.Any(r => GameData.Int(r, "_iSkillID") == g)) problems.Add($"{m.Group} {m.Name}: resetCooldown group {g} missing");
            foreach (int g in m.Cleanse ?? [])
                if (!data.Table("BuffConfig").Values.Any(r => GameData.Int(r, "_iBuffID") == g)) problems.Add($"{m.Group} {m.Name}: cleanse group {g} missing");
            if (m.BranchIf is { CasterBuffGroup: int bg } && !data.Table("BuffConfig").Values.Any(r => GameData.Int(r, "_iBuffID") == bg))
                problems.Add($"{m.Group} {m.Name}: branchIf buff group {bg} missing");
            foreach (int g in m.ConsumePiles ?? [])
                if (!data.Table("BuffConfig").Values.Any(r => GameData.Int(r, "_iBuffID") == g)) problems.Add($"{m.Group} {m.Name}: consumePiles group {g} missing");
            if (m.AtStacks is { } st)
                foreach (int b in st.Grants.Append(st.Counter))
                    if (data.Row("BuffConfig", b) == null) problems.Add($"{m.Group} {m.Name}: stackGrant buff {b} missing");
            if (m.DispatchDistance != null)
                foreach (var r in skills.Values.Where(r => GameData.Int(r, "_iSkillID") == m.Group && (m.Mode == 0 || GameData.Int(r, "_iMode") == m.Mode)))
                    foreach (var id in r.GetProperty("_kLogicData").EnumerateArray())
                        if (data.Row("SkillConfig", id.GetInt64()) == null) problems.Add($"{m.Group} {m.Name}: dispatch row {id} missing");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [ClientDataFact]
    public async Task EnergyReleaseSpendsHpThenExplodesAfterTheCountdown()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16317);
        var caster = Session("alice");
        var origin = caster.Position.Clone();
        var near = new List<MonsterUnit>();
        for (int i = 0; i < 3; i++) near.Add(await Target("alice", stationary: true));
        await Loop.InvokeAsync(async () =>
        {
            for (int i = 0; i < near.Count; i++) { near[i].Hp = 50_000_000; near[i].Moving = false; near[i].X = origin.X + 2 + i; near[i].Z = origin.Z; }
            await Character.SetHpSpAsync(caster, Character.Stats(caster)[101], null);
        });
        long maxHp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(caster)[101]));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1631701, Face = 0, TargetPos = origin });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Task.Delay(500);
        long afterCost = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(caster)[2000]));
        Assert.InRange(afterCost, maxHp / 2 - 2, maxHp / 2 + 2); // 50% of Max HP turned into the countdown shield
        Assert.All(near, m => Assert.Equal(50_000_000, m.Hp)); // nothing explodes at the cast
        // 3 s later the follow-up (1635201) explodes around the caster and costs 50% Max HP more, never fatally.
        var boom = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1635201);
        Assert.All(near, m => Assert.Contains(boom.Dsts, d => d.DstID == m.Uid)); // map monsters nearby may be hit too
        await Task.Delay(200);
        long afterBoom = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(caster)[2000]));
        Assert.Equal(1, afterBoom);
        Assert.False(caster.Dead);
    }

    [ClientDataFact]
    public async Task AerialBombardmentDropsSevenBombsOnTheStrip()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16318);
        var target = await Target("alice", stationary: true);
        var origin = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() => { target.Hp = 50_000_000; target.Moving = false; target.X = origin.X; target.Z = origin.Z + 6; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1631801, Face = 0, TargetPos = origin });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        for (int bomb = 0; bomb < 7; bomb++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            Assert.Contains(hit.Dsts, d => d.DstID == target.Uid);
            Assert.Equal(3, hit.Split);
        }
    }

    [ClientDataFact]
    public async Task ArmCannonSplashesAroundTheLockedTargetTwice()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16312);
        var locked = await Target("alice", stationary: true);
        var beside = await Target("alice", stationary: true);
        var byCaster = await Target("alice", stationary: true);
        var origin = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            foreach (var m in new[] { locked, beside, byCaster }) { m.Hp = 50_000_000; m.Moving = false; }
            locked.X = origin.X; locked.Z = origin.Z + 1;       // next to the caster: no navigation in the way
            beside.X = origin.X + 3; beside.Z = origin.Z + 1;   // 3 m from the target: inside its 6 m splash
            byCaster.X = origin.X; byCaster.Z = origin.Z - 6;   // 7 m from the target, behind the caster
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1631201, TargetId = locked.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        var ids = first.Dsts.Select(d => d.DstID).ToHashSet();
        Assert.Contains(locked.Uid, ids);
        Assert.Contains(beside.Uid, ids);
        Assert.DoesNotContain(byCaster.Uid, ids);
    }
}
