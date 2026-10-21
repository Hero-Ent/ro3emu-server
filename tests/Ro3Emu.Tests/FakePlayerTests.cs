using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.FakePlayers;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// "!fakeplayer &lt;n&gt;" (docs/guide/fake-players.md): fake players with real-looking names, classes and hair that log in
/// through the real protocol, live in Prontera or hunt on field maps, talk, and count as online.
/// </summary>
public sealed class FakePlayerTests
{
    [Fact]
    public void NamesLookLikePlayersAndFitTheServerRules()
    {
        var preferred = new List<string>();
        for (int i = 1; i <= 2000; i++)
        {
            var names = FakeNames.Candidates(i).ToList();
            Assert.True(names.Count >= 10);
            Assert.All(names, n =>
            {
                Assert.InRange(n.Length, 2, 16); // RoleSessionService: 2..16 characters
                Assert.False(n.StartsWith('!') || n.StartsWith('/'));
            });
            preferred.Add(names[0]);
        }
        // Mostly distinct first choices; a clash just takes the next candidate.
        Assert.True(preferred.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1500);
        Assert.Equal(FakeNames.Candidates(42), FakeNames.Candidates(42)); // same index, same player
    }

    [Fact]
    public void ProfilesMixClassesHairAndBehaviours()
    {
        var profiles = Enumerable.Range(1, 1000).Select(FakeProfile.For).ToList();
        foreach (FakeBehavior behavior in Enum.GetValues<FakeBehavior>())
            Assert.Contains(profiles, p => p.Behavior == behavior);
        Assert.All(profiles, p =>
        {
            Assert.InRange(p.Hair, p.Sex == 1 ? 70000000 : 72000000, p.Sex == 1 ? 70000014 : 72000014);
            Assert.False(p.Job == 3210 && p.Sex != 1, "Bard is male only");
            Assert.False(p.Job == 3211 && p.Sex != 2, "Dancer is female only");
            Assert.True(p.Job == 1000 || p.BaseLevel >= 10, "a class needs base level 10");
            Assert.Equal(p.Behavior == FakeBehavior.Hunter ? p.HuntMap : p.TownMap, p.HomeMap);
            Assert.Contains(p.TownMap, FakeProfile.Towns);
        });
        Assert.True(profiles.Select(p => p.Job).Distinct().Count() >= 25);
        // Every rank shows up: Novice, first, second, transcendent and third classes.
        int Rank(int job) => job == 1000 ? 1 : job % 1000 / 100 + 1;
        Assert.All(new[] { 1, 2, 3, 4, 5 }, rank => Assert.Contains(profiles, p => Rank(p.Job) == rank));
        Assert.InRange(profiles.Count(p => Rank(p.Job) >= 3), 600, 750); // most players have a second class or above
        Assert.All(profiles.Where(p => Rank(p.Job) == 5), p => Assert.True(p.BaseLevel >= 70));
        Assert.InRange(profiles.Count(p => p.Behavior == FakeBehavior.Hunter), 250, 450);
    }

    [Fact]
    public void FacingUsesTheClientConvention()
    {
        Assert.Equal(0f, BotClient.FaceTowards(0, 0, 0, 5), 3);    // +z
        Assert.Equal(90f, BotClient.FaceTowards(0, 0, 5, 0), 3);   // +x
        Assert.Equal(180f, BotClient.FaceTowards(0, 0, 0, -5), 3);
        Assert.Equal(270f, BotClient.FaceTowards(0, 0, -5, 0), 3);
    }

