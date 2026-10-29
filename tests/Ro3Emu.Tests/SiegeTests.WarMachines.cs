using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>War machines in a running siege: reserving one from the guild warehouse (32270) and giving it back (32272).</summary>
public sealed partial class SiegeTests
{
    private static async Task<GuildWartimeStorePrepayResp> Prepay(BotClient bot, int item)
    {
        await bot.SendGameAsync(GuildService.Prepay, new GuildWartimeStorePrepayReq { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        return await Next(bot, GuildService.Prepay, GuildWartimeStorePrepayResp.Parser);
    }

    private static async Task<int> Stocked(BotClient bot, int item, int count) =>
        (await Next(bot, GuildService.WartimeStore, GuildWartimeStoreItemNtf.Parser,
            n => n.WartimeItems.Any(i => i.ItemId == item && i.ItemCount == count))).WartimeItems.Single(i => i.ItemId == item).ItemCount;

    private async Task<(BotClient Attack, BotClient Defense, long Ga)> SiegeWithFirearm(string tag)
    {
        var attack = await Bot("SiegeGm"); // the fixture's GM account seeds the siege
        var defense = await Bot("SiegeWm" + tag);
        long ga = await Guild(attack, "SgWm" + tag), gd = await Guild(defense, "SgWd" + tag);
        await attack.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
        { ToolType = WartimeToolType.Firearm, BagIndex = 1, RoleGuildId = Session(attack).Role!.Id });
        Assert.Equal(0, (await Next(attack, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser)).Result);
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + 25);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Base(attack, currentCity: 1001);
        return (attack, defense, ga);
    }

    [ClientDataFact]
    public async Task AFirearmOperatorReservesACannonInTheSiegeAndGivesItBack()
    {
        var (attack, defense, ga) = await SiegeWithFirearm("A");
        await using var _ = attack;
        await using var __ = defense;

        // Outside the siege nothing is reserved.
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Prepay(attack, 1006001)).Result);
        await Enter(attack, 1); await Enter(defense, 2);

        // One cannon leaves the warehouse; the answer starts the blueprint and every member sees the stock.
        var held = await Prepay(attack, 1006001);
        Assert.Equal(0, held.Result);
        Assert.Equal((1006001, 1), (held.PrepayItem.ItemId, held.PrepayItem.ItemCount));
        Assert.Equal(49, await Stocked(attack, 1006001, 49));
        GuildStore.State Guild() => Service<GuildStore>().Get(ga)!;
        Assert.Equal(1006001, Guild().WartimePrepay[Session(attack).Role!.Id]);

        // A second reservation replaces the first: the guild never holds two for one member.
        Assert.Equal(0, (await Prepay(attack, 1006002)).Result);
        await Stocked(attack, 1006001, 50);
        Assert.Equal(1006002, Guild().WartimePrepay[Session(attack).Role!.Id]);

        // Only cannons, only with the Firearm permission, only from stock.
        Assert.Equal(ErrorCodes.Parameter, (await Prepay(attack, 1006005)).Result);
        Assert.Equal(ErrorCodes.GuildPermission, (await Prepay(defense, 1006001)).Result);
        await OnLoop(() => { var g = Guild(); g.WartimeItems[1006004] = 0; Assert.True(Service<GuildStore>().Save(g)); return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.GuildSupplies, (await Prepay(attack, 1006004)).Result);
        Assert.Equal(1006002, Guild().WartimePrepay[Session(attack).Role!.Id]); // a refused one keeps the held one

        // Esc on the blueprint gives it back; with nothing held the answer is still 0.
        await attack.SendGameAsync(GuildService.CancelPrepay, new GuildWartimeStoreItemCancelUseReq());
        var back = await Next(attack, GuildService.CancelPrepay, GuildWartimeStoreItemCancelUseResp.Parser);
        Assert.Equal((0, 1006002), (back.Result, back.Item.ItemId));
        await Stocked(attack, 1006002, 50);
        Assert.Empty(Guild().WartimePrepay);
        await attack.SendGameAsync(GuildService.CancelPrepay, new GuildWartimeStoreItemCancelUseReq());
        Assert.Equal(0, (await Next(attack, GuildService.CancelPrepay, GuildWartimeStoreItemCancelUseResp.Parser)).Result);
    }
}
