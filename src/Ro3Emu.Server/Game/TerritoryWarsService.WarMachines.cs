namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege side of the war machines (GuildService.Wartime keeps the warehouse and the reservations): where a member
/// may reserve and build them.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>
    /// GuildService.WarSideOf: 1 for the attackers, 2 for the defenders of the running siege the role is loaded into and
    /// fights for, else 0. Read outside the territory gate: the siege record only changes its settled flag and end, and
    /// a retired epoch reads as no siege.
    /// </summary>
    private int WarSide(GameSession s)
    {
        if (s.SiegeBattle is not { } battle || battle.Room.Closed) return 0;
        try
        {
            if (!SiegeActive(battle) || !SiegeMember(s, battle)) return 0;
            return battle.State.Players[s.Role!.Id].Guild == battle.State.Attack ? 1 : 2;
        }
        catch (InvalidOperationException) { return 0; }
        catch (KeyNotFoundException) { return 0; }
    }
}