    [Fact]
    public void OnlyPrefixedNumberedAccountsAreFake()
    {
        var service = (FakePlayerService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FakePlayerService));
        typeof(FakePlayerService).GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, Microsoft.Extensions.Options.Options.Create(new ServerOptions()));
        Assert.True(service.IsFake("fp_00001"));
        Assert.False(service.IsFake("fp_"));
        Assert.False(service.IsFake("fp_abc"));
        Assert.False(service.IsFake("hero"));
        Assert.False(service.IsFake(null));
    }

    /// <summary>
    /// The whole path: a GM types !fakeplayer, the server registers the accounts, starts Ro3Emu.FakePlayers, and the
    /// players log in, get their class/level/stats, reach their maps, walk, hunt and talk; !fakeplayer 0 removes them.
    /// </summary>
    [GameClientDataFact]
    public async Task TheGmCommandFillsTheWorldWithFakePlayersAndEmptiesItAgain()
    {
        // Enough players for every behaviour (the host starts indices 1..n).
        var profiles = Enumerable.Range(1, 200).Select(FakeProfile.For).ToList();
        int count = Enum.GetValues<FakeBehavior>().Max(b => profiles.First(p => p.Behavior == b).Index);
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-fake-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = GameClientData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.ReconnectGraceSeconds = 0;
            o.FakePlayers.ChatSpeed = 30;
            o.FakePlayers.LoginConcurrency = 6;
        });
        await app.StartAsync();
        var sessions = app.Services.GetRequiredService<SessionRegistry>();
        var fakes = app.Services.GetRequiredService<FakePlayerService>();
        try
        {
            string http = $"http://127.0.0.1:{httpPort}";
            await using var gm = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await gm.LoginAsync("gamemaster", "pw");
            await gm.EnterGateAsync();
            await gm.EnterWorldAsync((await gm.CreateRoleAsync("GameMaster")).RoleBaseInfo.Id);
            await gm.JoinChatAsync("4_1");
            await gm.SendChatAsync("4_1", 2, $"!fakeplayer {count}", channelIndex: 4);

            // Online, profiled (class + level) and on their own map.
            var ready = await Until(TimeSpan.FromMinutes(4), () =>
            {
                var online = sessions.All().Where(s => fakes.IsFake(s.Account) && s.Role != null && s.Progress != null).ToList();
                return online.Count == count && online.All(s =>
                {
                    var p = FakeProfile.For(int.Parse(s.Account![3..]));
                    return s.Progress!.Career == p.Job && s.Progress.Level >= p.BaseLevel && (p.Behavior == FakeBehavior.Hunter
                        ? FakeProfile.HuntBand(p.BaseLevel).Contains(s.SceneEntry) : s.SceneEntry == p.TownMap);
                }) ? online : null;
            });
            Assert.True(ready != null, $"fake players not ready: {fakes.FakeOnline}/{count} online");
            Assert.All(ready!, s => Assert.True(s.Progress!.Stats.Sum() > 6, "free stat points were spent"));
            // Classed players learned their class's attack skills with their skill points.
            var skillService = app.Services.GetRequiredService<SkillService>();
            Assert.All(ready!.Where(s => s.Progress!.Career != 1000 && s.Progress.JobLevel > 1), s =>
                Assert.True(s.Progress!.Skills.Count > 0, $"{s.Role!.Name} ({s.Progress.Career}) learned no skill"));
            Assert.Contains(ready!, s => FakeProfile.Towns.Contains(s.SceneEntry));

            // Walkers move, hunters attack, talkers talk.
            var walker = ready!.First(s => FakeProfile.For(int.Parse(s.Account![3..])).Behavior == FakeBehavior.Walker);
            var start = walker.Position;
            string? status = null;
            var moved = await Until(TimeSpan.FromSeconds(60), () =>
            {
                status = fakes.LastStatus;
                return walker.Position.X != start.X || walker.Position.Z != start.Z ? "moved" : null;
            });
            Assert.Equal("moved", moved);
            // Facing follows the walk (no moonwalking): the face the server keeps points the way the player moved.
            var previous = walker.Position;
            var facing = await Until(TimeSpan.FromSeconds(30), () =>
            {
                var now = walker.Position;
                float dx = now.X - previous.X, dz = now.Z - previous.Z;
                if (dx * dx + dz * dz < 1) return null;
                float travel = BotClient.FaceTowards(previous.X, previous.Z, now.X, now.Z);
                previous = now;
                return MathF.Abs((walker.Face - travel + 540f) % 360f - 180f) < 30f ? "facing" : null;
            });
            Assert.Equal("facing", facing);
            var lively = await Until(TimeSpan.FromSeconds(90), () =>
            {
                status = fakes.LastStatus;
                return Counter(status, "attacks") > 0 && Counter(status, "chats") > 0 ? status : null;
            });
            Assert.True(lively != null, $"no attacks or chats yet: {status}");

            await gm.SendChatAsync("4_1", 2, "!fakeplayer 0", channelIndex: 4);
            var empty = await Until(TimeSpan.FromSeconds(60), () => fakes.FakeOnline == 0 ? "empty" : null);
            Assert.Equal("empty", empty);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Stress run, off by default: RO3EMU_FAKE_STRESS=&lt;players&gt; [RO3EMU_FAKE_STRESS_SECONDS=180]. Prints, every 10 s,
    /// fake players online, the process counters, the game loop's busy ratio and queue, and both processes' CPU and memory.
    /// </summary>
    [GameClientDataFact]
    public async Task StressWithManyFakePlayers()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("RO3EMU_FAKE_STRESS"), out int count) || count <= 0) return;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("RO3EMU_FAKE_STRESS_SECONDS"), out int s) ? s : 180;
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-fakestress-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = GameClientData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.PreloadGameData = true;
            o.ReconnectGraceSeconds = 0;
            o.FakePlayers.MaxBots = Math.Max(count, 3000);
            o.FakePlayers.LoginConcurrency = 16;
            if (Environment.GetEnvironmentVariable("RO3EMU_FAKE_SKILLS") == "0") o.FakePlayers.Skills = false;
        });
        await app.StartAsync();
        var sessions = app.Services.GetRequiredService<SessionRegistry>();
        var fakes = app.Services.GetRequiredService<FakePlayerService>();
        var report = new List<string>();
        void Line(string text) { report.Add(text); Console.WriteLine("[STRESS] " + text); }
        try
        {
            string http = $"http://127.0.0.1:{httpPort}";
            using var metrics = new HttpClient { BaseAddress = new Uri(http) };
            await using var gm = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await gm.LoginAsync("gamemaster", "pw");
            await gm.EnterGateAsync();
            await gm.EnterWorldAsync((await gm.CreateRoleAsync("GameMaster")).RoleBaseInfo.Id);
            await gm.JoinChatAsync("4_1");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await gm.SendChatAsync("4_1", 2, $"!fakeplayer {count}", channelIndex: 4);
            var self = System.Diagnostics.Process.GetCurrentProcess();
            TimeSpan selfCpu = self.TotalProcessorTime, fakeCpu = TimeSpan.Zero;
            var last = clock.Elapsed;
            int peak = 0;
            TimeSpan? allOnline = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                await Task.Delay(10_000);
                await gm.SendGameAsync(MsgId.Heartbeat, new Ping());
                self.Refresh();
                var fakeProcess = System.Diagnostics.Process.GetProcessesByName("Ro3Emu.FakePlayers").FirstOrDefault();
                var now = clock.Elapsed;
                double wall = (now - last).TotalSeconds * Environment.ProcessorCount;
                double serverCpu = (self.TotalProcessorTime - selfCpu).TotalSeconds / wall * 100;
                double botCpu = fakeProcess == null ? 0 : (fakeProcess.TotalProcessorTime - fakeCpu).TotalSeconds / wall * 100;
                selfCpu = self.TotalProcessorTime;
                fakeCpu = fakeProcess?.TotalProcessorTime ?? TimeSpan.Zero;
                last = now;
                var loop = System.Text.Json.Nodes.JsonNode.Parse(await metrics.GetStringAsync("/ro3emu/metrics"))?["loop"];
                int online = fakes.FakeOnline;
                var fighting = sessions.All().Where(x => fakes.IsFake(x.Account) && x.Attack != null).Select(x => x.Attack!.Target.Uid).ToList();
                string spread = $"targets {fighting.Distinct().Count()}/{fighting.Count}";
                peak = Math.Max(peak, online);
                if (online >= count && allOnline == null) allOnline = now;
                Line($"{now.TotalSeconds,5:F0}s fakes {online}/{count} | loop busy {loop?["busyRatio"]?.GetValue<double>() * 100:F0}% queue {loop?["queueLengthCurrent"]} " +
                    $"| CPU server {serverCpu:F0}% bots {botCpu:F0}% (of {Environment.ProcessorCount} cores) | RAM server {self.WorkingSet64 >> 20} MB " +
                    $"bots {(fakeProcess?.WorkingSet64 ?? 0) >> 20} MB | {spread} | {fakes.LastStatus}");
                fakeProcess?.Dispose();
            }
            Line($"peak {peak}/{count}, all online after {(allOnline?.TotalSeconds.ToString("F0") ?? "never")} s");
            // Where the game loop spent its time (slow items, tickers, owner scopes): the first place to look when it saturates.
            File.WriteAllText(Path.Combine(ClientData.RepoRoot, ".loadtest", $"fakeplayers-{count}-metrics.json"),
                await metrics.GetStringAsync("/ro3emu/metrics"));
            Assert.True(peak >= count * 0.95, $"only {peak}/{count} fake players came online");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(ClientData.RepoRoot, ".loadtest", $"fakeplayers-{count}-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), report);
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static long Counter(string? status, string name) =>
        status != null && Regex.Match(status, $@"\b{name}=(\d+)") is { Success: true } m ? long.Parse(m.Groups[1].Value) : 0;

    private static async Task<T?> Until<T>(TimeSpan limit, Func<T?> probe) where T : class
    {
        var end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end)
        {
            if (probe() is { } value) return value;
            await Task.Delay(500);
        }
        return probe();
    }
}

/// <summary>The game data shipped in the client package (Client\RO3Emu\data), used by the fake player end-to-end test.</summary>
public static class GameClientData
{
    public static string Dir { get; } = Path.Combine(ClientData.RepoRoot, "Client", "RO3Emu", "data");
    public static bool Available => Directory.Exists(Path.Combine(Dir, "config")) && Directory.Exists(Path.Combine(Dir, "nav"));
}

/// <summary>A fact that needs Client\RO3Emu\data (real maps and nav grids); skipped without it.</summary>
public sealed class GameClientDataFactAttribute : FactAttribute
{
    public GameClientDataFactAttribute()
    {
        if (!GameClientData.Available) Skip = "Needs the client package's game data in Client/RO3Emu/data.";
    }
}
