namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GM panel combat and entity debug ops (HP, buffs, attributes, skills, NPCs, camps); null when the op is not one of them.</summary>
    private static Task<string?> CombatOpAsync(GameSession s, int op, string[] args) => Task.FromResult<string?>(null);
}
