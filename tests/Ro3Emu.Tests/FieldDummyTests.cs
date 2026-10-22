using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:FieldDummies: a training dummy stands where configured, never moves or dies.</summary>
public sealed class FieldDummyTests : IAsyncLifetime
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-dummy-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication app = null!;
    private string http = "";
    private GameLoop Loop => app.Services.GetRequiredService<GameLoop>();

    public async Task InitializeAsync()
    {
        var ports = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
            o.FieldDummies = ["10111 166 361"];
        });
        SureHitRolls.Install(app);
        await app.StartAsync(); http = $"http://127.0.0.1:{ports.Item1}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<MonsterUnit> Dummy()
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until)
        {
            var dummy = await Loop.InvokeAsync(() => Task.FromResult(app.Services.GetRequiredService<World>().Map(10111).Units
                .OfType<MonsterUnit>().FirstOrDefault(m => m.Template.NpcId == FieldDummyService.DummyNpc)));
            if (dummy != null) return dummy;
            await Task.Delay(100);
        }
        throw new TimeoutException("no field dummy");
    }

    [ClientDataFact]
    public async Task TheDummyStandsWhereConfiguredAndSurvivesALethalHit()
    {
        var dummy = await Dummy();
        Assert.Equal(166, dummy.X, 0.5);
        Assert.Equal(361, dummy.Z, 0.5);

        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await using var _ = bot;
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync("Alice"); await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        await Loop.InvokeAsync(async () =>
        {
            var s = app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Account == "alice");
            Assert.True(await app.Services.GetRequiredService<SceneService>().EnterSceneAsync(s, 10111));
        });
        var scene = SwitchSceneLineResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine), Wait)).Body);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene.SceneEntry });
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SceneLoaded), Wait);
        await Loop.InvokeAsync(async () =>
        {
            var s = app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Account == "alice");
            await app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(s, new MapPoint(dummy.X + 1, dummy.Y, dummy.Z, 0));
            dummy.Hp = 1; // the next hit is lethal for an ordinary monster
        });

        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = dummy.Uid });
        Assert.Equal(0, CastSkillRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), Wait)).Body).Result);
        var hit = SkillHitInfo.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == dummy.Uid), Wait)).Body);
        Assert.False(hit.Dsts.Single(d => d.DstID == dummy.Uid).IsDead);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(dummy.Dead);
            Assert.Equal(dummy.Template.MaxHp, dummy.Hp);
            Assert.Null(dummy.Target);
            return Task.CompletedTask;
        });
    }
}
