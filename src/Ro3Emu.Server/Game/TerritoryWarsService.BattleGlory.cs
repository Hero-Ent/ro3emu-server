using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Battle Merit (Battle Glory, function 7205, open at base level 99): the merit points (currency 15, "War Merit
/// Points") a role earns in the public territory PvP, the merit rank they reach, and its rank rewards.
/// <list type="bullet">
/// <item>5001 AUTO_SYNC DBBattleGloryInfo (LC_DAL_BattleGlory): the client reads curRankId, isSpecialRank and the keys of
/// rankRewardReceiveInfo; dayGainScore and sceneInfo are never read.</item>
/// <item>BattleGloryDan: a rank needs the sum of the _iGloryCondition of the ranks below it
/// (Lua_CFG_BattleGloryDan.GetNextGloryTotal): 1000 for rank 2 up to 36000 for rank 9. Rank 10 (_iRankCondition 10)
/// is the champion rank of leaderboard 2102, which this client ships no LeaderBoardsConfig row for: it is never set.</item>
/// <item>83400 BattleGloryRankRewardReq {rankReward: {level: 1}} claims a reached rank's _iNormalRewardGroup once
/// (UI_Sys_TerritoryWars_BattleFeatsRewardListWidgetCtrl: ranks 2-9, claimable at curRankId). The client has no
/// handler for the reply; it reads the claim from 5001.</item>
/// </list>
/// Only the client's display is in the client; the rules are the server's (inferred). The points come from the maps
/// with a BattleGloryBase row (30301-30319), between WorldHostile players: a kill pays _iKillBase, a hit on the
/// victim in the last 10 s an assist's _iAssistBase, a death _iDeathBase. Each is scaled by BattleGloryKillDif /
/// BattleGloryDeathDif by how many times today the same pair met that way (the 6th pays nothing), kills and assists
/// also by BattleGloryPowerDif of the victim's power minus the earner's. A death pays too (inferred: the record source
/// enum names Kill, Assist and Death as ways points came). PublicParam BattleGloryDailyLimit caps a game day (05:00).
/// The rank follows the season's earned points and never drops. The state is the role's territory RoleState, per
/// season like its honor.
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const uint GlorySync = 5001, GloryRankReward = 83400;
    public const int GloryFunction = 7205, GloryCurrency = 15;
    private const long GloryAssistWindowMs = 10_000;

    /// <summary>One season's Battle Merit of a role.</summary>
    public sealed class GloryState
    {
        public string Epoch { get; set; } = "";
        public long Points { get; set; }
        public long Rank { get; set; } = 1;
        public HashSet<long> Claimed { get; set; } = [];
        public int Day { get; set; }
        public long DayGain { get; set; }
        /// <summary>Today's meetings per "kind:other role" (the KillDif / DeathDif count).</summary>
        public Dictionary<string, int> Counts { get; set; } = [];
    }

    private enum GloryKind { Kill = 1, Assist = 2, Death = 3 }

    /// <summary>Who hit whom last in the public PvP maps (victim, attacker) -> ms, for assists.</summary>
    private readonly Dictionary<long, Dictionary<long, long>> _gloryHits = [];

    private int GloryDay => DateOnly.FromDateTime(DailyActivityService.GameDay(_clock.LocalNow.DateTime)).DayNumber;

    /// <summary>The role's Battle Merit for this season and game day (a new season or day starts over).</summary>
    private GloryState Glory(RoleState role)
    {
        var glory = role.Glory;
        if (glory.Epoch != Epoch) glory = role.Glory = new GloryState { Epoch = Epoch };
        if (glory.Day != GloryDay) { glory.Day = GloryDay; glory.DayGain = 0; glory.Counts.Clear(); }
        return glory;
    }

    private Task SyncGloryAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null || !_functions.IsOpen(s, GloryFunction)) return Task.CompletedTask;
        var glory = Glory(Copy(s.TerritoryWars ?? LoadRole(s.Role.Id)));
        var info = new DBBattleGloryInfo { CurRankId = glory.Rank, DayGainScore = glory.DayGain };
        foreach (long rank in glory.Claimed) info.RankRewardReceiveInfo[rank] = 1;
        return CharacterService.SyncAsync(s, GlorySync, info);
    }

    private List<BattleGloryDan> GloryRanks() =>
        [.. _data.Table("BattleGloryDan").Values.Select(r => new BattleGloryDan(r)).OrderBy(r => r.Id)];

    /// <summary>The highest rank whose summed conditions the points reach (rank 10 is the leaderboard's).</summary>
    private long GloryRankFor(long points)
    {
        long rank = 1, needed = 0;
        foreach (var dan in GloryRanks().Where(d => d.RankCondition == 0))
        {
            if (dan.Id > 1)
            {
                if (needed > points) break;
                rank = dan.Id;
            }
            needed += dan.GloryCondition;
        }
        return rank;
    }

    private long GloryCoe(string table, int count) => _data.Table(table).Values
        .Select(r => table == "BattleGloryKillDif" ? (new BattleGloryKillDif(r).Count, new BattleGloryKillDif(r).Coe)
            : (new BattleGloryDeathDif(r).Count, new BattleGloryDeathDif(r).Coe))
        .Where(r => r.Item1 <= count).OrderByDescending(r => r.Item1).Select(r => r.Item2).FirstOrDefault(10000);

    /// <summary>BattleGloryPowerDif: the row with the largest threshold the difference reaches, on its side.</summary>
    private long GloryPowerCoe(long difference) => _data.Table("BattleGloryPowerDif").Values
        .Select(r => new BattleGloryPowerDif(r))
        .Where(r => difference >= 0 ? r.Battle > 0 && r.Battle <= difference : r.Battle < 0 && r.Battle >= difference)
        .OrderByDescending(r => Math.Abs(r.Battle)).Select(r => r.Coe).FirstOrDefault(10000);

    private static long GloryPower(GameSession s) => PowerService.Total(PowerService.Current(s));

    private BattleGloryBase? GloryMap(GameSession s) => s.InstanceId == 0 && s.LineNumber == 1
        && _data.Row("BattleGloryBase", s.SceneEntry) is { } row ? new BattleGloryBase(row) : null;

    /// <summary>CombatService.PvpDealtAsync: the hit an assist needs.</summary>
    private Task GloryHitAsync(GameSession source, GameSession target, long amount, bool lethal)
    {
        if (amount > 0 && source.Role is { } attacker && target.Role is { } victim && GloryMap(source) != null)
        {
            if (!_gloryHits.TryGetValue(victim.Id, out var hits)) _gloryHits[victim.Id] = hits = [];
            hits[attacker.Id] = MonsterService.NowMs;
        }
        return Task.CompletedTask;
    }

    /// <summary>CombatService.PlayerKilled: the killer's, the assists' and the victim's merit points.</summary>
    private async Task GloryKilledAsync(GameSession killer, GameSession victim)
    {
        if (killer.Role is not { } k || victim.Role is not { } v) return;
        _gloryHits.Remove(v.Id, out var hits);
        if (GloryMap(killer) is not { } map || !WorldHostile(killer, victim)) return;
        long now = MonsterService.NowMs;
        await AwardGloryAsync(killer, GloryKind.Kill, v.Id, map.KillBase, GloryPower(victim) - GloryPower(killer));
        foreach (var (id, at) in hits ?? [])
            if (id != k.Id && now - at <= GloryAssistWindowMs
                && _world.PlayersIn(killer.WorldScene).FirstOrDefault(p => p.Role?.Id == id) is { } helper && WorldHostile(helper, victim))
                await AwardGloryAsync(helper, GloryKind.Assist, v.Id, map.AssistBase, GloryPower(victim) - GloryPower(helper));
        await AwardGloryAsync(victim, GloryKind.Death, k.Id, map.DeathBase, null);
    }

    /// <summary>One earner's points: scaled, capped for the day, granted with the role state as one receipt.</summary>
    private async Task AwardGloryAsync(GameSession s, GloryKind kind, long other, long basePoints, long? powerDifference)
    {
        if (s.Role is not { } role) return;
        var attachment = s.Attachment;
        using var reservation = await EnterGateAsync("territory/battle glory", s);
        using var stagedTerritoryState = StageState();
        try
        {
            using var held = await CharacterService.Lock(s);
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment) || s.Progress == null
                || !_functions.IsOpen(s, GloryFunction)) return;
            var next = Copy(s.TerritoryWars ?? LoadRole(role.Id));
            var glory = Glory(next);
            string key = $"{(int)kind}:{other}";
            int count = glory.Counts[key] = glory.Counts.GetValueOrDefault(key) + 1;
            long coe = GloryCoe(kind == GloryKind.Death ? "BattleGloryDeathDif" : "BattleGloryKillDif", count);
            long points = basePoints * coe / 10000;
            if (powerDifference is { } difference) points = points * GloryPowerCoe(difference) / 10000;
            long limit = _data.Table("PublicParamConfig").TryGetValue("BattleGloryDailyLimit", out var cap)
                ? (long)(Data.GameData.Number(cap) ?? 1_000_000) : 1_000_000;
            points = Math.Clamp(points, 0, Math.Max(0, limit - glory.DayGain));
            glory.Points += points; glory.DayGain += points;
            glory.Rank = Math.Max(glory.Rank, GloryRankFor(glory.Points));
            if (points == 0)
            {
                await SaveRoleAsync(role.Id, next);
                s.TerritoryWars = next;
                return;
            }
            var grants = new List<AwardGrant> { new(GloryCurrency, (int)points, 0) };
            string source = $"battle-glory:{Epoch}:{role.Id}:{kind}:{other}:{Guid.NewGuid():N}";
            if (!await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants), persist: tx =>
                {
                    SaveRole(role.Id, next, tx);
                    tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "battle-glory", source, [], grants,
                        new { Kind = kind.ToString(), Other = other, Points = points, glory.Rank }));
                }, stateLockHeld: true, committed: () => s.TerritoryWars = next))
                return;
            LogGloryEarned(_log, role.Id, points, (int)kind, other, glory.Rank);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await SyncGloryAsync(s);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Role {Role} earns {Points} Battle Merit (kind {Kind} of {Other}), rank {Rank}")]
    private static partial void LogGloryEarned(ILogger logger, long role, long points, int kind, long other, long rank);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Battle Merit rank {Rank} reward unresolved for {Role}: {Reason}")]
    private static partial void LogGloryRewardUnresolved(ILogger logger, int rank, long role, string reason);

    /// <summary>83400: a reached rank's reward, once.</summary>
    private async Task GloryRewardAsync(GameSession s, BattleGloryRankRewardReq req)
    {
        var response = new BattleGloryRankRewardResp();
        using (await CharacterService.Lock(s))
            foreach (int level in req.RankReward.Keys.Order())
            {
                int result = await ClaimGloryRankAsync(s, level);
                response.ReceiveResult[level] = result;
                if (result != 0 && response.Result == 0) response.Result = result;
            }
        await SyncGloryAsync(s);
        await Send(s, GloryRankReward, response);
    }

    private async Task<int> ClaimGloryRankAsync(GameSession s, int level)
    {
        if (s.Role is not { } role || s.Progress == null) return ErrorCodes.Unknown;
        if (!_functions.IsOpen(s, GloryFunction)) return ErrorCodes.FunctionLocked;
        var dan = GloryRanks().FirstOrDefault(d => d.Id == level);
        if (dan is not { NormalRewardGroup: > 0, RankCondition: 0 }) return ErrorCodes.RewardMissing;
        var next = Copy(s.TerritoryWars ?? LoadRole(role.Id));
        var glory = Glory(next);
        if (glory.Claimed.Contains(level)) return ErrorCodes.RewardClaimed;
        if (glory.Rank < level) return ErrorCodes.NoRankReward;
        glory.Claimed.Add(level);
        int? playBook = s.Synced.GetValueOrDefault(SeasonService.SeasonSync) is DBSeasonInfo { Current: { } current }
            ? (int)current.PlayBookId : null;
        AwardPlan plan;
        try { plan = _awards.Resolve(s, dan.NormalRewardGroup, playBook); }
        catch (AwardResolutionException e)
        {
            LogGloryRewardUnresolved(_log, level, role.Id, e.Message);
            return ErrorCodes.RewardMissing;
        }
        string source = $"battle-glory-rank:{Epoch}:{role.Id}:{level}";
        return await _character.ApplyAwardAsync(s, plan, persist: tx =>
            {
                SaveRole(role.Id, next, tx);
                if (plan.Grants.Any(g => g.Entry is not (100 or 101)))
                    tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "battle-glory-rank", source, [], plan.Grants,
                        new { Rank = level, Award = dan.NormalRewardGroup, plan.AwardId }));
            }, stateLockHeld: true, committed: () => s.TerritoryWars = next) ? 0 : ErrorCodes.Unknown;
    }
}
