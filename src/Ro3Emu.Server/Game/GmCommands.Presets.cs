namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GM panel ops that only GMCombinationConfig presets use (builds: weapon, mount, pendant, emblem, skills, talents); null when the op is not one of them.</summary>
    private static Task<string?> PresetOpAsync(GameSession s, int op, string[] args) => Task.FromResult<string?>(null);
}
