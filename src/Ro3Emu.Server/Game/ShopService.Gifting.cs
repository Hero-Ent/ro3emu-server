using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 82335/82336/82337/82339: gifting a Fashion-tab commodity to a friend (LC_DAL_Shop.lua CommodityPresent/Pick).
/// The gift card itself is a normal private chat message the CLIENT builds and sends after a successful
/// CommodityPresentResp (LC_DAL_Shop.lua:1176-1216, GenSendGiftToFriendMessageExtraParam); the server's job is only
/// the order: pay the sender, hold it until the recipient (friend only, ErrorCodeConfig 2312) picks it, then unlock
/// the appearance (and its _iAppearanceAwardId bonus, like a normal purchase) for them. PublicParamConfig
/// .OrderExpireTime (86400s) and FriendsGiftTextMaxNum (20) are the client's own limits; PresentRefundMailId (1115)
/// is the mail template an expired, unclaimed order's payment comes back on.
///
/// (inferred) The order record itself (everything but the recipient's sender/message display, which is the wire
/// DBBattlePassInfo.pendingGiftMap) is not specified by any client-visible field, so it is kept as a role blob
/// ("gifts") on both sender and recipient. Settlement of an expired order is lazy and one-sided: it only runs
/// against the role whose own session is acting (present, pick or login), since WardrobeService's family-economy
/// transaction (and a plain blob write outside one) only ever lets this code touch the acting session's own role
/// state; mailing the refund is the one part allowed to reach the other side, the same way guild mail already
/// reaches an offline role.
/// </summary>
public sealed partial class ShopService
{
    public const uint CommodityPick = 82336, CommodityPresentListNtf = 82337, SentGiftsListNtf = 82339;

    public sealed class GiftOrder
    {
        public long OrderId { get; set; }
        public long SenderId { get; set; }
        public string SenderName { get; set; } = "";
        public long RecipientId { get; set; }
        public string RecipientName { get; set; } = "";
        public int CommodityId { get; set; }
        public long AppearanceId { get; set; }
        public long AppearanceAwardId { get; set; }
        public int CurrencyId { get; set; }
        public int CurrencyCount { get; set; }
        public string GiftContent { get; set; } = "";
        public long CreatedUtc { get; set; }
        /// <summary>enum_FriendGiftTakenState: 1 WaitTake, 2 AlreadyTake, 3 OutTime.</summary>
        public int State { get; set; }
    }

    public sealed class GiftState
    {
        /// <summary>Orders this role must still pick, keyed by order id.</summary>
        public Dictionary<long, GiftOrder> Received { get; set; } = new();
        /// <summary>Orders this role sent, kept for its own sent-gifts list until pruned.</summary>
        public Dictionary<long, GiftOrder> Sent { get; set; } = new();
    }

    private const int WaitTake = 1, AlreadyTake = 2, OutTime = 3;
    private const int GiftMessageMaxLength = 20; // PublicParamConfig.FriendsGiftTextMaxNum
    private const int GiftRefundMailTemplate = 1115; // PublicParamConfig.PresentRefundMailId
    private static readonly TimeSpan GiftOrderLifetime = TimeSpan.FromSeconds(86400); // PublicParamConfig.OrderExpireTime
    /// <summary>Resolved orders older than this are dropped from a sent-gifts list so the blob does not grow forever.</summary>
    private static readonly TimeSpan GiftHistoryRetention = TimeSpan.FromDays(30);

    private GiftState LoadGifts(long role) =>
        _roles.LoadGifts(role) is { } bytes ? JsonSerializer.Deserialize<GiftState>(bytes) ?? new() : new();

