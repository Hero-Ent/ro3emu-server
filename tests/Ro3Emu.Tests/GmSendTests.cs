using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Hero Saga's "!enviar": a GM gives another character an item by system mail, online or offline.</summary>
public sealed class GmSendTests : GmSendTestsBase
{
    protected override string GameDataDir => SyntheticData.Dir;

    // A currency (1 Zeny here) to an offline character: the player claims it after logging in.
    [Fact]
    public Task GmSendsACurrencyToAnOfflineCharacterByMail() => SendAndClaim(1, 500);
}

/// <summary>The same on the live data: cash is item 3.</summary>
public sealed class GmSendCashTests : GmSendTestsBase
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    [ClientDataFact]
    public Task GmSendsCashToAnOfflineCharacterByMail() => SendAndClaim(3, 500);
}

public abstract class GmSendTestsBase : MailTestsHost
{
    protected async Task SendAndClaim(int item, int count)
    {
        var (bob, bobRole) = await InWorld("Bob");
        await bob.DisposeAsync();
        var (alice, _) = await InWorld("Alice");
        await using var gm = alice;

        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent("!enviar Ninguem 1 500"))).EnsureSuccessStatusCode();
        await Reply(alice, "não encontrado");
        (await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent($"!enviar Bob {item} {count}"))).EnsureSuccessStatusCode();
        await Reply(alice, $"Enviado {count}x"); // the GM runs on the loop after the HTTP answer

        var (again, role) = await InWorld("Bob", create: false);
        await using var player = again;
        Assert.Equal(bobRole, role);
        var letter = Row(Assert.Single(await Letters(again, role)));
        Assert.Equal("Hero Saga", letter["Title"]);
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        long before = session.Progress!.Currency.GetValueOrDefault(item);
        Assert.Equal(0, (await Claim(again, (string)letter["MailId"]!)).Result);
        Assert.Equal(before + count, session.Progress!.Currency.GetValueOrDefault(item));
    }

    private static async Task Reply(Ro3Emu.Bot.BotClient bot, string text)
    {
        try
        {
            await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
                && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(text, StringComparison.Ordinal), Wait);
        }
        catch (TimeoutException)
        {
            var seen = bot.Inbox.Snapshot().Where(e => e.IsGame(GmCommands.BroadcastNotify)).Select(e => BroadCastNotify.Parser.ParseFrom(e.Body).Content);
            throw new TimeoutException("replies: " + string.Join(" | ", seen));
        }
    }
}
