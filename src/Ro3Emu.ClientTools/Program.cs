using Ro3Emu.ClientTools;

// Ro3Emu.ClientTools — everything a player needs to use the RO3 client (the PC build) with an Ro3Emu server.
if (args.Length < 2)
{
    Console.Error.WriteLine("""
        usage:
          play    <client dir> <server url>     patch for that server (if needed) and start the game
          patch   <client dir> <server url> [gate_rsa_public.pem] [Ro3Emu.ClientHooks.dll] [--verbose]
          restore <client dir>                  put the original DLLs back
          launch  <client dir> [ro3.exe args...]
          release <client dir> <server url> <gate_rsa_public.pem> <repo dir> <version> [channel] [zstd level] [--no-cache]
                                                publish the client patched for that server for Ro3Emu.Launcher
          release-launcher <repo dir> <channel> <Ro3EmuLauncher.exe> <version>
          init-repo <repo dir> [channel] [version]
                                                initialize a clean patch repository structure
          scan    <client dir> <text>...        list string literals in the client DLLs
          members <client dir> <dll> <type> [method]  list a type's methods with their parameters
          delete-role <server url> <account> <password> <character name or id>
        <client dir> is the folder with ro3.exe, <server url> e.g. http://203.0.113.5:5380.
        Without a .pem the gate key is downloaded from the server.
        """);
    return 1;
}

try
{
    ClientPatcher.VerboseLogs = args.Contains("--verbose");
    bool noCache = args.Contains("--no-cache");
    args = args.Where(a => a != "--verbose" && a != "--no-cache").ToArray();
    switch (args[0])
    {
        case "play":
            if (args.Length < 3) throw new ArgumentException("play needs <client dir> <server url>");
            ClientPatcher.Patch(args[1], args[2].TrimEnd('/'), await ServerKey(args[2]));
            return Launcher.Launch(args[1], args[3..]);
        case "patch":
            if (args.Length < 3) throw new ArgumentException("patch needs <client dir> <server url>");
            string pem = args.Length > 3 ? File.ReadAllText(args[3]) : await ServerKey(args[2]);
            ClientPatcher.Patch(args[1], args[2].TrimEnd('/'), pem, args.Length > 4 ? args[4] : null);
            return 0;
        case "release":
            if (args.Length < 6) throw new ArgumentException("release needs <client dir> <server url> <pem> <repo dir> <version>");
            return await ReleaseBuilder.PublishAsync(args[1], args[2].TrimEnd('/'), File.ReadAllText(args[3]), args[4], args[5],
                args.Length > 6 ? args[6] : "test", args.Length > 7 ? int.Parse(args[7]) : 9, useCache: !noCache);
        case "release-launcher":
            if (args.Length < 5) throw new ArgumentException("release-launcher needs <repo dir> <channel> <Ro3EmuLauncher.exe> <version>");
            var withLauncher = await Ro3Emu.Patcher.Publisher.PublishLauncherAsync(args[1], args[2], args[3], args[4]);
            Console.WriteLine($"channel {withLauncher.Channel}: game {withLauncher.Version}, launcher {withLauncher.Launcher!.Version}");
            return 0;
        case "init-repo":
            if (args.Length < 2) throw new ArgumentException("init-repo needs <repo dir> [channel] [version]");
            return await InitRepo(args[1], args.Length > 2 ? args[2] : "test", args.Length > 3 ? args[3] : "1.0.0");
        case "scan":
            ClientPatcher.Scan(args[1], args[2..]);
            return 0;
        case "delete-role":
            if (args.Length < 5) throw new ArgumentException("delete-role needs <server url> <account> <password> <character>");
            return await DeleteRole(args[1], args[2], args[3], args[4]);
        case "members":
            ClientPatcher.Members(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
            return 0;
        case "restore":
            ClientPatcher.Restore(args[1]);
            return 0;
        case "launch":
            return Launcher.Launch(args[1], args[2..]);
        default:
            throw new ArgumentException($"unknown command {args[0]}");
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// The RO3 client has no character deletion (its role screen button is hidden); the server offers it over HTTP.
static async Task<int> DeleteRole(string serverUrl, string account, string password, string role)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var resp = await http.PostAsync(serverUrl.TrimEnd('/') + "/ro3emu/account/delete-role", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["identity"] = account, ["credential"] = password, ["role"] = role,
    }));
    using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    int code = json.RootElement.GetProperty("code").GetInt32();
    Console.WriteLine(json.RootElement.GetProperty("message").GetString());
    return code == 0 ? 0 : 3;
}

static async Task<string> ServerKey(string serverUrl)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var url = serverUrl.TrimEnd('/') + "/ro3emu/gate-key.pem";
    Console.WriteLine($"fetching gate key from {url}");
    return await http.GetStringAsync(url);
}