    private static long NewOrderId() =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())), 0) & long.MaxValue;

    private bool Expired(GiftOrder order) => Now - order.CreatedUtc > GiftOrderLifetime.TotalSeconds;

    /// <summary>82335 COMMODITY_PRESENT: buy a Fashion commodity as a gift for a friend. Nothing is granted now; the
    /// recipient picks it up with 82336.</summary>
    public async Task PresentAsync(GameSession session, CommodityPresentReq req)
    {
        long recipientId = req.TargetId;
        using var guard = await CharacterService.Lock(session, [Ownership.ReservationKey.Role(recipientId, "gifts")]);
        SettleExpiredLocked(session); // this role's own stale received orders, unrelated to this present
        var (code, orderId) = await PresentLockedAsync(session, req, recipientId);
        await session.SendGameAsync(CommodityPresent, new CommodityPresentResp
        {
            Result = code, OrderId = orderId, CommodityId = req.BuyReq?.CommodityId ?? 0, TargetId = recipientId,
            GiftContent = req.GiftContent,
        });
    }

    private async Task<(int Code, long OrderId)> PresentLockedAsync(GameSession session, CommodityPresentReq req, long recipientId)
    {
        var buy = req.BuyReq;
        if (buy == null || buy.Count != 1) return (ErrorCodes.Parameter, 0);
        if (recipientId == session.Role!.Id) return (ErrorCodes.ShopGiftSelf, 0);
        if ((req.GiftContent?.Length ?? 0) > GiftMessageMaxLength) return (ErrorCodes.ShopGiftMessageLength, 0);
        if (_data.Row("ShopCommodityConfig", buy.CommodityId) is not { } row) return (ErrorCodes.ShopNoConfig, 0);
        long appearance = GameData.Int(row, "_iAppearanceId");
        if (appearance <= 0) return (ErrorCodes.ShopGiftNotAllowed, 0);
        if (Unavailable(session, row) is { } unavailable) return (unavailable.Code, 0);
        int unit = UnitPrice(session, row), currency = PaymentItem(row);
        if (Malformed(row, (int)appearance, 1, unit) is { } malformed) return (malformed.Code, 0);
        if (await _roles.GetAsync(recipientId) is not { } recipient) return (ErrorCodes.Parameter, 0);
        if (!await _friends.AreFriendsAsync(session.Role.Id, recipientId)) return (ErrorCodes.ShopGiftFriendsOnly, 0);
        if (await RecipientOwnsAsync(recipientId, (int)appearance)) return (ErrorCodes.ShopAppearanceOwned, 0);

        var recipientGifts = LoadGifts(recipientId);
        if (recipientGifts.Received.Values.Any(o => o.State == WaitTake && !Expired(o) && o.AppearanceId == appearance))
            return (ErrorCodes.ShopGiftDuplicate, 0);

        var order = new GiftOrder
        {
            OrderId = NewOrderId(), SenderId = session.Role.Id, SenderName = session.Role.Name,
            RecipientId = recipientId, RecipientName = recipient.Name, CommodityId = buy.CommodityId,
            AppearanceId = appearance, AppearanceAwardId = GameData.Int(row, "_iAppearanceAwardId"),
            CurrencyId = currency, CurrencyCount = unit, GiftContent = req.GiftContent ?? "",
            CreatedUtc = Now, State = WaitTake,
        };
        var senderGifts = LoadGifts(session.Role.Id);
        PruneSentHistory(senderGifts);
        senderGifts.Sent[order.OrderId] = order;
        recipientGifts.Received[order.OrderId] = order;
        var cost = currency > 0 && unit > 0 ? new[] { (currency, (long)unit) } : [];
        var paid = await _character.ExchangeLockedAsync(session, cost, [], persist: tx =>
        {
            tx.RoleBlob(session.Role.Id, "gifts", JsonSerializer.SerializeToUtf8Bytes(senderGifts));
            tx.RoleBlob(recipientId, "gifts", JsonSerializer.SerializeToUtf8Bytes(recipientGifts));
        }, asyncCommit: true);
        if (paid == null) return (_character.ShortageOf(session, cost), 0);

        _log.LogInformation("{Name} gifts appearance {Appearance} (commodity {Commodity}) to {Recipient}: order {Order}",
            session.Role.Name, appearance, buy.CommodityId, recipient.Name, order.OrderId);
        await PushSentAsync(session);
        PushReceivedIfOnline(recipientId);
        return (0, order.OrderId);
    }

    /// <summary>Whether another role already owns (and still holds, if timed) this appearance: read-only, no lock
    /// needed since it is someone else's state being peeked, not mutated.</summary>
    private async Task<bool> RecipientOwnsAsync(long recipientId, int appearanceId) =>
        await _roles.LoadWardrobeAsync(recipientId) is { } bytes
        && DBWardRobeInfo.Parser.ParseFrom(bytes).AllLightUpAppearances.TryGetValue(appearanceId, out var owned)
        && (owned.OverdueTimeMs == 0 || owned.OverdueTimeMs > _clock.GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>82336 COMMODITY_PICK: the recipient claims a gift order and unlocks its appearance (and the
    /// commodity's own bonus award, same as buying it directly). The sender's own copy only needs a lock (and a
    /// write) when an order is genuinely found and picked.</summary>
    public async Task PickAsync(GameSession session, CommodityPickReq req)
    {
        // A peek (unlocked, stale is fine) just to know which sender's "gifts" part to reserve; PickLockedAsync
        // re-reads everything fresh once the lock (and that reservation) is held.
        long senderId = LoadGifts(session.Role!.Id).Received.GetValueOrDefault(req.OrderId)?.SenderId ?? 0;
        var keys = senderId > 0 ? new[] { Ownership.ReservationKey.Role(senderId, "gifts") } : [];
        using var guard = await CharacterService.Lock(session, keys);
        SettleExpiredLocked(session); // may remove req.OrderId itself if it just expired; the pick below then misses it
        int code = await PickLockedAsync(session, req.OrderId);
        await session.SendGameAsync(CommodityPick, new CommodityPickResp { Result = code });
    }

    private async Task<int> PickLockedAsync(GameSession session, long orderId)
    {
        var own = LoadGifts(session.Role!.Id);
        if (!own.Received.TryGetValue(orderId, out var order)) return ErrorCodes.ShopGiftOrderMissing;
        if (_data.Row("ShopCommodityConfig", order.CommodityId) is not { } row) return ErrorCodes.ShopNoConfig;

        AwardPlan bonus;
        try { bonus = _awards.Resolve(session, order.AppearanceAwardId, (int)(session.SeasonState?.PlayBookId ?? _seasons.PlayBook)); }
        catch (Exception e) when (e is AwardResolutionException or OverflowException)
        {
            _log.LogWarning(e, "Gift {Order} award {Award} did not resolve", orderId, order.AppearanceAwardId);
            return ErrorCodes.ShopRewardMissing;
        }

        // The sender's own copy is a plain, unowned blob write (RoleStore.SaveGifts), not part of this transaction:
        // WardrobeService.BuyFromShopLockedAsync's family-economy plan only allows the picker's own role state.
        int code = await _wardrobe.BuyFromShopLockedAsync(session, (int)order.AppearanceId, Parts(row), cost: [], bonus,
            persistShop: tx =>
            {
                own.Received.Remove(orderId);
                tx.RoleBlob(session.Role.Id, "gifts", JsonSerializer.SerializeToUtf8Bytes(own));
            },
            committedShop: () => { }, shopOutcome: new { Gift = order.OrderId, Sender = order.SenderId, Appearance = order.AppearanceId });
        if (code != 0) return code;

        var sender = LoadGifts(order.SenderId);
        if (sender.Sent.TryGetValue(orderId, out var sent)) sent.State = AlreadyTake;
        _roles.SaveGifts(order.SenderId, JsonSerializer.SerializeToUtf8Bytes(sender));

        _log.LogInformation("{Name} picks gift {Order} from {Sender}: appearance {Appearance}",
            session.Role.Name, orderId, order.SenderName, order.AppearanceId);
        await PushReceivedAsync(session);
        PushSentIfOnline(order.SenderId);
        return 0;
    }

    /// <summary>
    /// This role's own expired, still-unclaimed received orders: removed and persisted immediately (so a later
    /// failure in whatever triggered this cannot make the same order "expire" twice), then refunded to each sender
    /// by mail (ErrorCodeConfig.PresentRefundMailId), the same way guild mail already reaches an offline role. Only
    /// ever touches the acting session's own role state directly; the mail is the one part allowed to reach another.
    /// </summary>
    private void SettleExpiredLocked(GameSession session)
    {
        var own = LoadGifts(session.Role!.Id);
        var stale = own.Received.Values.Where(o => o.State == WaitTake && Expired(o)).ToList();
        if (stale.Count == 0) return;
        foreach (var order in stale) own.Received.Remove(order.OrderId);
        _roles.SaveGifts(session.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(own));
        foreach (var order in stale) _ = RefundExpiredAsync(order);
    }

    private async Task RefundExpiredAsync(GiftOrder order)
    {
        try
        {
            var attachments = order.CurrencyId > 0 && order.CurrencyCount > 0
                ? new[] { new MailService.Attachment(order.CurrencyId, order.CurrencyCount) } : [];
            if (attachments.Length > 0) await _mail.SendTemplateAsync(order.SenderId, GiftRefundMailTemplate, attachments);
            _log.LogInformation("Gift {Order} from {Sender} to {Recipient} expired unclaimed: refunded by mail",
                order.OrderId, order.SenderId, order.RecipientId);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Gift {Order} from {Sender} expired, but the refund mail failed", order.OrderId, order.SenderId);
        }
    }

    /// <summary>Drops this role's own old resolved (picked or expired) sent gifts so the blob does not grow forever.
    /// Never touches a received order: those are only ever removed by <see cref="SettleExpiredLocked"/> or a pick.</summary>
    private void PruneSentHistory(GiftState state)
    {
        foreach (long id in state.Sent.Values.Where(o => o.State != WaitTake && Now - o.CreatedUtc > GiftHistoryRetention.TotalSeconds)
            .Select(o => o.OrderId).ToList())
            state.Sent.Remove(id);
    }

    private Task PushSentAsync(GameSession session) =>
        session.SendGameAsync(SentGiftsListNtf, new SentGiftsListNtf
        {
            SentGifts = { LoadGifts(session.Role!.Id).Sent.Values.OrderByDescending(o => o.CreatedUtc).Select(ToSentGiftInfo) },
        });

    private Task PushReceivedAsync(GameSession session) =>
        session.SendGameAsync(CommodityPresentListNtf, new CommodityPresentListNtf
        {
            Orders = { LoadGifts(session.Role!.Id).Received.Values.Where(o => !Expired(o)).OrderByDescending(o => o.CreatedUtc).Select(ToPresentOrderInfo) },
        });

    private void PushReceivedIfOnline(long roleId)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == roleId) is { } recipient)
            _loop.PostOwned(recipient, () => recipient.Role != null ? PushReceivedAsync(recipient) : Task.CompletedTask);
    }

    private void PushSentIfOnline(long roleId)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == roleId) is { } sender)
            _loop.PostOwned(sender, () => sender.Role != null ? PushSentAsync(sender) : Task.CompletedTask);
    }

    private static PresentOrderClientInfo ToPresentOrderInfo(GiftOrder o) => new()
    {
        OrderId = o.OrderId, SenderId = o.SenderId, SenderName = o.SenderName, AppranceId = o.AppearanceId,
        AppranceAwardId = o.AppearanceAwardId, CreateTime = o.CreatedUtc, State = o.State, GiftContent = o.GiftContent,
    };

    /// <summary>Effective state at read time: an unclaimed order past its lifetime displays OutTime even in the rare
    /// window before the recipient's own session settles (and refunds) it.</summary>
    private SentGiftInfo ToSentGiftInfo(GiftOrder o) => new()
    {
        OrderId = o.OrderId, RecipientId = o.RecipientId, RecipientName = o.RecipientName, AppranceId = o.AppearanceId,
        AppranceAwardId = o.AppearanceAwardId, CreateTime = o.CreatedUtc,
        State = o.State == WaitTake && Expired(o) ? OutTime : o.State, GiftContent = o.GiftContent,
        CurrencyId = o.CurrencyId, CurrencyCount = o.CurrencyCount,
    };

    /// <summary>Login: push both lists so a reconnect sees pending gifts without waiting for a mutation. Expired-order
    /// settlement is not run here: ShopService.LoginAsync is not under CharacterService.Lock, and the next present or
    /// pick settles it anyway.</summary>
    private async Task LoginGiftsAsync(GameSession session)
    {
        await PushReceivedAsync(session);
        await PushSentAsync(session);
    }
}
