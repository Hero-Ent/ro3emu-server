using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Ro3Android.Updater;
using Ro3Emu.Patcher;

namespace Ro3Android.Updater.Tests;

/// <summary>Serves a repository directory without a socket; counts chunk traffic and can fail on demand.</summary>
sealed class FileRepoHandler(string repo) : HttpMessageHandler
{
    public long ChunkBytes;
    public int ChunkRequests;
    public int FailAfterChunks = int.MaxValue;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string relative = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
        string path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        if (relative.StartsWith("chunks/") && Interlocked.Increment(ref ChunkRequests) > FailAfterChunks)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        if (!File.Exists(path)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        byte[] data = File.ReadAllBytes(path);
        if (relative.StartsWith("chunks/")) Interlocked.Add(ref ChunkBytes, data.Length);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }
}

public sealed class InstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ro3upd-" + Guid.NewGuid().ToString("N"));
    string Src => Path.Combine(_root, "src");
    string Repo => Path.Combine(_root, "repo");
    string Phone => Path.Combine(_root, "phone");
    readonly RSA _key = RSA.Create(2048);
    static readonly ChunkingParameters Small = new(1024, 4096, 16384);

    public InstallerTests() => Directory.CreateDirectory(Src);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    void Put(string relative, byte[] data)
    {
        string path = Path.Combine(Src, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    async Task<Ro3Android.Updater.Channel> Publish(string version)
    {
        var result = await Publisher.PublishAsync(new PublishOptions
        {
            Files = Publisher.CollectDirectory(Src, "StreamingAssets", _ => false),
            RepoDirectory = Repo,
            Version = version,
            Channel = "android-test",
            LaunchExe = "StreamingAssets/Ro3BuildSetting.json",
            Chunking = Small,
            CompressionLevel = 3,
            SkipChannel = true,
        });
        var channel = result.Channel;
        channel.Apk = new Ro3Emu.Patcher.ApkRelease { Version = "1.0.1", MinVersion = "1.0.0", Url = "apk/1.0.1/HeroSaga.apk", Sha256 = new string('a', 64) };
        ChannelSigning.Sign(channel, _key);
        await Publisher.WriteChannelAsync(Repo, channel, CancellationToken.None);
        return Ro3Android.Updater.Channel.Parse(File.ReadAllBytes(Path.Combine(Repo, "channels", "android-test.json")));
    }

    (Installer, FileRepoHandler) NewInstaller()
    {
        var handler = new FileRepoHandler(Repo);
        var installer = new Installer(Phone, new Repository(new Uri("http://repo.test/"), handler)) { FileWorkers = 3, RetryDelayMs = _ => 0 };
        return (installer, handler);
    }

    async Task Update(Ro3Android.Updater.Channel channel, Installer installer, bool verifyAll = false)
    {
        var plan = await installer.PrepareAsync(channel, verifyAll, CancellationToken.None);
        await installer.ApplyAsync(plan, CancellationToken.None);
    }

    void AssertSame(string relative)
    {
        Assert.Equal(File.ReadAllBytes(Path.Combine(Src, relative)), File.ReadAllBytes(Path.Combine(Phone, "StreamingAssets", relative)));
    }

    void Seed()
    {
        Put("Ro3BuildSetting.json", "{}"u8.ToArray());
        Put("VFS/aa/big.bundle", Bytes(200_000, 1));
        Put("VFS/pack.ro3v", Bytes(120_000, 2));
        for (int i = 0; i < 20; i++) Put($"Recovery/LuaPayload/{i}.lua", Bytes(500 + i, 100 + i));
    }

    [Fact]
    public async Task Signature_made_by_the_publisher_verifies_on_the_phone()
    {
        Seed();
        var channel = await Publish("1.0.0-android");
        string publicKey = ChannelSigning.PublicKeyText(_key);
        Assert.True(channel.VerifySignature(publicKey));
        channel.ManifestSha256 = new string('0', 64);
        Assert.False(channel.VerifySignature(publicKey));
        using var other = RSA.Create(2048);
        Assert.False((await Publish("1.0.1-android")).VerifySignature(ChannelSigning.PublicKeyText(other)));
    }

    [Fact]
    public async Task Fresh_install_then_nothing_to_do()
    {
        Seed();
        var channel = await Publish("1.0.0-android");
        var (installer, _) = NewInstaller();
        Assert.False(installer.IsComplete(channel));
        await Update(channel, installer);
        AssertSame("VFS/aa/big.bundle");
        AssertSame("Recovery/LuaPayload/7.lua");
        Assert.True(installer.IsComplete(channel));

        var (again, handler) = NewInstaller();
        Assert.True(again.IsComplete(channel));
        Assert.Empty(again.QuickVerify());
        var plan = await again.PrepareAsync(channel, false, CancellationToken.None);
        Assert.True(plan.UpToDate);
        Assert.Equal(0, handler.ChunkRequests);
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_the_finished_files()
    {
        Seed();
        var channel = await Publish("1.0.0-android");
        var (installer, handler) = NewInstaller();
        handler.FailAfterChunks = 15;
        var plan = await installer.PrepareAsync(channel, false, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => installer.ApplyAsync(plan, CancellationToken.None));
        Assert.False(new Installer(Phone, new Repository(new Uri("http://x/"))).IsComplete(channel));

        var (resumed, handler2) = NewInstaller();
        var plan2 = await resumed.PrepareAsync(channel, false, CancellationToken.None);
        Assert.True(plan2.Install.Count < plan.Install.Count, "finished files were kept");
        await resumed.ApplyAsync(plan2, CancellationToken.None);
        AssertSame("VFS/aa/big.bundle");
        AssertSame("VFS/pack.ro3v");
        Assert.True(resumed.IsComplete(channel));
    }

    [Fact]
    public async Task Update_downloads_only_the_changed_chunks_and_deletes_removed_files()
    {
        Seed();
        var v1 = await Publish("1.0.0-android");
        var (installer, _) = NewInstaller();
        await Update(v1, installer);

        var big = File.ReadAllBytes(Path.Combine(Src, "VFS", "aa", "big.bundle"));
        Array.Copy(Bytes(3000, 9), 0, big, 100_000, 3000);   // a patched region in the middle
        Put("VFS/aa/big.bundle", big);
        Put("VFS/new_map.ro3v", Bytes(30_000, 77));
        File.Delete(Path.Combine(Src, "Recovery", "LuaPayload", "3.lua"));
        var v2 = await Publish("1.0.1-android");

        var (updater, handler) = NewInstaller();
        Assert.False(updater.IsComplete(v2));
        await Update(v2, updater);
        AssertSame("VFS/aa/big.bundle");
        AssertSame("VFS/new_map.ro3v");
        Assert.False(File.Exists(Path.Combine(Phone, "StreamingAssets", "Recovery", "LuaPayload", "3.lua")));
        Assert.True(handler.ChunkBytes < 60_000, $"downloaded {handler.ChunkBytes} bytes: should be the new map and a few chunks of big.bundle");
        Assert.True(updater.IsComplete(v2));
    }

    [Fact]
    public async Task Files_pushed_without_a_state_are_adopted_by_hash()
    {
        Seed();
        var channel = await Publish("1.0.0-android");
        // What android\push-data.ps1 leaves: the files, no state; one of them is damaged, one extra is lying around.
        foreach (var file in Directory.EnumerateFiles(Src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(Phone, "StreamingAssets", Path.GetRelativePath(Src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var damaged = Path.Combine(Phone, "StreamingAssets", "VFS", "pack.ro3v");
        var bytes = File.ReadAllBytes(damaged);
        bytes[5000] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);
        string extra = Path.Combine(Phone, "StreamingAssets", "VFS", "Videos", "intro.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        File.WriteAllBytes(extra, [1, 2, 3]);

        var (installer, handler) = NewInstaller();
        var plan = await installer.PrepareAsync(channel, false, CancellationToken.None);
        Assert.Single(plan.Install);
        Assert.Empty(plan.Delete);
        await installer.ApplyAsync(plan, CancellationToken.None);
        AssertSame("VFS/pack.ro3v");
        // A damaged file the state never vouched for comes down whole (its chunk layout is unknown), nothing else does.
        Assert.Equal(plan.Install[0].Chunks.Count, handler.ChunkRequests);

        // Repair hashes everything and removes what no release owns.
        var (repair, _) = NewInstaller();
        var repairPlan = await repair.PrepareAsync(channel, true, CancellationToken.None);
        Assert.Contains("StreamingAssets/VFS/Videos/intro.mp4", repairPlan.Delete);
        await repair.ApplyAsync(repairPlan, CancellationToken.None);
        Assert.False(File.Exists(extra));
    }

    [Fact]
    public async Task Deleted_files_count_in_the_download_estimate()
    {
        Seed();
        var channel = await Publish("1.0.0-android");
        var (installer, _) = NewInstaller();
        await Update(channel, installer);
        File.Delete(Path.Combine(Phone, "StreamingAssets", "VFS", "pack.ro3v"));

        var (again, handler) = NewInstaller();
        var plan = await again.PrepareAsync(channel, false, CancellationToken.None);
        Assert.Single(plan.Install);
        Assert.Equal(plan.Install[0].Chunks.Select(c => c.Hash).Distinct().Count(), plan.Install[0].Chunks.Count);
        Assert.Equal(plan.Install[0].Chunks.Sum(c => (long)c.StoredSize), plan.DownloadBytes);
        await again.ApplyAsync(plan, CancellationToken.None);
        Assert.Equal(plan.DownloadBytes, handler.ChunkBytes);
        AssertSame("VFS/pack.ro3v");
    }

    [Fact]
    public async Task Not_enough_space_fails_before_writing()
    {
        Seed();
        var channel = await Publish("1.0.0-android");
        var (installer, _) = NewInstaller();
        installer.FreeSpace = _ => 1000;
        var plan = await installer.PrepareAsync(channel, false, CancellationToken.None);
        var e = await Assert.ThrowsAsync<UpdateException>(() => installer.ApplyAsync(plan, CancellationToken.None));
        Assert.Contains("Espaço insuficiente", e.Message);
        Assert.False(Directory.Exists(Path.Combine(Phone, "StreamingAssets")));
    }

    [Fact]
    public void Channel_json_written_by_the_publisher_parses()
    {
        var channel = new ChannelInfo { Channel = "android-live", Version = "1", Manifest = "manifests/1.json.zst", ManifestSha256 = new string('b', 64), Published = DateTimeOffset.UtcNow };
        var parsed = Ro3Android.Updater.Channel.Parse(JsonSerializer.SerializeToUtf8Bytes(channel, Json.Indented));
        Assert.Equal("android-live", parsed.Name);
        Assert.Null(parsed.Apk);
        Assert.Null(parsed.Signature);
    }
}

public sealed class RealManifestTests
{
    /// <summary>RO3_MANIFEST=patch-server\repo\manifests\x.json.zst: the real manifest parses, and how long it takes.</summary>
    [Fact]
    public void Real_manifest_parses()
    {
        string path = Environment.GetEnvironmentVariable("RO3_MANIFEST");
        if (string.IsNullOrEmpty(path)) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var manifest = Manifest.Parse(File.ReadAllBytes(path));
        Console.WriteLine($"{manifest.Files.Count} files, {manifest.Files.Sum(f => f.Chunks.Count)} chunks in {clock.ElapsedMilliseconds} ms, {GC.GetTotalMemory(false) >> 20} MB heap");
        Assert.True(manifest.Files.Count > 1000);
    }
}

public sealed class RawHttpTests
{
    /// <summary>RO3_RAWHTTP=1 with android\serve-repo.ps1 running: many parallel GETs on a few pooled connections,
    /// locally and over HTTPS from production.</summary>
    [Fact]
    public async Task Parallel_gets_against_real_servers()
    {
        if (Environment.GetEnvironmentVariable("RO3_RAWHTTP") != "1") return;
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "patch-server", "repo"));
        var chunks = Directory.EnumerateFiles(Path.Combine(repo, "chunks"), "*.zst", SearchOption.AllDirectories).Take(600).ToList();
        foreach (string server in new[] { "http://127.0.0.1:8790/", "https://patch.herosaga.com.br/" })
        {
            using var http = new RawHttp(32);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var results = await Task.WhenAll(chunks.Select(async path =>
            {
                string rel = Path.GetRelativePath(repo, path).Replace(Path.DirectorySeparatorChar, '/');
                var r = await http.GetAsync(new Uri(new Uri(server), rel), CancellationToken.None);
                // Production lacks the chunks only published on this PC (the Android overlay): those are a clean 404.
                return r.Status == 200 ? r.Body.Length == new FileInfo(path).Length : r.Status == 404 && server.StartsWith("https");
            }));
            Console.WriteLine($"{server}: {results.Count(x => x)}/{chunks.Count} ok in {clock.ElapsedMilliseconds} ms");
            Assert.All(results, Assert.True);
            var channel = await http.GetAsync(new Uri(new Uri(server), "channels/live.json?t=1"), CancellationToken.None);
            Assert.Equal(200, channel.Status);
            var missing = await http.GetAsync(new Uri(new Uri(server), "chunks/00/nope.zst"), CancellationToken.None);
            Assert.Equal(404, missing.Status);
        }
    }
}
