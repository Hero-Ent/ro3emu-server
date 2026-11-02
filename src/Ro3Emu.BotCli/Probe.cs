using System.Globalization;
using Google.Protobuf;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Romsg;

/// <summary>
/// Logs into an existing account and role and runs test commands against a live (local) server, printing what the
/// server answers. usage: Ro3Emu.BotCli probe &lt;server http base&gt; &lt;account&gt; &lt;password&gt; &lt;roleName&gt;, then
/// commands on stdin (or a piped file):
///   skills                      the skill dataset (5025) as the server last sent it
///   load|unload &lt;skill&gt; &lt;socket&gt; [level]   manual skill bar (skill window) load / unload
///   cast &lt;skill&gt; [target uid]  cast; prints the reply, the cast start and any refusal
///   mobs                        units in view other than players
///   burst &lt;skill&gt; &lt;target uid&gt; &lt;count&gt; &lt;interval ms&gt;   casts sent like a held key, without waiting for replies
///   refusals                    every cast reply (31102) and common error (2) so far
///   ids                         the message ids received so far, with counts
///   gm &lt;line&gt;                   a GM command as this account (the server's loopback /ro3emu/gm), e.g. gm !jump 100 80
///   interact &lt;npc uid&gt; &lt;interaction id&gt;
///   item &lt;item entry&gt;          use one item of that entry
///   treasure                    the treasure hunt cursor (5064) as last sent
///   wait &lt;secs&gt;
///   dump &lt;msg id&gt;              every message with that id received so far, raw size
/// </summary>
internal static class Probe
{
    private static readonly Dictionary<uint, MessageParser> Parsers = new()
    {
        [SkillService.SkillDataSync] = DBSkillDataIncrement.Parser,
        [SkillService.SkillLoad] = SkillLoadResp.Parser,
        [CombatService.UseSkill] = CastSkillRsp.Parser,
        [SceneService.NpcInteraction] = NpcInteractionRsp.Parser,
        [CharacterService.ItemUse] = ItemUseResp.Parser,
        [5064] = DBTreasureInfoIncrement.Parser,
        [2] = CommonErrorNtf.Parser,
    };

    private static string _http = "", _account = "";

