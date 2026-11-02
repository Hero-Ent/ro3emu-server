using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// ServerOptions.DungeonSoloEntry, a custom measure: a party of any size, one role included, may start a dungeon that
/// asks for a bigger team, and the run has as many players as the party.
/// </summary>
public sealed class DungeonSoloEntryTests
{
    // The fixture's only DungeonConfig row (a map the server can open); the plan asks for five like a team boss.
    private const long Scene = 4000101;

    [Fact]
    public async Task LoneRoleStartsADungeonThatRequiresATeam()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        Assert.Equal(0, await Begin(host, party: false));
    }

    [Fact]
    public async Task PartyOfTwoStartsADungeonThatRequiresFive()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        Assert.Equal(0, await Begin(host, party: true));
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            var members = host.Service<SessionRegistry>().All().ToArray();
            Assert.Equal(2, members.Length);
            Assert.All(members, m => Assert.Equal(members[0].DungeonAttempt, m.DungeonAttempt));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WithoutTheOptionALoneRoleIsRefusedTheTeamSize()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.DungeonSoloEntry = false);
        await using var alice = await host.InWorldAsync("Alice");
        Assert.Equal(ErrorCodes.DungeonTeamSize, await Begin(host, party: false));
    }

    private static Task<int> Begin(SyntheticHost host, bool party)
    {
        var config = host.Service<GameData>().Row("DungeonConfig", Scene)!.Value;
        var plan = new DungeonCatalog.Plan(config, null, null, 0, 5, 5, [new DungeonCatalog.Wave(1, [])]);
        return host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var members = host.Service<SessionRegistry>().All().ToArray();
            long team = party ? await host.Service<TeamService>().CreateMatchedPartyAsync(members) : 0;
            if (party) Assert.NotEqual(0, team);
            return await host.Service<DungeonService>().BeginMatchedAsync(plan, members, team);
        });
    }
}
