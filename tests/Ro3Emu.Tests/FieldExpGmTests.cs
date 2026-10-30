using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The "!fieldexp" GM command: Ro3Emu:FieldExpRates changed in memory, here through the loopback /ro3emu/gm the panel uses.</summary>
public sealed class FieldExpGmTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-fieldexp-gm-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.FieldExpRates = [new FieldExpRule { MinLevel = 1, MaxLevel = 10, Percent = 150 }];
        });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=fieldexp", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private IReadOnlyList<FieldExpRule> Rules => _app.Services.GetRequiredService<RewardService>().FieldExpRules;

    [ClientDataFact]
    public async Task TheCommandChangesTheRulesInForceAndResetBringsBackTheConfigured()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("fieldexp", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("FieldExp")).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));

        Assert.Equal((1, 10, 150), (Rules[0].MinLevel, Rules[0].MaxLevel, Rules[0].Percent)); // from the configuration

        await Gm("!fieldexp set 20 40 300");
        var rule = Assert.Single(Rules);
        Assert.Equal((20, 40, 300), (rule.MinLevel, rule.MaxLevel, rule.Percent));

        await Gm("!fieldexp set 40 20 300"); // refused: the usage answers and the rules stay
        Assert.Equal(300, Assert.Single(Rules).Percent);

        await Gm("!fieldexp");
        Assert.Equal(300, Assert.Single(Rules).Percent); // showing changes nothing

        await Gm("!fieldexp clear");
        Assert.Empty(Rules);

        await Gm("!fieldexp reset");
        Assert.Equal(150, Assert.Single(Rules).Percent);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
