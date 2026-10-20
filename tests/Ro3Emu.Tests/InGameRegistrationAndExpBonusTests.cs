using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

public sealed class InGameRegistrationAndExpBonusTests
{
    [Fact]
    public async Task WhenInGameRegistrationDisabledUnknownAccountReturns404()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-reg-disabled-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.AllowInGameRegistration = false; // explicitly disabled
        });
        await app.StartAsync();

        try
        {
            using var client = new HttpClient();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["identity"] = "nonexistent_player",
                ["credential"] = "password123"
            });

            var response = await client.PostAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.LoginPath}", form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(404, json.GetProperty("code").GetInt32());
            Assert.Contains("site", json.GetProperty("msg").GetString()!);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ExpBonusServiceCalculatesCorrectBonusAndApplies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ro3-exp-bonus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var options = Options.Create(new ServerOptions { DataDir = tempDir });
            var service = new ExpBonusService(options);

            // Default is 100x
            service.SetMultiplier(100, saveToFile: false);
            Assert.Equal(100, service.Multiplier);

            // Base EXP = 10, Bonus = 10 * 100 = 1000
            Assert.Equal(1000, service.CalculateBonus(10));

            long baseExp = 15;
            long jobExp = 20;
            service.ApplyBonus(ref baseExp, ref jobExp);
            Assert.Equal(15 + 15 * 100, baseExp); // 1515
            Assert.Equal(20 + 20 * 100, jobExp);  // 2020

            // Multiplier 0: no bonus
            service.SetMultiplier(0, saveToFile: false);
            Assert.Equal(0, service.Multiplier);
            Assert.Equal(0, service.CalculateBonus(100));

            baseExp = 50;
            jobExp = 30;
            service.ApplyBonus(ref baseExp, ref jobExp);
            Assert.Equal(50, baseExp);
            Assert.Equal(30, jobExp);

            // Multiplier 1: 1x extra bonus
            service.SetMultiplier(1, saveToFile: false);
            baseExp = 100;
            jobExp = 50;
            service.ApplyBonus(ref baseExp, ref jobExp);
            Assert.Equal(200, baseExp);
            Assert.Equal(100, jobExp);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }
}
