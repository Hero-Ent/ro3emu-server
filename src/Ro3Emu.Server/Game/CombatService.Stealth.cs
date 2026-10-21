using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Stealth and the Hiding speed buff end the way their texts say: "You are revealed after taking damage «N» times or
/// dealing damage" (Cloaking 1500602, Enhance Cloaking 1530401) and "removed after attacking or taking damage a total of
/// «N» times" (Hiding 15007). Their BuffConfig _kLogicData is [buff group removed with it, 1 = dealing damage reveals,
/// hits that reveal, optional SkillConfig row released on removal] (1500602 [15007, 1, 1], 1530401 [15305, 1, 5], talent
/// 11530401 [115305, 1, 5, 11532701] = the 15327 disc ring; monster 5000901 [50031, 0, 1] whose text only names hits).
/// (inferred from those texts and data; the original server script is not in the client.)
/// </summary>
public sealed partial class CombatService
{
    private const int HidingSpeedGroup = 15007;
    private readonly Dictionary<long, int> _revealHits = new(); // buff guid -> hits counted so far

    private (int Linked, bool OnDeal, int Hits, int Skill)? RevealRule(ActiveBuff buff)
    {
        if (data.Row("BuffConfig", buff.ConfigId) is not { } row) return null;
        if (!BuffService.IsStealth(row) && GameData.Int(row, "_iBuffID") != HidingSpeedGroup) return null;
        var logic = Numbers(row, "_kLogicData");
        return logic.Length >= 3 && logic[2] > 0 ? ((int)logic[0], logic[1] == 1, (int)logic[2], logic.Length > 3 ? (int)logic[3] : 0) : null;
    }

    private async Task RevealHitAsync(GameSession p, bool dealt)
    {
        if (p.Role == null) return;
        foreach (var buff in p.Buffs.Where(b => b.ExpireMs > Now).ToArray())
        {
            if (RevealRule(buff) is not { } rule || !p.Buffs.Contains(buff)) continue;
            if (dealt && !rule.OnDeal) continue;
            bool stealth = data.Row("BuffConfig", buff.ConfigId) is { } row && BuffService.IsStealth(row);
            // Dealing damage ends a stealth buff at once ("or dealing damage"); for the Hiding speed buff it is one of
            // its N ("attacking or taking damage a total of N times").
            int hits = dealt && stealth ? rule.Hits : _revealHits.GetValueOrDefault(buff.Guid) + 1;
            if (hits < rule.Hits) { _revealHits[buff.Guid] = hits; continue; }
            _revealHits.Remove(buff.Guid);
            await buffs.RemoveAsync(p, buff);
            if (rule.Linked > 0)
                foreach (var linked in p.Buffs.Where(o => data.Row("BuffConfig", o.ConfigId) is { } lr && GameData.Int(lr, "_iBuffID") == rule.Linked).ToArray())
                    await buffs.RemoveAsync(p, linked);
            // "When the [Enhance Camouflage] status is removed by dealing or taking damage, unleash a ring of discs".
            if (rule.Skill > 0 && data.Row("SkillConfig", rule.Skill) is { } skillRow && data.Row("BuffConfig", buff.ConfigId) is { } buffRow
                && FollowUpRow((int)GameData.Int(skillRow, "_iSkillID"), (int)GameData.Int(buffRow, "_iLevel")) is int release)
                await CastTriggeredAsync(p, release, null, p.Position);
        }
    }

    private void WireStealth()
    {
        TakenAsync.Add((_, p, damage) => damage > 0 ? RevealHitAsync(p, dealt: false) : Task.CompletedTask);
        DealtAsync.Add((s, _, _, damage) => damage > 0 ? RevealHitAsync(s, dealt: true) : Task.CompletedTask);
        buffs.Expired.Add((_, _, buff) => { _revealHits.Remove(buff.Guid); return Task.CompletedTask; });
    }
}