static async Task<int> InitRepo(string repoDir, string channelName, string version)
{
    repoDir = Path.GetFullPath(repoDir);
    Directory.CreateDirectory(Path.Combine(repoDir, "channels"));
    Directory.CreateDirectory(Path.Combine(repoDir, "manifests"));
    Directory.CreateDirectory(Path.Combine(repoDir, "chunks"));
    Directory.CreateDirectory(Path.Combine(repoDir, "launcher", version));

    string manifestRelative = $"manifests/{version}.json.zst";
    string manifestPath = Path.Combine(repoDir, manifestRelative);

    var manifest = new Ro3Emu.Patcher.Manifest
    {
        Version = version,
        Created = DateTimeOffset.UtcNow,
        Launch = new Ro3Emu.Patcher.LaunchInfo { Exe = "ro3.exe", Args = "" },
        Chunking = Ro3Emu.Patcher.ChunkingInfo.From(Ro3Emu.Patcher.ChunkingParameters.Default),
        Files = new List<Ro3Emu.Patcher.ManifestFile>()
    };
    byte[] manifestBytes = manifest.ToCompressedBytes();
    await File.WriteAllBytesAsync(manifestPath, manifestBytes);
    string manifestSha256 = Ro3Emu.Patcher.Hashing.Sha256Hex(manifestBytes);

    Ro3Emu.Patcher.LauncherRelease? launcherRelease = null;
    string[] candidateLaunchers = ["HeroLauncher.exe", "Launcher-Dist/HeroLauncher.exe", "Ro3EmuLauncher.exe"];
    string? foundLauncher = candidateLaunchers.FirstOrDefault(File.Exists);
    if (foundLauncher is not null)
    {
        byte[] exeBytes = await File.ReadAllBytesAsync(foundLauncher);
        string launcherTarget = Path.Combine(repoDir, "launcher", version, "HeroLauncher.exe");
        await File.WriteAllBytesAsync(launcherTarget, exeBytes);
        string rootTarget = Path.Combine(repoDir, "HeroLauncher.exe");
        if (!File.Exists(rootTarget)) File.Copy(foundLauncher, rootTarget, overwrite: true);
        launcherRelease = new Ro3Emu.Patcher.LauncherRelease
        {
            Version = version,
            Url = $"launcher/{version}/HeroLauncher.exe",
            Sha256 = Ro3Emu.Patcher.Hashing.Sha256Hex(exeBytes)
        };
    }

    var channel = new Ro3Emu.Patcher.ChannelInfo
    {
        Channel = channelName,
        Version = version,
        Manifest = manifestRelative,
        ManifestSha256 = manifestSha256,
        Published = DateTimeOffset.UtcNow,
        Launcher = launcherRelease
    };
    string channelPath = Path.Combine(repoDir, "channels", $"{channelName}.json");
    await File.WriteAllBytesAsync(channelPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(channel, Ro3Emu.Patcher.Json.Indented));

    string newsPath = Path.Combine(repoDir, "news.json");
    if (!File.Exists(newsPath))
    {
        var news = new
        {
            items = new[]
            {
                new { tag = "WELCOME", color = "blue", title = "Prepare-se para o lançamento", text = "Os portões de Midgard se abrem em breve. Junte-se ao nosso Discord oficial para acompanhar tudo!", date = DateTime.UtcNow.ToString("yyyy-MM-dd") },
                new { tag = "STATUS", color = "red", title = "Servidores em preparação", text = "Estamos ajustando os detalhes finais para o lançamento. Verifique o Discord para avisos.", date = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd") },
                new { tag = "UPDATES", color = "orange", title = "Atualizações automáticas", text = "O inicializador baixa e atualiza todos os arquivos necessários de forma rápida e segura.", date = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd") }
            }
        };
        await File.WriteAllBytesAsync(newsPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(news, Ro3Emu.Patcher.Json.Indented));
    }

    string clientStatePath = Path.Combine("Client", "Ragnarok3", ".ro3launcher", "state.json");
    if (File.Exists(clientStatePath))
    {
        var localState = System.Text.Json.JsonSerializer.Deserialize<Ro3Emu.Patcher.LocalState>(await File.ReadAllBytesAsync(clientStatePath), Ro3Emu.Patcher.Json.Compact)
            ?? new Ro3Emu.Patcher.LocalState();
        localState.Version = version;
        localState.Channel = channelName;
        localState.ManifestSha256 = manifestSha256;
        localState.Save(clientStatePath);
    }

    Console.WriteLine($"Repository initialized at {repoDir}");
    Console.WriteLine($"  Channel: {channelName} -> Version {version}");
    Console.WriteLine($"  Manifest: {manifestRelative} (sha256: {manifestSha256})");
    if (launcherRelease is not null)
        Console.WriteLine($"  Launcher: {launcherRelease.Url} (version: {launcherRelease.Version})");
    return 0;
}

