using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Publishes the content the Android app downloads (android\updater) into the same repository as the PC client. The PC
/// release's <c>ro3_Data/StreamingAssets</c> is taken from its manifest as it is, under <c>StreamingAssets/</c> (the
/// folder Ro3Android.Hooks.Paths points the game at), so those chunks are shared with the PC and nothing is re-read;
/// only the overlay (the bundles retargeted to Android) is chunked. The channel names the APK the content needs and is
/// signed with the key whose public half the app carries.
/// </summary>
public static class AndroidRelease
{
    const string PcPrefix = "ro3_Data/StreamingAssets/";
    const string AndroidPrefix = "StreamingAssets/";

    /// <summary>Never shipped to phones: the AVPro video player is a stub on Android (2 GB of videos).</summary>
    public static readonly string[] DefaultExcludes = ["StreamingAssets/VFS/Videos/"];

    public static async Task<int> PublishAsync(string[] args)
    {
        if (args.Length < 5) throw new ArgumentException("release-android needs <repo dir> <base version> <version> <channel>");
        string repo = Path.GetFullPath(args[1]), baseVersion = args[2], version = args[3], channelName = args[4];
        var opts = ParseOptions(args[5..]);
        string keyFile = Single(opts, "key") ?? throw new ArgumentException("--key <signing key .pem> is required (create one with android-key)");

        byte[] baseBytes = await File.ReadAllBytesAsync(Path.Combine(repo, "manifests", baseVersion + ".json.zst"));
        var baseManifest = Manifest.FromCompressedBytes(baseBytes);
        var excludes = new GlobSet(opts.TryGetValue("exclude", out var ex) ? ex : DefaultExcludes);
        var inherited = new List<ManifestFile>();
        long excludedBytes = 0;
        foreach (var file in baseManifest.Files)
        {
            if (!file.Path.StartsWith(PcPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            string path = AndroidPrefix + file.Path[PcPrefix.Length..];
            if (excludes.IsMatch(path)) { excludedBytes += file.Size; continue; }
            inherited.Add(new ManifestFile { Path = path, Size = file.Size, Sha256 = file.Sha256, Chunks = file.Chunks });
        }
        Console.WriteLine($"from {baseVersion}: {inherited.Count} files ({inherited.Sum(f => f.Size) / 1048576} MiB), excluded {excludedBytes / 1048576} MiB");

        var files = new List<KeyValuePair<string, string>>();
        if (Single(opts, "overlay") is { } overlay)
        {
            files = Publisher.CollectDirectory(overlay, AndroidPrefix + "VFS", _ => false);
            Console.WriteLine($"overlay {overlay}: {files.Count} files");
        }

        var result = await Publisher.PublishAsync(new PublishOptions
        {
            Files = files,
            Inherited = inherited,
            RepoDirectory = repo,
            Version = version,
            Channel = channelName,
            LaunchExe = AndroidPrefix + "Ro3BuildSetting.json",
            CompressionLevel = 19,
            SkipChannel = true,
            CacheFile = Path.Combine(Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar))!, ".publish-cache-android.json.zst"),
        });
        Console.WriteLine($"manifest {version}: {result.Manifest.Files.Count} files, {result.Manifest.Files.Sum(f => f.Chunks.Sum(c => (long)c.StoredSize)) / 1048576} MiB to download; {result.NewChunks} new chunks ({result.NewStoredBytes / 1048576} MiB)");

        var channel = result.Channel;
        channel.Apk = await ApkAsync(repo, channelName, opts);
        using var key = RSA.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(keyFile));
        ChannelSigning.Sign(channel, key);
        await Publisher.WriteChannelAsync(repo, channel, CancellationToken.None);
        Console.WriteLine($"channel {channelName} -> {version}" + (channel.Apk is { } apk ? $", apk {apk.Version} (min {apk.MinVersion})" : ", no apk"));
        return 0;
    }

    /// <summary>--apk copies the APK into apk/&lt;version&gt;/; without it the channel keeps the APK it had.</summary>
    static async Task<ApkRelease?> ApkAsync(string repo, string channelName, Dictionary<string, List<string>> opts)
    {
        string? apkFile = Single(opts, "apk");
        string? apkVersion = Single(opts, "apk-version"), minVersion = Single(opts, "apk-min");
        ApkRelease? previous = null;
        string channelPath = Path.Combine(repo, "channels", channelName + ".json");
        if (File.Exists(channelPath))
        {
            previous = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(channelPath), Json.Compact)?.Apk;
        }
        if (apkFile is null)
        {
            if (previous is not null && minVersion is not null) previous.MinVersion = minVersion;
            return previous;
        }
        if (apkVersion is null) throw new ArgumentException("--apk needs --apk-version");
        if (!System.Text.RegularExpressions.Regex.IsMatch(apkVersion, @"^[0-9]+(\.[0-9]+)*$")) throw new ArgumentException($"bad APK version '{apkVersion}'");
        byte[] apk = await File.ReadAllBytesAsync(apkFile);
        string relative = $"apk/{apkVersion}/HeroSaga.apk";
        string target = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, apk);
        return new ApkRelease
        {
            Version = apkVersion,
            MinVersion = minVersion ?? previous?.MinVersion ?? apkVersion,
            Url = relative,
            Sha256 = Hashing.Sha256Hex(apk),
            Size = apk.Length,
        };
    }

    /// <summary>android-key: creates the channel signing key (never committed) and prints the public half for the app.</summary>
    public static int Key(string path)
    {
        using var key = RSA.Create(3072);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            Console.WriteLine($"created {path}: keep it out of git and backed up; losing it means a new APK for everyone");
        }
        Console.WriteLine(ChannelSigning.PublicKeyText(key));
        return 0;
    }

    static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException($"expected --option value, got '{args[i]}'");
            string name = args[i][2..];
            if (!result.TryGetValue(name, out var list)) result[name] = list = [];
            list.Add(args[++i]);
        }
        return result;
    }

    static string? Single(Dictionary<string, List<string>> opts, string name) => opts.TryGetValue(name, out var v) ? v[^1] : null;
}
