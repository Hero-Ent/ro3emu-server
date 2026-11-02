using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// An awarded LifeJobEnergy currency (the To-Do chests' Vigor) is the Vigor gathering spends: restriction 1004, not a
/// currency balance the mining check never reads.
/// </summary>
public sealed class LifeEnergyAwardTests
{
    private const int Vigor = 3990006; // fixture currency of _iSubType LifeJobEnergy
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task AwardedVigorReachesTheRestrictionGatheringSpendsUpToTheExtraLimit()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Alice");
        long role = bot.Roles.Single().Id;
        await Until(host, s => Energy(s) == 300, "VitalityInitCount");

        await Award(host, 200);
        await Until(host, s => Energy(s) == 500 && Balance(s) == 0, "chest vigor credited");

        // VitalityExtraLimit 800: the 300 past the cap stays as currency instead of being lost.
        await Award(host, 600);
        await Until(host, s => Energy(s) == 800 && Balance(s) == 300, "credit stops at the extra limit");

        var saved = DBRestrictInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadLifeRestrict(role));
        Assert.Equal(800, saved.RestrictCount[LifeJobService.VigorId].Count);
        Assert.Equal(300, host.Service<RoleStore>().LoadProgress(role).Currency.GetValueOrDefault(Vigor));
    }

    private static long Energy(GameSession s) => s.Restrictions?.RestrictCount.GetValueOrDefault(LifeJobService.VigorId)?.Count ?? -1;
    private static long Balance(GameSession s) => s.Progress?.Currency.GetValueOrDefault(Vigor) ?? -1;

    private static Task Award(SyntheticHost host, int count) => host.Service<GameLoop>().InvokeAsync(async () =>
        Assert.True(await host.Service<CharacterService>().ApplyAwardAsync(Session(host),
            new AwardPlan(0, 0, [new AwardGrant(Vigor, count)]))));

    private static GameSession Session(SyntheticHost host) => host.Service<SessionRegistry>().All().Single(s => s.Role != null);

    private static async Task Until(SyntheticHost host, Func<GameSession, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(condition(Session(host)))))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }
}