    public static async Task<int> RunAsync(string[] args, string dataDir)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: Ro3Emu.BotCli probe <server http base> <account> <password> <roleName>");
            return 2;
        }
        _http = args[0];
        _account = args[1];
        await using var bot = new BotClient(args[0], File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(args[1], args[2]);
        await bot.EnterGateAsync();
        var role = bot.Roles.FirstOrDefault(r => r.Name == args[3]);
        if (role == null)
        {
            Console.WriteLine($"no role {args[3]} on this account: {string.Join(", ", bot.Roles.Select(r => r.Name))}");
            return 1;
        }
        var self = await bot.EnterWorldAsync(role.Id);
        Console.WriteLine($"{role.Name} ({role.Id}) in world at {self.MovementInfo.Pos}");
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            // A PowerShell pipe starts the input with a byte-order mark.
            var p = line.Trim().TrimStart('﻿').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith('#')) continue;
            Console.WriteLine("> " + line.Trim());
            try { await RunCommandAsync(bot, p); }
            catch (Exception e) { Console.WriteLine($"  failed: {e.GetType().Name}: {e.Message}"); }
        }
        return 0;
    }

    private static async Task RunCommandAsync(BotClient bot, string[] p)
    {
        switch (p[0])
        {
            case "skills": Print(Last(bot, SkillService.SkillDataSync)); break;
            case "treasure": Print(Last(bot, 5064)); break;
            case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(p[1], CultureInfo.InvariantCulture))); break;
            case "dump":
                foreach (var e in bot.Inbox.Snapshot().Where(e => e.IsGame(uint.Parse(p[1], CultureInfo.InvariantCulture))))
                    Print(e);
                break;
            case "load" or "unload":
                await AskAsync(bot, SkillService.SkillLoad, new SkillLoadReq
                {
                    OpType = p[0] == "load" ? SkillLoadOperation.SkillLoadOpLoad : SkillLoadOperation.SkillLoadOpUnload,
                    IsManual = true, SocketNum = Int(p[2]),
                    Info = new SkillData { SkillId = Int(p[1]), SkillLevel = p.Length > 3 ? Int(p[3]) : 1 },
                });
                break;
            case "cast":
                await AskAsync(bot, CombatService.UseSkill, new CastSkillReq
                {
                    SkillId = Int(p[1]), TargetId = p.Length > 2 ? long.Parse(p[2], CultureInfo.InvariantCulture) : 0,
                });
                break;
            case "mobs":
                foreach (var e in bot.Inbox.Snapshot().Where(e => e.IsGame(Ro3Emu.Server.Maps.GameAoiSink.SyncAoiView)))
                    foreach (var unit in AoIViewChangeData.Parser.ParseFrom(e.Body).Bases.Where(b => b.EntityType != 1))
                        Console.WriteLine($"  uid {unit.Uuid} type {unit.EntityType} template {unit.EntityTemplateID} {unit.Name} at {unit.MovementInfo?.Pos}");
                break;
            case "burst":
                // burst <skill> <target uid> <count> <interval ms>: what a held key sends (the client's 0.2 s hold tick).
                for (int i = 0, count = Int(p[3]), interval = Int(p[4]); i < count; i++)
                {
                    await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
                    {
                        SkillId = Int(p[1]), TargetId = long.Parse(p[2], CultureInfo.InvariantCulture),
                    });
                    await Task.Delay(interval);
                }
                await Task.Delay(500);
                break;
            case "ids":
                Console.WriteLine("  " + string.Join(" ", bot.Inbox.Snapshot().Where(e => e.Kind == BotEventKind.Game)
                    .GroupBy(e => e.MsgId).Select(g => $"{g.Key}x{g.Count()}")));
                break;
            case "gm":
                // The server's loopback GM tool (ServerHost /ro3emu/gm), run as this account.
                using (var http = new HttpClient())
                {
                    var reply = await http.PostAsync($"{_http}/ro3emu/gm?account={_account}", new StringContent(string.Join(' ', p.Skip(1))));
                    Console.WriteLine($"  {(int)reply.StatusCode} {await reply.Content.ReadAsStringAsync()}");
                }
                await Task.Delay(1000);
                // The GM reply is a marquee (BroadcastNotify 83270); its text is the readable part of the body.
                if (Last(bot, GmCommands.BroadcastNotify) is { } said)
                    Console.WriteLine("  gm: " + new string(System.Text.Encoding.UTF8.GetString(said.Body).Where(c => !char.IsControl(c)).ToArray()));
                break;
            case "refusals":
                foreach (var e in bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseSkill) || e.IsGame(2))) Print(e);
                break;
            case "interact":
                await AskAsync(bot, SceneService.NpcInteraction, new NpcInteractionReq
                {
                    NpcUid = long.Parse(p[1], CultureInfo.InvariantCulture), InteractionId = Int(p[2]),
                });
                break;
            default: Console.WriteLine("  unknown command"); break;
        }
    }

    /// <summary>Sends the request and prints its reply plus whatever else arrived meanwhile (errors, syncs).</summary>
    private static async Task AskAsync(BotClient bot, uint msgId, IMessage request)
    {
        int seen = bot.Inbox.Snapshot().Count;
        await bot.SendGameAsync(msgId, request);
        try { await bot.Inbox.WaitAsync(e => e.IsGame(msgId), TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { Console.WriteLine("  no reply in 5 s"); }
        await Task.Delay(300);
        foreach (var e in bot.Inbox.Snapshot().Skip(seen).Where(e => e.Kind == BotEventKind.Game)) Print(e);
    }

    private static BotEvent? Last(BotClient bot, uint msgId) => bot.Inbox.Snapshot().LastOrDefault(e => e.IsGame(msgId));

    private static void Print(BotEvent? e)
    {
        if (e == null) { Console.WriteLine("  (nothing received)"); return; }
        Console.WriteLine(Parsers.TryGetValue(e.MsgId, out var parser)
            ? $"  {e.MsgId}: {parser.ParseFrom(e.Body)}"
            : $"  {e.MsgId}: {e.Body.Length} bytes");
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}
