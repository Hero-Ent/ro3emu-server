using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class ValhallaService
{
    /// <summary>
    /// DungeonConfig._iRecoveryRes: 1 on exactly the seven Valhalla Trials dungeons (HeroicSpiritLayerConfig
    /// _iDungeonId, _iGamePlayType 23) and 0 everywhere else. No client code reads the field (it appears only in
    /// Config/DataConfig/DungeonConfig), so it is the server's: the role's HP and SP are restored when it leaves such a
    /// dungeon (inferred timing: on the way out, so a run's losses do not follow the player back to the field). A dead
    /// role is left to the revive flow.
    /// </summary>
    private async Task RecoverOnLeaveAsync(GameSession s)
    {
        if (s.InstanceId == 0 || s.Dead || s.Role == null || s.Progress == null) return;
        if (DungeonConfig.Get(_data, s.SceneEntry)?.RecoveryRes != 1) return;
        var stats = _character.Stats(s);
        await _character.SetHpSpAsync(s, stats[AttributeId.MaxHp], stats[AttributeId.MaxSp]);
    }
}
