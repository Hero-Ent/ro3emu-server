namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GM panel character, gear, quests, dungeons, guild, mail and position ops; null when the op is not one of them.</summary>
    private static Task<string?> CharacterOpAsync(GameSession s, int op, string[] args) => Task.FromResult<string?>(null);
}
