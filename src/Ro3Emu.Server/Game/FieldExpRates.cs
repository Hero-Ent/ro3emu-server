namespace Ro3Emu.Server.Game;

/// <summary>
/// The field EXP rates (inferred, an operator's knob, not client evidence): Ro3Emu:FieldExpRates scales the Base EXP a
/// kill of an open-field monster pays (NPCConfig._iWildMonsters 1), by the killer's base level. The client's drop tables
/// give a Lv 30 role about 55 EXP a kill against the 168,600 a level needs (docs/systems/monster-ai.md); the original's
/// own rate is not in the client data.
/// </summary>
public static class FieldExpRates
{
    public const int MaxPercent = 10000;

    /// <summary>The usable rules in the order given: a level range that is not empty and a percent from 1 to
    /// <see cref="MaxPercent"/> (a rule that would pay nothing, or a thousand times more, is ignored).</summary>
    public static FieldExpRule[] Valid(ServerOptions? options) =>
        [.. (options?.FieldExpRates ?? []).Where(r => r.MinLevel >= 1 && r.MaxLevel >= r.MinLevel && r.Percent is >= 1 and <= MaxPercent)];

    /// <summary>The EXP after the first rule that holds the level (the EXP as it is when none does); a positive EXP stays at 1 or more.</summary>
    public static long Scale(IReadOnlyList<FieldExpRule> rules, long level, long exp)
    {
        if (exp <= 0) return exp;
        foreach (var rule in rules)
            if (level >= rule.MinLevel && level <= rule.MaxLevel)
                return Math.Max(1, exp * rule.Percent / 100);
        return exp;
    }
}
