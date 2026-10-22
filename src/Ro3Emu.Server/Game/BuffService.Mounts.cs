using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>The source of every mount-bound passive (inferred: one source id for them all).</summary>
    private const long MountBindSource = -5012;

    /// <summary>
    /// CartRemouldTable / MountsConfig _kBindBuff while that mount is ridden, dropped on dismount: the Ranger's wolf
    /// 1500400 -> 1331001 "Mount Buff" (+10% MSPD, and _kSkillAttributeList type 15 switching Warg Strike 13312 and
    /// Arrow Smash 13099 to their mounted mode 1), the Knight's Peco 1131601, Dragon 1131901, Gryphon 1163901. Nothing
    /// granted them, so mounted-only modes (Warg Charge) could not be cast. (inferred: the original grant point.)
    /// </summary>
    public async Task MountBindBuffsAsync(GameSession p)
    {
        if (p.Role == null) return;
        long rider = MountService.Rider(p);
        var row = rider == 0 ? null : _data.Row("CartRemouldTable", rider) ?? _data.Row("MountsConfig", rider);
        var want = row is { } r && r.TryGetProperty("_kBindBuff", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).Where(id => id > 0 && _data.Row("BuffConfig", id) != null).ToHashSet()
            : [];
        bool changed = false;
        foreach (var buff in p.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == MountBindSource && !want.Contains(b.ConfigId)).ToArray())
        {
            await RemoveAsync(p, buff);
            changed = true;
        }
        foreach (int id in want.Where(id => !p.Buffs.Any(b => b.IsPassive && b.SourceId == MountBindSource && b.ConfigId == id)))
        {
            await AddPassiveAsync(p, id, MountBindSource, totemOwned: false);
            changed = true;
        }
        // AddPassiveAsync does not raise Changed: the skill parameters (type 15 modes) and stats must follow.
        if (changed)
        {
            await _character.RefreshStatsAsync(p);
            foreach (var handler in Changed) await handler(p);
        }
    }
}
