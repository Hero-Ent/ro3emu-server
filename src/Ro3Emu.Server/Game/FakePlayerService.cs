using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The GM command "!fakeplayer &lt;n&gt;": n fake players that log in through the real protocol (they count as online,
/// walk in Prontera, talk, hunt, die and revive), for a fuller world during the beta and as a stress test.
/// The players run in a separate process (Ro3Emu.FakePlayers) on loopback, so their client work does not share this
/// server's CPU budget; this service registers their accounts, starts and steers that process, and applies each
/// player's class, levels and stats when it reports "@ready". docs/guide/fake-players.md.
/// </summary>
public sealed class FakePlayerService : IDisposable
{
    private readonly IOptions<ServerOptions> _options;
    private readonly GmCommands _gm;
    private readonly AccountStore _accounts;
    private readonly SessionRegistry _sessions;
    private readonly CharacterService _character;
    private readonly GameLoop _loop;
    private readonly IServiceProvider _services;
    private readonly ILogger<FakePlayerService> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly HashSet<int> _registered = new();
    private readonly object _sync = new();
    private Process? _process;
    private string _lastStatus = "";
    private DateTime _lastStatusAt;
    private int _target;
    private int[]? _statCost;

    public FakePlayerService(IOptions<ServerOptions> options, GmCommands gm, AccountStore accounts, SessionRegistry sessions,
        CharacterService character, GameLoop loop, IHostApplicationLifetime lifetime, IServiceProvider services, ILogger<FakePlayerService> log)
    {
        _options = options;
        _gm = gm;
        _accounts = accounts;
        _sessions = sessions;
        _character = character;
        _loop = loop;
        _services = services;
        _log = log;
        gm.Register("fakeplayer", "<quantidade> | 0 | status | stop  (fake players: populacao e teste de carga)", CommandAsync);
        lifetime.ApplicationStopping.Register(StopProcess);
        character.PrepareProgress = PrepareProgress;
    }

    private FakePlayerOptions Settings => _options.Value.FakePlayers;

    /// <summary>A fake player's account (prefix + digits).</summary>
    public bool IsFake(string? account) =>
        account != null && account.StartsWith(Settings.AccountPrefix, StringComparison.Ordinal) &&
        account.Length > Settings.AccountPrefix.Length && account.AsSpan(Settings.AccountPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    public int FakeOnline => _sessions.All().Count(s => IsFake(s.Account));

    /// <summary>The fake player process's last "@status" counters (empty before its first report).</summary>
    public string LastStatus => _lastStatus;

    // ------------------------------------------------------------------ GM command

    private async Task CommandAsync(GameSession session, string[] args)
    {
        string verb = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        if (verb == "status")
        {
            await GmCommands.ReplyAsync(session, StatusText());
            return;
        }
        if (verb is "stop" or "kill")
        {
            _target = 0;
            _ = Task.Run(StopProcess);
            await GmCommands.ReplyAsync(session, "Fake players: parando o processo e desconectando todos.");
            return;
        }
        if (!int.TryParse(verb, out int count) || count < 0)
            throw new ArgumentException("<quantidade> | 0 | status | stop");
        if (count > Settings.MaxBots)
        {
            await GmCommands.ReplyAsync(session, $"Fake players: o maximo e {Settings.MaxBots} (Ro3Emu:FakePlayers:MaxBots).");
            return;
        }
        _target = count;
        if (count == 0)
        {
            Send("target 0");
            await GmCommands.ReplyAsync(session, "Fake players: removendo todos (eles saem em alguns segundos).");
            return;
        }
        await GmCommands.ReplyAsync(session, $"Fake players: preparando {count}. Eles entram aos poucos; use !fakeplayer status.");
        _ = Task.Run(() => PrepareAsync(count, session));
    }

    private string StatusText()
    {
        bool running = _process is { HasExited: false };
        string status = _lastStatus.Length == 0 ? "" : $" | processo ({(DateTime.UtcNow - _lastStatusAt).TotalSeconds:F0}s atras): {_lastStatus}";
        return $"Fake players: alvo {_target}, online no servidor {FakeOnline}, processo {(running ? "rodando" : "parado")}{status}";
    }

    /// <summary>Registers the missing accounts (off the game loop: each is a password hash), then starts/steers the process.</summary>
    private async Task PrepareAsync(int count, GameSession requester)
    {
        await _busy.WaitAsync();
        try
        {
            if (_target != count) return; // a newer command took over
            string password = Password();
            var missing = Enumerable.Range(1, count).Where(i => { lock (_registered) return !_registered.Contains(i); }).ToArray();
            int created = 0, done = 0, nextReport = Math.Max(50, missing.Length / 4);
            await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) },
                async (index, _) =>
                {
                    string account = AccountName(index);
                    if (!await _accounts.ExistsAsync(account) && await _accounts.RegisterAsync(account, password)) Interlocked.Increment(ref created);
                    lock (_registered) _registered.Add(index);
                    int n = Interlocked.Increment(ref done);
                    if (missing.Length >= 200 && n % nextReport == 0) await ReplyAsync(requester, $"Fake players: contas {n}/{missing.Length}...");
                });
            if (created > 0) _log.LogInformation("Fake players: {Created} account(s) registered ({Prefix}*)", created, Settings.AccountPrefix);
            if (_target != count) return;
            if (!EnsureProcess(password, out string? problem))
            {
                await ReplyAsync(requester, "Fake players: " + problem);
                return;
            }
            Send($"target {count}");
            await ReplyAsync(requester, $"Fake players: {count} a caminho ({created} contas novas). Veja com !fakeplayer status.");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Fake players: preparing {Count} failed", count);
            await ReplyAsync(requester, $"Fake players: falhou ({e.Message}).");
        }
        finally { _busy.Release(); }
    }

    private string AccountName(int index) => Settings.AccountPrefix + index.ToString("D5");

    private Task ReplyAsync(GameSession session, string text) =>
        _loop.InvokeAsync(async () =>
        {
            if (session.Role == null) return;
            try { await GmCommands.ReplyAsync(session, text); }
            catch (Exception e) { _log.LogDebug(e, "Fake players: reply to the GM failed"); }
        });

    /// <summary>The fake accounts' password, created once and kept in the data dir (fakeplayers.key).</summary>
    private string Password()
    {
        string path = Path.Combine(_options.Value.DataDir, "fakeplayers.key");
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 16 } existing) return existing;
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(path, key);
        return key;
    }

    // ------------------------------------------------------------------ the process

    private bool EnsureProcess(string password, out string? problem)
    {
        problem = null;
        lock (_sync)
        {
            if (_process is { HasExited: false }) return true;
            string? exe = FindExecutable();
            if (exe == null)
            {
                problem = "Ro3Emu.FakePlayers nao encontrado. Compile com: dotnet build src/Ro3Emu.FakePlayers -c Release";
                return false;
            }
            var opts = _options.Value;
            bool dll = exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(dll ? "dotnet" : exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            if (dll) start.ArgumentList.Add(exe);
            foreach (var arg in new[] { "--server", $"http://127.0.0.1:{opts.HttpPort}", "--data", opts.GameDataDir ?? opts.DataDir,
                         "--prefix", Settings.AccountPrefix, "--logins", Settings.LoginConcurrency.ToString(),
                         "--chat-speed", Settings.ChatSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--view", Settings.ViewCount.ToString() })
                start.ArgumentList.Add(arg);
            start.Environment["RO3EMU_FAKE_PASSWORD"] = password;
            var process = Process.Start(start) ?? throw new InvalidOperationException("the fake player process did not start");
            process.StandardInput.AutoFlush = true;
            _process = process;
            _ = Task.Run(() => ReadOutputAsync(process));
            _ = Task.Run(() => ReadErrorsAsync(process));
            _log.LogInformation("Fake players: started {Exe} (pid {Pid})", exe, process.Id);
            return true;
        }
    }

    private string? FindExecutable()
    {
        string name = OperatingSystem.IsWindows() ? "Ro3Emu.FakePlayers.exe" : "Ro3Emu.FakePlayers";
        if (Settings.Executable is { Length: > 0 } configured)
        {
            string path = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
            return File.Exists(path) ? path : null;
        }
        string bundled = Path.Combine(AppContext.BaseDirectory, "fakeplayers", name);
        if (File.Exists(bundled)) return bundled;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) continue;
            return new[] { "Release", "Debug" }
                .Select(c => Path.Combine(dir.FullName, "src", "Ro3Emu.FakePlayers", "bin", c, "net10.0", name))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    private void Send(string line)
    {
        lock (_sync)
        {
            if (_process is not { HasExited: false } process) return;
            try { process.StandardInput.WriteLine(line); }
            catch (Exception e) { _log.LogWarning(e, "Fake players: cannot write to the process"); }
        }
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("@status ", StringComparison.Ordinal)) { _lastStatus = line[8..]; _lastStatusAt = DateTime.UtcNow; }
                else if (line.StartsWith("@log ", StringComparison.Ordinal)) _log.LogInformation("Fake players: {Text}", line[5..]);
            }
        }
        catch (Exception e) { _log.LogWarning(e, "Fake players: output reader stopped"); }
        _log.LogInformation("Fake players: process ended");
    }

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (line.Length > 0) _log.LogWarning("Fake players (stderr): {Line}", line);
        }
        catch (Exception) { }
    }

    private void StopProcess()
    {
        Process? process;
        lock (_sync) { process = _process; _process = null; }
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.WriteLine("quit"); } catch (Exception) { }
                if (!process.WaitForExit(15_000)) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) { _log.LogWarning(e, "Fake players: stopping the process failed"); }
        finally { process.Dispose(); }
    }

    public void Dispose() => StopProcess();

    // ------------------------------------------------------------------ profiles

    /// <summary>
    /// CharacterService.PrepareProgress: while a fake role loads at login (the role select owns it), give it its
    /// profile's class and levels the first time, spend free skill points on the class's attack skills and buffs and
    /// free stat points on its build, and tell the fake player process which skills it can cast. Nothing here runs on
    /// a live role: an earlier version applied all this through realm-wide operations after login, which stalled the
    /// whole server once hundreds of fake players arrived together.
    /// </summary>
    internal bool PrepareProgress(GameSession session, RoleProgress p)
    {
        string? account = session.Account;
        if (!IsFake(account) || !int.TryParse(account![Settings.AccountPrefix.Length..], out int index)) return false;
        var profile = FakeProfile.For(index);
        bool changed = false;
        try
        {
            if (p.Career != profile.Job || p.Level < profile.BaseLevel)
            {
                _character.SetCareerAndLevels(p, profile.Job, profile.BaseLevel, profile.JobLevel);
                changed = true;
            }
            var skills = _services.GetRequiredService<SkillService>();
            var data = _services.GetRequiredService<Data.GameData>();
            if (Settings.Skills) changed |= LearnSkills(skills, data, p, Settings.Buffs);
            changed |= AllocateStats(p);
            if (Settings.Skills)
            {
                var groups = skills.Tree(p.Career).Select(t => t.Group).Distinct()
                    .Where(g => IsAttackSkill(data, skills.RealId(g, 1)) || Settings.Buffs && IsSelfBuff(data, skills.RealId(g, 1)));
                var learned = groups.Select(g => (Group: g, Level: skills.Level(p, g))).Where(x => x.Level > 0);
                Send($"skills {account} {string.Join(',', learned.Select(x => $"{x.Group}:{x.Level}"))}");
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            _log.LogWarning("Fake players: profile for {Account}: {Error}", account, e.Message);
        }
        return changed;
    }

    /// <summary>
    /// Spends the free skill points (per job rank) on the class's attack skills: prerequisites first, each to 5, then to
    /// 10 and beyond; what is left buys the self buffs. Only levels the skill tree allows are set.
    /// </summary>
    private static bool LearnSkills(SkillService skills, Data.GameData data, RoleProgress p, bool buffsEnabled)
    {
        var tree = skills.Tree(p.Career);
        var rankOf = tree.GroupBy(t => t.Group).ToDictionary(g => g.Key, g => skills.Rank(g.First().Career));
        var free = skills.FreePoints(p).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (free.Values.All(v => v <= 0)) return false;
        bool changed = false;
        bool Raise(int group, int to, int depth)
        {
            if (depth > 5 || !rankOf.TryGetValue(group, out int rank)) return false;
            to = Math.Min(to, skills.MaxLevel(group));
            int current = skills.Level(p, group);
            if (current >= to) return true;
            if (data.Row("SkillConfig", skills.RealId(group, to)) is { } row && row.TryGetProperty("_kPreSkillRequired", out var pre)
                && pre.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var need in pre.EnumerateArray())
                    if (need.ValueKind == System.Text.Json.JsonValueKind.Array && need.GetArrayLength() >= 2
                        && (need[1].GetInt32() < 0 || !Raise(need[0].GetInt32(), need[1].GetInt32(), depth + 1))) return false;
            int points = free.GetValueOrDefault(rank);
            if (points <= 0) return false;
            int level = Math.Min(to, current + points);
            if (skills.RealId(group, level) == 0) return false;
            free[rank] = points - (level - current);
            p.Skills[group] = Math.Max(level, skills.InitialLevel(p, group));
            changed = true;
            return level >= to;
        }
        var groups = tree.Select(t => t.Group).Distinct().ToList();
        var attacks = groups.Where(g => IsAttackSkill(data, skills.RealId(g, 1))).ToList();
        var buffs = groups.Where(g => IsSelfBuff(data, skills.RealId(g, 1))).ToList();
        foreach (int cap in new[] { 5, 10, 20 })
            foreach (int group in attacks) Raise(group, cap, 0);
        if (buffsEnabled)
            foreach (int cap in new[] { 5, 10 })
                foreach (int group in buffs) Raise(group, cap, 0);
        return changed;
    }

    /// <summary>
    /// An active skill that only hurts a monster (aimed at it, at the ground under it, or around the caster): no buff or
    /// debuff left behind and no energy cost. Effects and energy each start an expiry/recovery timer that the server
    /// runs realm-wide; hundreds of fake players casting them froze every other operation.
    /// </summary>
    internal static bool IsAttackSkill(Data.GameData data, int rowId) =>
        data.Row("SkillConfig", rowId) is { } row && Data.GameData.Int(row, "_iBasicType") == 1
        && Data.GameData.Int(row, "_iDmgType") is 1 or 2 && Data.GameData.Int(row, "_iRelease") is 1 or 2 or 7 or 8
        && Data.GameData.Int(row, "_iIsNormalAtk") != 1
        && Empty(row, "_kBuff1") && Empty(row, "_kBuff2") && Empty(row, "_kEnergyInfo") && Empty(row, "_kEnergyChangeList");

    private static bool Empty(System.Text.Json.JsonElement row, string field) =>
        !row.TryGetProperty(field, out var v) || v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Array => v.GetArrayLength() == 0 || v.EnumerateArray().All(x =>
                x.ValueKind == System.Text.Json.JsonValueKind.Array ? x.GetArrayLength() == 0
                : x.ValueKind is System.Text.Json.JsonValueKind.Number ? x.GetDouble() == 0
                : x.ValueKind is System.Text.Json.JsonValueKind.String ? string.IsNullOrEmpty(x.GetString()) : false),
            System.Text.Json.JsonValueKind.Object => !v.EnumerateObject().Any(),
            System.Text.Json.JsonValueKind.Number => v.GetDouble() == 0,
            System.Text.Json.JsonValueKind.String => string.IsNullOrEmpty(v.GetString()),
            _ => true,
        };

    /// <summary>A skill cast on oneself that applies a buff: no damage, _kBuff1 set (no teleports or other utilities).</summary>
    internal static bool IsSelfBuff(Data.GameData data, int rowId) =>
        data.Row("SkillConfig", rowId) is { } row && Data.GameData.Int(row, "_iBasicType") == 1
        && Data.GameData.Int(row, "_iRelease") == 2 && Data.GameData.Int(row, "_iDmgType") == 0
        && row.TryGetProperty("_kBuff1", out var buffs) && buffs.ValueKind == System.Text.Json.JsonValueKind.Array && buffs.GetArrayLength() > 0;

    /// <summary>Spends every free stat point on the job line's usual build (each stat at most 99).</summary>
    private bool AllocateStats(RoleProgress p)
    {
        var cost = _statCost ??= Enumerable.Range(0, CharacterService.MaxStat + 2).Select(v => v < 1 ? 0 : _character.StatCost(v)).ToArray();
        int total = _character.TotalPoints(p);
        int used = p.Stats.Sum(v => cost[Math.Clamp(v, 0, cost.Length - 1)]);
        if (total - used <= 0) return false;
        int[] weights = BuildWeights(p.Career);
        var full = new bool[6];
        bool changed = false;
        while (true)
        {
            int pick = -1;
            for (int i = 0; i < 6; i++)
                if (weights[i] > 0 && !full[i] && (pick < 0 || p.Stats[i] * weights[pick] < p.Stats[pick] * weights[i])) pick = i;
            if (pick < 0) break;
            int next = p.Stats[pick] + 1;
            if (next > CharacterService.MaxStat || used + cost[next] - cost[p.Stats[pick]] > total) { full[pick] = true; continue; }
            used += cost[next] - cost[p.Stats[pick]];
            p.Stats[pick] = next;
            changed = true;
        }
        return changed;
    }

    /// <summary>STR, AGI, VIT, INT, DEX, LUK weights per job line.</summary>
    private static int[] BuildWeights(int career) => career == 1000 ? [3, 2, 3, 0, 2, 1] : (career / 1000) switch
    {
        1 => [5, 3, 4, 0, 2, 1],
        2 => [0, 1, 2, 6, 4, 0],
        3 => [1, 3, 1, 0, 6, 1],
        4 => [1, 1, 4, 5, 2, 0],
        5 => [4, 6, 1, 0, 2, 2],
        6 => [5, 1, 4, 0, 3, 2],
        _ => [2, 2, 2, 2, 2, 1],
    };
}
