using System.Collections.Concurrent;
using System.Globalization;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The GM command "!gvg": a whole castle siege played by fake players (docs/guide/fake-players.md "Guerra simulada").
/// Two guilds of third-class fake players at the top level (FakeBehavior.Warrior, run by the Ro3Emu.FakePlayers
/// process) are founded through the real protocol (create, auto-accept, apply); the siege's own test aids seed them on a
/// city and open it (!siege seed / open); their war warehouses get cannons, vehicles and repair kits; and every fake
/// player enters the siege and fights with its class's skills, focus fire, healers, cannons and war vehicles. Each guild
/// keeps room for real players (80 members, 70 fake by default), who join through the guild list (auto-accept) or
/// "!gvg entrar". "!gvg puxar" adds fake players to any guild, the GM's own included (the GM approves them when the guild
/// does not auto-accept). A GM test aid, not original behaviour.
/// </summary>
public sealed class GvgSimService
{
    private const int GuildCap = 80, DefaultPerSide = 70, DefaultCity = 1001;
    private const string Usage = "iniciar [porLado] [cidade] [minha=atacante|minha=defensor] | parar | status | entrar atacante|defensor | " +
        "maquina canhao|veiculo | puxar <quantidade> [minha|atacante|defensor|idGuilda]";
    /// <summary>The names the founders try (GuildNameNumNew allows 2-7 characters); a taken one gets a digit.</summary>
    private const string AttackName = "Ataque", DefenseName = "Defesa";

    /// <summary>
    /// The war warehouse of every guild in the simulation: the four cannons (SiegeInitItemList's 50 each), the three
    /// Madogears, the tank, the Peco Peco vehicle and the Madogear repair kit (ItemConfig 1006001-1006010). The game has no
    /// way to restock vehicles (docs/systems/territory-wars.md "Machine HP, destruction and repair"), so the test does.
    /// </summary>
    private static readonly Dictionary<int, int> WarStock = new()
    {
        [1006001] = 50, [1006002] = 50, [1006003] = 50, [1006004] = 50,
        [1006005] = 10, [1006006] = 10, [1006007] = 10, [1006008] = 6, [1006009] = 6, [1006010] = 10,
    };

    private readonly FakePlayerService _fakes;
    private readonly GmCommands _gm;
    private readonly GuildService _guilds;
    private readonly GameLoop _loop;
    private readonly SessionRegistry _sessions;
    private readonly ILogger<GvgSimService> _log;
    /// <summary>The role of each GM session that used "!gvg": a reconnect gives the GM a new session (the old one loses
    /// its role), and the war's later steps and answers go to the new one.</summary>
    private readonly ConcurrentDictionary<GameSession, long> _gmRoles = new();
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<long, GameSession> _approvers = new();
    private readonly ConcurrentQueue<(long Role, long Guild)> _applied = new();
    private readonly ConcurrentDictionary<string, DateTime> _failNotes = new();
    private TaskCompletionSource<long> _attackGuild = NewGuildWait(), _defenseGuild = NewGuildWait();
    private GameSession? _owner;
    private long _attack, _defense;
    private int _city = DefaultCity, _nextPull = FakeProfile.WarPullFirst;
    private bool _starting, _running;
    private string _warStatus = "";
    private DateTime _warStatusAt;
    private int _approving;

    public GvgSimService(FakePlayerService fakes, GmCommands gm, GuildService guilds, GameLoop loop, SessionRegistry sessions,
        ILogger<GvgSimService> log)
    {
        _fakes = fakes;
        _gm = gm;
        _guilds = guilds;
        _loop = loop;
        _sessions = sessions;
        _log = log;
        gm.Register("gvg", Usage + "  (guerra de guilda simulada com fake players)", CommandAsync);
        fakes.WarLine = OnWarLine;
    }

    private static TaskCompletionSource<long> NewGuildWait() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ------------------------------------------------------------------ command

    private async Task CommandAsync(GameSession s, string[] args)
    {
        if (s.Role is { } gm) _gmRoles[s] = gm.Id;
        string verb = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        switch (verb)
        {
            case "iniciar" or "start":
                await StartCommandAsync(s, args[1..]);
                return;
            case "parar" or "stop":
                await StopCommandAsync(s);
                return;
            case "status":
                await GmCommands.ReplyAsync(s, StatusText());
                return;
            case "entrar" or "join":
                await JoinCommandAsync(s, args.Length > 1 ? args[1].ToLowerInvariant() : "");
                return;
            case "puxar" or "pull":
                await PullCommandAsync(s, args[1..]);
                return;
            case "maquina" or "máquina" or "machine":
                await MachineCommandAsync(s, args.Length > 1 ? args[1].ToLowerInvariant() : "");
                return;
            default:
                throw new ArgumentException(Usage);
        }
    }

