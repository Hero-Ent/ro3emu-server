namespace Ro3Emu.Server.Game;

// Who each fake player is (docs/guide/fake-players.md). It lives in the server so the server can give a fake role
// its class, levels, skills and stats while loading it at login (FakePlayerService.PrepareProgress), and the
// fake player process (Ro3Emu.FakePlayers) uses the same profile for its behaviour.

/// <summary>What a fake player does once in the world.</summary>
public enum FakeBehavior
{
    /// <summary>Walks around Prontera without stopping for long.</summary>
    Walker,
    /// <summary>Stands still in Prontera (and talks more).</summary>
    Idle,
    /// <summary>Walks to a spot in Prontera, stays a while, walks again.</summary>
    Wanderer,
    /// <summary>Hunts monsters on a field map that fits its level; dies and revives.</summary>
    Hunter,
}

/// <summary>Everything that makes one fake player look like a person, fixed by its index (same index, same player).</summary>
public sealed record FakeProfile(int Index, int Sex, int Hair, int Job, int BaseLevel, int JobLevel, FakeBehavior Behavior, long HuntMap,
    long TownMap = FakeProfile.Prontera)
{
    public const long Prontera = 10100;

    /// <summary>Towns town players live in (each line holds ~200): Prontera gets the most, the others share the rest.</summary>
    public static readonly long[] Towns = [10100, 10100, 10300, 10200, 10500, 10101, 10600];

    /// <summary>The map this player starts on: its town, or for a hunter its preferred field (it may move to a quieter one).</summary>
    public long HomeMap => Behavior == FakeBehavior.Hunter ? HuntMap : TownMap;

    /// <summary>Field maps that suit a level (the hunter's choices when it changes map).</summary>
    public static long[] HuntBand(int level) => HuntMaps.Last(x => x.MinLevel <= level).Maps;

    /// <summary>The band below (an easier field after dying too often).</summary>
    public static long[] EasierBand(int level) => HuntMaps.LastOrDefault(x => x.MinLevel < HuntMaps.Last(y => y.MinLevel <= level).MinLevel).Maps
        ?? HuntMaps[0].Maps;

    private static readonly int[] FirstJobs = [1100, 2100, 3100, 4100, 5100, 6100];

    /// <summary>Second jobs per first job; Bard (3210) is male only and Dancer (3211) female only.</summary>
    private static int[] SecondJobs(int first, int sex) => first switch
    {
        1100 => [1200, 1210],
        2100 => [2200, 2210],
        3100 => sex == 1 ? [3200, 3210] : [3200, 3211],
        4100 => [4200, 4210],
        5100 => [5200, 5210],
        _ => [6200],
    };

    /// <summary>Transcendent (rank 4) job of a second job's line; Sage, Bard, Dancer, Monk and Rogue have none.</summary>
    private static int? HighJob(int second) => second switch
    {
        1200 => 1300,
        1210 => 1310,
        2200 => 2300,
        3200 => 3300,
        4200 => 4300,
        5200 => 5300,
        6200 => 6300,
        _ => null,
    };

    /// <summary>Third job (rank 5) of a transcendent one: Rune Knight, Royal Guard, Warlock, Ranger, Archbishop...</summary>
    private static int ThirdJob(int high) => high switch
    {
        1300 => 1400,
        1310 => 1410,
        2300 => 2400,
        3300 => 3400,
        4300 => 4400,
        5300 => 5400,
        _ => 6400,
    };

    /// <summary>Field maps by the level they suit (verified spawn tables; see docs/guide/fake-players.md).</summary>
    private static readonly (int MinLevel, long[] Maps)[] HuntMaps =
    [
        (1, [10111, 10110]),
        (10, [10330, 10213, 10317, 10522]),
        (22, [10112, 10218, 10113, 10510, 10314]),
        (35, [10214, 10217, 10310]),
        (48, [10512, 10420, 10219, 10220, 10531]),
        (70, [10420, 10219, 10220, 10531, 10512, 10310]),
    ];

    /// <summary>
    /// The player for an index. Classes: ~10% Novices (levels 1-9), ~22% first classes (10-39), ~30% second classes
    /// (40-74), ~18% transcendent (60-89) and ~20% third classes (70-100); job levels follow the base level.
    /// </summary>
    public static FakeProfile For(int index)
    {
        var r = new Random(unchecked(index * 7919 + 17));
        int sex = r.Next(2) == 0 ? 1 : 2;
        int hair = (sex == 1 ? 70000000 : 72000000) + r.Next(15);
        int first = FirstJobs[r.Next(FirstJobs.Length)];
        var seconds = SecondJobs(first, sex);
        int second = seconds[r.Next(seconds.Length)];
        // Lines without a transcendent class go up through the line's main second class.
        int high = HighJob(second) ?? HighJob(seconds[0]) ?? 6300;
        double roll = r.NextDouble();
        int level, job, jobLevel;
        if (roll < 0.10) { level = r.Next(1, 10); job = 1000; jobLevel = level; }
        else if (roll < 0.32) { level = r.Next(10, 40); job = first; jobLevel = Math.Clamp(level - r.Next(0, 6), 10, 40); }
        else if (roll < 0.62) { level = r.Next(40, 75); job = second; jobLevel = Math.Clamp(level - 35 + r.Next(0, 10), 1, 40); }
        else if (roll < 0.80) { level = r.Next(60, 90); job = high; jobLevel = Math.Clamp(level - 55 + r.Next(0, 10), 1, 40); }
        else { level = r.Next(70, 101); job = ThirdJob(high); jobLevel = Math.Clamp(level - 65 + r.Next(0, 10), 1, 40); }
        double b = r.NextDouble();
        var behavior = b < 0.30 ? FakeBehavior.Walker : b < 0.45 ? FakeBehavior.Idle : b < 0.65 ? FakeBehavior.Wanderer : FakeBehavior.Hunter;
        var band = HuntBand(level);
        return new FakeProfile(index, sex, hair, job, level, jobLevel, behavior, band[r.Next(band.Length)], Towns[r.Next(Towns.Length)]);
    }
}