    private async Task StartCommandAsync(GameSession s, string[] args)
    {
        int perSide = DefaultPerSide, city = DefaultCity;
        char mine = ' ';
        var numbers = new List<int>();
        foreach (string arg in args)
        {
            string a = arg.ToLowerInvariant();
            if (a is "minha=atacante" or "minha=ataque" or "mine=attack") mine = 'A';
            else if (a is "minha=defensor" or "minha=defesa" or "mine=defense") mine = 'D';
            else if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) numbers.Add(n);
            else throw new ArgumentException(Usage);
        }
        if (numbers.Count > 0) perSide = numbers[0];
        if (numbers.Count > 1) city = numbers[1];
        if (perSide < 2 || perSide > GuildCap - 1)
            throw new ArgumentException($"porLado entre 2 e {GuildCap - 1} (a guilda tem {GuildCap} vagas; o padrao {DefaultPerSide} deixa 10 livres)");
        if (s.Role == null) throw new ArgumentException("entre com um personagem primeiro");
        lock (_sync)
        {
            if (_starting)
            {
                _ = GmCommands.ReplyAsync(s, "Guerra simulada: ja esta sendo preparada; veja !gvg status.");
                return;
            }
            _starting = true;
            _owner = s;
            _city = city;
        }
        await GmCommands.ReplyAsync(s, $"Guerra simulada: preparando {perSide} por lado na cidade {city}. Leva alguns minutos; acompanhe com !gvg status.");
        _ = Task.Run(() => StartAsync(s, perSide, city, mine));
    }

    /// <summary>
    /// Off the loop: accounts and process, the founders and members of both sides, the guilds they report, then the siege
    /// test aids (close any running test siege, seed the city with the two guilds, open it), the war warehouses and "wargo".
    /// </summary>
    private async Task StartAsync(GameSession s, int perSide, int city, char mine)
    {
        try
        {
            _fakes.SendLine("warstop");
            lock (_sync)
            {
                _running = false;
                _attack = _defense = 0;
                _attackGuild = NewGuildWait();
                _defenseGuild = NewGuildWait();
            }
            long myGuild = mine == ' ' ? 0 : await OnLoopAsync(() => _guilds.GuildOf(s.Role?.Id ?? 0));
            if (mine != ' ' && myGuild == 0)
            {
                await ReplyAsync(s, "Guerra simulada: voce nao esta em nenhuma guilda (minha=...).");
                return;
            }
            var myFacts = myGuild == 0 ? null : await OnLoopAsync(() => _guilds.GuildFacts(myGuild));
            List<string> lines = [];
            List<int> indexes = [];
            void Side(char side, int first, string name)
            {
                if (mine == side)
                {
                    // The GM's guild fights on this side: fake players fill it up to perSide members.
                    int fill = Math.Max(0, perSide - (myFacts?.Members ?? 0));
                    for (int i = 0; i < fill; i++)
                    {
                        int index = first + 1 + i;
                        indexes.Add(index);
                        lines.Add($"warbot {index} {side} member {myGuild}");
                    }
                    if (myFacts is { AutoAccept: false }) _approvers[myGuild] = s;
                    if (side == 'A') _attackGuild.TrySetResult(myGuild); else _defenseGuild.TrySetResult(myGuild);
                    return;
                }
                indexes.Add(first);
                lines.Add($"warbot {first} {side} leader {name}");
                for (int i = 1; i < perSide; i++)
                {
                    indexes.Add(first + i);
                    lines.Add($"warbot {first + i} {side} member 0");
                }
            }
            Side('A', FakeProfile.WarAttackFirst, AttackName);
            Side('D', FakeProfile.WarDefenseFirst, DefenseName);
            if (await _fakes.EnsureWarAsync(indexes) is { } problem)
            {
                await ReplyAsync(s, "Guerra simulada: " + problem);
                return;
            }
            foreach (string line in lines) _fakes.SendLine(line);
            await ReplyAsync(s, $"Guerra simulada: {indexes.Count} fake players entrando e fundando as guildas...");

            var both = Task.WhenAll(_attackGuild.Task, _defenseGuild.Task);
            if (await Task.WhenAny(both, Task.Delay(TimeSpan.FromMinutes(5))) != both)
            {
                await ReplyAsync(s, "Guerra simulada: as guildas nao ficaram prontas em 5 minutos (veja o log do servidor). Use !gvg parar.");
                return;
            }
            long attack = _attackGuild.Task.Result, defense = _defenseGuild.Task.Result;
            if (attack == defense)
            {
                await ReplyAsync(s, "Guerra simulada: atacante e defensor sao a mesma guilda.");
                return;
            }
            lock (_sync) { _attack = attack; _defense = defense; }
            // Members join for a while yet; the siege seeds the guilds, not the members.
            await Task.Delay(TimeSpan.FromSeconds(20));
            await GmLineAsync(s, "!siege close");
            await GmLineAsync(s, $"!siege seed {city} {attack} {defense}");
            await GmLineAsync(s, "!siege open");
            await OnLoopRuntimeAsync("gvg-stock", () => _guilds.TopUpWartimeStockAsync(attack, WarStock));
            await OnLoopRuntimeAsync("gvg-stock", () => _guilds.TopUpWartimeStockAsync(defense, WarStock));
            _fakes.SendLine($"wargo {city}");
            lock (_sync) _running = true;
            var a = await OnLoopAsync(() => _guilds.GuildFacts(attack));
            var d = await OnLoopAsync(() => _guilds.GuildFacts(defense));
            await ReplyAsync(s, $"Guerra simulada iniciada! Atacante {a?.Name} (#{attack}, {a?.Members}/{a?.Max}) x Defensor {d?.Name} " +
                $"(#{defense}, {d?.Members}/{d?.Max}). Para lutar: !gvg entrar atacante|defensor e Atividades > Royal City War > Ir a batalha.");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Guild war: start failed");
            await ReplyAsync(s, $"Guerra simulada: falhou ({e.Message}).");
        }
        finally
        {
            lock (_sync) _starting = false;
        }
    }

    private async Task StopCommandAsync(GameSession s)
    {
        _fakes.SendLine("warstop");
        _approvers.Clear();
        lock (_sync) _running = false;
        await GmCommands.ReplyAsync(s, "Guerra simulada: os fake players saem da guerra e desconectam; o cerco de teste fecha.");
        _ = Task.Run(() => GmLineAsync(s, "!siege close"));
    }

    private async Task JoinCommandAsync(GameSession s, string side)
    {
        long guild = side switch
        {
            "atacante" or "ataque" or "attack" => _attack,
            "defensor" or "defesa" or "defense" => _defense,
            _ => throw new ArgumentException("!gvg entrar atacante|defensor"),
        };
        if (guild == 0) throw new ArgumentException("a guerra ainda nao tem guildas: use !gvg iniciar");
        if (s.Role is not { } role) throw new ArgumentException("entre com um personagem primeiro");
        if (_guilds.GuildOf(role.Id) is var current and not 0)
            throw new ArgumentException(current == guild ? "voce ja esta nessa guilda" : "saia da sua guilda primeiro");
        await GmCommands.ReplyAsync(s, "Guerra simulada: pedido de entrada enviado (a guilda aceita na hora). Depois: Atividades > Royal City War > Ir a batalha.");
        long roleId = role.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await OnLoopRuntimeAsync("gvg-join", () => _guilds.ApplyAsync(s, new GuildEnterApplyReq { GuildIds = { guild } }));
                if (await OnLoopAsync(() => _guilds.GuildOf(roleId)) != guild) return;
                // The fake founder gives its own gunners and drivers the first eight slots of each type: a real player
                // gets one of the last two, a cannon to start with (!gvg maquina veiculo for a vehicle).
                await GiveMachineAsync(s, guild, roleId, WartimeToolType.Firearm);
            }
            catch (Exception e) { _log.LogWarning(e, "Guild war: joining guild {Guild} failed", guild); }
        });
    }

    /// <summary>"!gvg maquina canhao|veiculo": the GM's war machine slot in its guild, as the Tactics panel gives it.</summary>
    private async Task MachineCommandAsync(GameSession s, string kind)
    {
        var type = kind switch
        {
            "canhao" or "canhão" or "cannon" => WartimeToolType.Firearm,
            "veiculo" or "veículo" or "vehicle" => WartimeToolType.Vehicle,
            _ => throw new ArgumentException("!gvg maquina canhao|veiculo"),
        };
        if (s.Role is not { } role) throw new ArgumentException("entre com um personagem primeiro");
        long guild = _guilds.GuildOf(role.Id);
        if (guild == 0) throw new ArgumentException("voce nao esta em nenhuma guilda");
        long roleId = role.Id;
        await GmCommands.ReplyAsync(s, "Guerra simulada: pedindo a permissao...");
        _ = Task.Run(async () =>
        {
            try { await GiveMachineAsync(s, guild, roleId, type); }
            catch (Exception e) { _log.LogWarning(e, "Guild war: machine slot in guild {Guild} failed", guild); }
        });
    }

    /// <summary>A war machine slot for a real player (off the loop), and the answer to the GM.</summary>
    private async Task GiveMachineAsync(GameSession s, long guild, long role, WartimeToolType type)
    {
        int bag = 0;
        await OnLoopRuntimeAsync("gvg-slot", async () => bag = await _guilds.GiveWarSlotAsync(guild, role, type));
        string machine = type == WartimeToolType.Firearm ? "canhao" : "veiculo";
        await ReplyAsync(s, bag > 0
            ? $"Guerra simulada: voce tem a permissao de {machine} (vaga {bag} da guilda). No cerco, a bolsa de batalha mostra os itens."
            : "Guerra simulada: nao foi possivel dar a permissao (voce esta na guilda?).");
    }

    private async Task PullCommandAsync(GameSession s, string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1)
            throw new ArgumentException("!gvg puxar <quantidade> [minha|atacante|defensor|idGuilda]");
        string where = args.Length > 1 ? args[1].ToLowerInvariant() : "minha";
        long guild = where switch
        {
            "minha" or "mine" => s.Role is { } role ? _guilds.GuildOf(role.Id) : 0,
            "atacante" or "ataque" or "attack" => _attack,
            "defensor" or "defesa" or "defense" => _defense,
            _ => long.TryParse(where, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id
                : throw new ArgumentException("!gvg puxar <quantidade> [minha|atacante|defensor|idGuilda]"),
        };
        if (guild == 0)
            throw new ArgumentException(where is "minha" or "mine" ? "voce nao esta em nenhuma guilda" : "essa guilda ainda nao existe: use !gvg iniciar");
        if (_guilds.GuildFacts(guild) is not { } facts) throw new ArgumentException($"guilda {guild} nao existe");
        int room = (int)Math.Max(0, facts.Max - facts.Members);
        if (room == 0) throw new ArgumentException($"a guilda {facts.Name} esta cheia ({facts.Members}/{facts.Max})");
        count = Math.Min(count, room);
        int first;
        lock (_sync)
        {
            if (_nextPull + count - 1 > FakeProfile.WarLast) _nextPull = FakeProfile.WarPullFirst;
            first = _nextPull;
            _nextPull += count;
        }
        if (!facts.AutoAccept) _approvers[guild] = s;
        int[] indexes = [.. Enumerable.Range(first, count)];
        await GmCommands.ReplyAsync(s, $"Guerra simulada: {count} fake players (3RD nivel maximo) a caminho da guilda {facts.Name}" +
            (facts.AutoAccept ? "." : " (voce aprova os pedidos automaticamente)."));
        _ = Task.Run(async () =>
        {
            if (await _fakes.EnsureWarAsync(indexes) is { } problem)
            {
                await ReplyAsync(s, "Guerra simulada: " + problem);
                return;
            }
            foreach (int index in indexes) _fakes.SendLine($"warbot {index} P member {guild}");
        });
    }

    private string StatusText()
    {
        bool running;
        long attack, defense;
        lock (_sync) { running = _running; attack = _attack; defense = _defense; }
        string Side(long id) => id == 0 ? "-" : _guilds.GuildFacts(id) is { } f ? $"{f.Name} #{id} ({f.Members}/{f.Max})" : $"#{id}";
        string bots = _warStatus.Length == 0 ? "" : $" | fake players ({(DateTime.UtcNow - _warStatusAt).TotalSeconds:F0}s atras): {_warStatus}";
        string process = _fakes.ProcessRunning ? "" : " | processo de fake players parado";
        return $"Guerra simulada: {(running ? "em andamento" : _starting ? "preparando" : "parada")}, cidade {_city}, " +
            $"atacante {Side(attack)}, defensor {Side(defense)}{bots}{process}";
    }

    // ------------------------------------------------------------------ the fake player process

    /// <summary>"@guild A 123", "@applied 4567 123", "@warstatus ...", "@warfail fp_90003 entrar 6071" (off the loop).</summary>
    private void OnWarLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        switch (parts[0])
        {
            case "@guild" when parts.Length >= 3 && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long guild):
                if (parts[1] == "A") _attackGuild.TrySetResult(guild);
                else if (parts[1] == "D") _defenseGuild.TrySetResult(guild);
                return;
            case "@applied" when parts.Length >= 3 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long role) && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long target):
                _applied.Enqueue((role, target));
                if (Interlocked.Exchange(ref _approving, 1) == 0) _ = Task.Run(ApproveLoopAsync);
                return;
            case "@warstatus":
                _warStatus = line.Length > 11 ? line[11..] : "";
                _warStatusAt = DateTime.UtcNow;
                return;
            case "@warfail" when parts.Length >= 4:
                // One note per kind of failure a minute, to the GM who started the war.
                string key = parts[2] + " " + parts[3];
                var now = DateTime.UtcNow;
                if (_failNotes.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
                _failNotes[key] = now;
                _log.LogInformation("Guild war: {Line}", line);
                if (_owner is { } owner) _ = ReplyAsync(owner, $"Guerra simulada: {parts[1]} falhou em '{parts[2]}' (codigo {parts[3]}).");
                return;
        }
    }

    /// <summary>
    /// Approves the fake players' applications to a guild that does not auto-accept, as the GM who asked for them (the
    /// same 32224 the guild window sends; the GM needs the guild's approval right).
    /// </summary>
    private async Task ApproveLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                var batch = new Dictionary<long, List<long>>();
                while (_applied.TryDequeue(out var a))
                {
                    if (!batch.TryGetValue(a.Guild, out var list)) batch[a.Guild] = list = [];
                    list.Add(a.Role);
                }
                if (batch.Count == 0) break;
                foreach (var (guild, roles) in batch)
                {
                    if (!_approvers.TryGetValue(guild, out var asked) || await OnLoopAsync(() => Live(asked)) is not { } gm) continue;
                    foreach (var chunk in roles.Distinct().Chunk(50))
                        await OnLoopRuntimeAsync("gvg-approve", () => _guilds.ApplyOpeAsync(gm,
                            new GuildEnterApplyOpeReq { Agree = true, IsBatch = chunk.Length > 1, PlayerIds = { chunk } }));
                }
            }
        }
        catch (Exception e) { _log.LogWarning(e, "Guild war: approving applications failed"); }
        finally
        {
            Interlocked.Exchange(ref _approving, 0);
            if (!_applied.IsEmpty && Interlocked.Exchange(ref _approving, 1) == 0) _ = Task.Run(ApproveLoopAsync);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The GM's session now (call on the loop): <paramref name="s"/> while it is signed in, else the session its role came
    /// back with after a reconnect, or null while the GM is offline.
    /// </summary>
    private GameSession? Live(GameSession s) =>
        s.Role != null ? s
        : _gmRoles.TryGetValue(s, out long role) ? _sessions.All().FirstOrDefault(x => x.Role?.Id == role) : null;

    /// <summary>
    /// A GM line as the GM typed it (its own admission: "!siege" runs as the siege's event owner), awaited. A GM who is
    /// reconnecting at that moment is waited for, up to a minute.
    /// </summary>
    private async Task GmLineAsync(GameSession s, string line)
    {
        for (int i = 0; i < 30; i++)
        {
            if (await OnLoopAsync(() => Live(s)) is { } live)
            {
                _log.LogInformation("Guild war: {Line}", line);
                await OnLoopRuntimeAsync("gvg-gm", () => _gm.RunAsync(live, line));
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        _log.LogWarning("Guild war: {Line} was not run: the GM who started the war is offline", line);
        throw new InvalidOperationException($"o GM saiu do jogo antes de '{line}'");
    }

    /// <summary>
    /// Runs work as a runtime operation of the loop (each guild or siege operation admits its own keys) and waits. Queued
    /// with PostRuntime, which any thread may call (TrackRuntimeDetached must start on the loop, and the war's steps run
    /// off it).
    /// </summary>
    private Task OnLoopRuntimeAsync(string name, Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.PostRuntime(async () =>
        {
            try { await work(); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
        }, "gvg/" + name);
        return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private Task<T> OnLoopAsync<T>(Func<T> read) => _loop.InvokeAsync(() => Task.FromResult(read()));

    /// <summary>A line to the GM (its current session) and to the server log, where it stays when the GM is offline.</summary>
    private Task ReplyAsync(GameSession session, string text)
    {
        _log.LogInformation("{Text}", text);
        return _loop.InvokeAsync(async () =>
        {
            if (Live(session) is not { } live) return;
            try { await GmCommands.ReplyAsync(live, text); }
            catch (Exception e) { _log.LogDebug(e, "Guild war: reply to the GM failed"); }
        });
    }
}
