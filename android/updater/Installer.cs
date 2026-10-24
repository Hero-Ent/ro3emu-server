using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ro3Android.Updater
{
    public enum Stage { Idle, Checking, Scanning, Ready, Downloading, Done, Failed }

    /// <summary>What an update will do, known before it downloads anything (the app asks before a big download).</summary>
    public sealed class Plan
    {
        public Channel Channel;
        public Manifest Manifest;
        internal byte[] ManifestRaw;
        public List<ManifestFile> Install = new List<ManifestFile>();
        public List<string> Delete = new List<string>();
        /// <summary>Compressed bytes to download (chunks found in files already on disk are copied instead).</summary>
        public long DownloadBytes;
        /// <summary>Free space the update needs on the data folder, a margin included.</summary>
        public long NeedBytes;
        public bool UpToDate => Install.Count == 0 && Delete.Count == 0;
    }

    /// <summary>Live counters the UI reads every frame; written from worker threads.</summary>
    public sealed class Status
    {
        public volatile Stage Stage;
        public long Done, Total, FilesDone, FilesTotal;
        public volatile string Detail = "";

        internal void Set(Stage stage, long total, long filesTotal = 0)
        {
            Interlocked.Exchange(ref Done, 0);
            Interlocked.Exchange(ref FilesDone, 0);
            Interlocked.Exchange(ref Total, total);
            Interlocked.Exchange(ref FilesTotal, filesTotal);
            Stage = stage;
        }
    }

    /// <summary>
    /// Installs a channel of the patch repository into the app's data folder, one file at a time: each file is assembled
    /// in staging from chunks (downloaded, or copied out of files already on disk), checked against its SHA-256 and moved
    /// into place, and the state file records it. Unlike the PC launcher's all-or-nothing commit, an interrupted download
    /// (Android kills apps in the background, phones lose signal) keeps every finished file, and the game never runs
    /// while files are being replaced, so file-by-file is safe. The game only starts once the state says complete.
    /// </summary>
    public sealed class Installer
    {
        readonly string _root, _meta;
        readonly Repository _repo;
        readonly ConcurrentDictionary<string, InstalledFile> _files = new ConcurrentDictionary<string, InstalledFile>(StringComparer.Ordinal);
        readonly object _stateLock = new object();
        LocalState _state;

        public readonly Status Status = new Status();
        /// <summary>Files assembled at once; each keeps <see cref="ChunksAhead"/> chunk downloads going. Most files are a
        /// single small chunk (13k Lua files of a few KB), so the request latency (~200 ms to the CDN) sets the pace.</summary>
        public int FileWorkers = 32;
        public int ChunksAhead = 3;
        /// <summary>Free bytes of the data folder's volume (StatFs on Android).</summary>
        public Func<string, long> FreeSpace = path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;
        public Action<string> Log = _ => { };
        /// <summary>Wait before retry n of a chunk (n = 1..9).</summary>
        public Func<int, int> RetryDelayMs = n => Math.Min(15000, 300 * n * n);

        public Installer(string root, Repository repository)
        {
            _root = Path.GetFullPath(root);
            _meta = Path.Combine(_root, ".ro3patch");
            _repo = repository;
        }

        string StatePath => Path.Combine(_meta, "state.json");
        string InstalledManifestPath => Path.Combine(_meta, "installed.json.zst");
        string StagingDir => Path.Combine(_meta, "staging");

        string Local(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        public LocalState State => _state ?? (_state = LocalState.Load(StatePath));

        /// <summary>The last update finished for this channel's current release: the game can start without the network.</summary>
        public bool IsComplete(Channel channel = null) =>
            State.Complete && (channel == null || string.Equals(State.ManifestSha256, channel.ManifestSha256, StringComparison.OrdinalIgnoreCase));

        /// <summary>Cheap check that the installed files are still there (size and time only).</summary>
        public List<string> QuickVerify()
        {
            var changed = new List<string>();
            foreach (var pair in State.Files)
            {
                var info = new FileInfo(Local(pair.Key));
                if (!info.Exists || info.Length != pair.Value.Size || info.LastWriteTimeUtc.Ticks != pair.Value.MtimeTicks) changed.Add(pair.Key);
            }
            return changed;
        }

        /// <summary>
        /// Compares the release with the folder. Files the state does not vouch for (a phone filled by adb, a repair)
        /// are hashed when their size matches, in parallel; <paramref name="verifyAll"/> hashes every file.
        /// </summary>
        public async Task<Plan> PrepareAsync(Channel channel, bool verifyAll, CancellationToken ct)
        {
            Status.Set(Stage.Checking, 0);
            var plan = new Plan { Channel = channel };
            plan.ManifestRaw = await _repo.GetManifestAsync(channel, ct).ConfigureAwait(false);
            plan.Manifest = Manifest.Parse(plan.ManifestRaw);
            var state = State;
            _files.Clear();

            var toHash = new List<ManifestFile>();
            long hashBytes = 0;
            foreach (var file in plan.Manifest.Files)
            {
                var info = new FileInfo(Local(file.Path));
                if (!info.Exists) { plan.Install.Add(file); continue; }
                bool vouched = state.Files.TryGetValue(file.Path, out var entry) && entry.Size == info.Length && entry.MtimeTicks == info.LastWriteTimeUtc.Ticks;
                if (vouched && !verifyAll)
                {
                    if (string.Equals(entry.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) _files[file.Path] = entry;
                    else plan.Install.Add(file);
                    continue;
                }
                if (info.Length != file.Size) { plan.Install.Add(file); continue; }
                toHash.Add(file);
                hashBytes += file.Size;
            }

            if (toHash.Count > 0)
            {
                Log($"hashing {toHash.Count} files ({hashBytes >> 20} MB) the state does not vouch for");
                Status.Set(Stage.Scanning, hashBytes, toHash.Count);
                var bad = new ConcurrentBag<ManifestFile>();
                await ForEachAsync(toHash, Math.Max(2, Environment.ProcessorCount), (file, token) =>
                {
                    string path = Local(file.Path);
                    Status.Detail = file.Path;
                    string hash = HashFile(path, n => Interlocked.Add(ref Status.Done, n), token);
                    var info = new FileInfo(path);
                    if (string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        _files[file.Path] = new InstalledFile { Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks, Sha256 = hash };
                    else bad.Add(file);
                    Interlocked.Increment(ref Status.FilesDone);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
                plan.Install.AddRange(bad);
            }

            var release = new HashSet<string>(plan.Manifest.Files.Select(f => f.Path), StringComparer.Ordinal);
            plan.Delete = state.Files.Keys.Where(p => !release.Contains(p)).ToList();
            if (verifyAll) plan.Delete.AddRange(UnknownFiles(release).Where(p => !plan.Delete.Contains(p)));

            var index = ReuseIndex(plan);
            long replaced = 0, largest = 0;
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in plan.Install)
            {
                foreach (var chunk in file.Chunks)
                    if (!index.ContainsKey(chunk.Hash) && counted.Add(chunk.Hash)) plan.DownloadBytes += chunk.StoredSize;
                var info = new FileInfo(Local(file.Path));
                if (info.Exists) replaced += info.Length;
                largest = Math.Max(largest, file.Size);
            }
            long writes = plan.Install.Sum(f => f.Size);
            plan.NeedBytes = Math.Max(0, writes - replaced) + largest + (300L << 20);
            Status.Set(Stage.Ready, plan.DownloadBytes, plan.Install.Count);
            Log($"release {plan.Manifest.Version}: {plan.Install.Count} files to install, {plan.Delete.Count} to delete, {plan.DownloadBytes >> 20} MB to download");
            return plan;
        }

        /// <summary>Files under StreamingAssets that no release owns (left by adb pushes or older layouts).</summary>
        IEnumerable<string> UnknownFiles(HashSet<string> release)
        {
            string dir = Path.Combine(_root, "StreamingAssets");
            if (!Directory.Exists(dir)) yield break;
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(_root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                if (!release.Contains(relative)) yield return relative;
            }
        }

        sealed class ChunkSource
        {
            public string Path;
            public long Offset;
        }

        /// <summary>Where chunks a changed file needs already sit on disk: files vouched for, by their chunk layout.</summary>
        Dictionary<string, ChunkSource> ReuseIndex(Plan plan)
        {
            var layouts = new Dictionary<string, List<ChunkRef>>(StringComparer.OrdinalIgnoreCase);
            void Add(Manifest m) { foreach (var f in m.Files) if (!layouts.ContainsKey(f.Sha256)) layouts[f.Sha256] = f.Chunks; }
            Add(plan.Manifest);
            try { if (File.Exists(InstalledManifestPath)) Add(Manifest.Parse(File.ReadAllBytes(InstalledManifestPath))); }
            catch (Exception e) when (e is InvalidDataException || e is IOException) { }

            var index = new Dictionary<string, ChunkSource>(StringComparer.OrdinalIgnoreCase);
            // Files vouched for, plus the ones the state knows that are still on disk at their recorded size (about to be
            // replaced, or touched since): a deleted file's chunks must not count as already here.
            var sources = new List<KeyValuePair<string, InstalledFile>>(_files);
            foreach (var pair in State.Files)
            {
                if (_files.ContainsKey(pair.Key)) continue;
                var info = new FileInfo(Local(pair.Key));
                if (info.Exists && info.Length == pair.Value.Size) sources.Add(pair);
            }
            foreach (var pair in sources)
            {
                if (!layouts.TryGetValue(pair.Value.Sha256, out var chunks)) continue;
                long offset = 0;
                foreach (var chunk in chunks)
                {
                    if (!index.ContainsKey(chunk.Hash)) index[chunk.Hash] = new ChunkSource { Path = Local(pair.Key), Offset = offset };
                    offset += chunk.Size;
                }
            }
            return index;
        }

        /// <summary>Downloads and installs the plan. Finished files survive a cancel or a crash; run it again to resume.</summary>
        public async Task ApplyAsync(Plan plan, CancellationToken ct)
        {
            Directory.CreateDirectory(_meta);
            long free = FreeSpace(_root);
            if (free < plan.NeedBytes)
                throw new UpdateException($"Espaço insuficiente: a atualização precisa de {Format(plan.NeedBytes)} livres, o aparelho tem {Format(free)}.");

            var index = ReuseIndex(plan);
            // The new release is not complete until every file is in place: a crash meanwhile resumes, never plays.
            // Entries of files about to be replaced stay: their size and time stop matching once they are.
            lock (_stateLock)
            {
                var state = State;
                state.Complete = false;
                state.Version = plan.Manifest.Version;
                state.ManifestSha256 = plan.Channel.ManifestSha256;
                foreach (var pair in _files) state.Files[pair.Key] = pair.Value;
                state.Save(StatePath);
            }
            File.WriteAllBytes(InstalledManifestPath + ".next", plan.ManifestRaw);

            Status.Set(Stage.Downloading, plan.DownloadBytes, plan.Install.Count);
            var clock = Stopwatch.StartNew();
            if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true);
            // Big files first: they decide the total time, and the many small ones fill the gaps at the end.
            var order = plan.Install.OrderByDescending(f => f.Size).ToList();
            using (var journal = LocalState.OpenJournal(StatePath))
            {
                await ForEachAsync(order, FileWorkers, async (file, token) =>
                {
                    Status.Detail = file.Path;
                    var installed = await InstallFileAsync(file, index, token).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        _state.Files[file.Path] = installed;
                        _files[file.Path] = installed;
                        LocalState.WriteJournal(journal, file.Path, installed);
                    }
                    Interlocked.Increment(ref Status.FilesDone);
                }, ct).ConfigureAwait(false);
            }

            foreach (string path in plan.Delete)
            {
                try { File.Delete(Local(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _state.Files.Remove(path);
            }
            try { Directory.Delete(StagingDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            lock (_stateLock)
            {
                if (File.Exists(InstalledManifestPath)) File.Delete(InstalledManifestPath);
                File.Move(InstalledManifestPath + ".next", InstalledManifestPath);
                _state.Complete = true;
                _state.Save(StatePath);
            }
            Status.Stage = Stage.Done;
            Log($"installed {plan.Manifest.Version}: {plan.Install.Count} files, {Interlocked.Read(ref Status.Done) >> 20} MB downloaded in {clock.Elapsed.TotalSeconds:F0} s");
        }

        async Task<InstalledFile> InstallFileAsync(ManifestFile file, Dictionary<string, ChunkSource> index, CancellationToken ct)
        {
            string target = Local(file.Path);
            string stage = Path.Combine(StagingDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stage));
            using (var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var output = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    // A window of chunk reads/downloads runs ahead of the writer, which keeps the file's order.
                    var pending = new Queue<Task<byte[]>>();
                    int next = 0;
                    while (next < file.Chunks.Count || pending.Count > 0)
                    {
                        while (next < file.Chunks.Count && pending.Count < ChunksAhead)
                        {
                            var chunk = file.Chunks[next++];
                            pending.Enqueue(Task.Run(() => GetChunkAsync(chunk, index, ct), ct));
                        }
                        byte[] data = await pending.Dequeue().ConfigureAwait(false);
                        whole.AppendData(data);
                        await output.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                    }
                }
                string hash = Hex.Of(whole.GetHashAndReset());
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException($"{file.Path} não confere depois do download. Tente de novo.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target)) File.Delete(target);
            File.Move(stage, target);
            var info = new FileInfo(target);
            return new InstalledFile { Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks, Sha256 = file.Sha256 };
        }

        async Task<byte[]> GetChunkAsync(ChunkRef chunk, Dictionary<string, ChunkSource> index, CancellationToken ct)
        {
            if (index.TryGetValue(chunk.Hash, out var source))
            {
                // The source may have been replaced meanwhile: its bytes count only if they still hash right.
                try
                {
                    using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    {
                        var data = new byte[chunk.Size];
                        stream.Position = source.Offset;
                        int total = 0, read;
                        while (total < data.Length && (read = await stream.ReadAsync(data, total, data.Length - total, ct).ConfigureAwait(false)) > 0) total += read;
                        if (total == data.Length && Hex.Sha256(data, 0, data.Length) == chunk.Hash) return data;
                    }
                }
                catch (IOException) { }
            }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    byte[] stored = await _repo.GetChunkAsync(chunk, ct).ConfigureAwait(false);
                    byte[] raw;
                    using (var decompressor = new ZstdSharp.Decompressor()) raw = decompressor.Unwrap(stored).ToArray();
                    if (raw.Length != chunk.Size || Hex.Sha256(raw, 0, raw.Length) != chunk.Hash) throw new InvalidDataException("chunk " + chunk.Hash.Substring(0, 12) + " corrupt");
                    Interlocked.Add(ref Status.Done, chunk.StoredSize);
                    return raw;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && (e is IOException || e is HttpRequestException || e is InvalidDataException
                                          || e is TaskCanceledException || e is ZstdSharp.ZstdException))
                {
                    if (attempt >= 10) throw new UpdateException("O download falhou. Verifique sua conexão e tente de novo.", e);
                    Log($"chunk {chunk.Hash.Substring(0, 12)} retry {attempt}: {e.GetType().Name} {e.Message}");
                    await Task.Delay(RetryDelayMs(attempt), ct).ConfigureAwait(false);
                }
            }
        }

        static async Task ForEachAsync<T>(IEnumerable<T> items, int workers, Func<T, CancellationToken, Task> body, CancellationToken ct)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var queue = new ConcurrentQueue<T>(items);
                var tasks = Enumerable.Range(0, Math.Max(1, workers)).Select(_ => Task.Run(async () =>
                {
                    while (queue.TryDequeue(out var item))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        try { await body(item, linked.Token).ConfigureAwait(false); }
                        catch { linked.Cancel(); throw; }
                    }
                }, linked.Token)).ToArray();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch
                {
                    // The first real failure, not the cancellations it caused in the other workers.
                    var first = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception.InnerExceptions).FirstOrDefault(e => !(e is OperationCanceledException));
                    if (first != null && !ct.IsCancellationRequested) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
                    throw;
                }
            }
        }

        static string HashFile(string path, Action<long> onBytes, CancellationToken ct)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    onBytes(read);
                }
                return Hex.Of(hash.GetHashAndReset());
            }
        }

        public static string Format(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (bytes >> 20) + " MB";
    }

    public sealed class InstalledFile
    {
        public long Size, MtimeTicks;
        public string Sha256;
    }

    /// <summary>.ro3patch/state.json: the release being (or last) installed and the files vouched for.</summary>
    public sealed class LocalState
    {
        public string Version = "", ManifestSha256 = "";
        public bool Complete;
        public Dictionary<string, InstalledFile> Files = new Dictionary<string, InstalledFile>(StringComparer.Ordinal);

        public static LocalState Load(string path)
        {
            var state = new LocalState();
            try
            {
                if (!File.Exists(path)) return state;
                var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllBytes(path));
                state.Version = MiniJson.Str(root, "version");
                state.ManifestSha256 = MiniJson.Str(root, "manifestSha256");
                state.Complete = MiniJson.Bool(root, "complete");
                foreach (var f in MiniJson.Obj(root, "files"))
                {
                    var v = (Dictionary<string, object>)f.Value;
                    state.Files[f.Key] = new InstalledFile { Size = MiniJson.Long(v, "size"), MtimeTicks = MiniJson.Long(v, "mtime"), Sha256 = MiniJson.Str(v, "sha256") };
                }
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is InvalidCastException || e is NullReferenceException)
            {
                state = new LocalState(); // unreadable: every file gets hashed again, nothing is lost
            }
            try
            {
                if (File.Exists(JournalPath(path)))
                {
                    foreach (var line in File.ReadAllLines(JournalPath(path)))
                    {
                        var parts = line.Split('\t');
                        // A line cut by a crash is skipped: that file is hashed again.
                        if (parts.Length == 4 && long.TryParse(parts[1], out long size) && long.TryParse(parts[2], out long mtime) && Hex.IsSha256(parts[3]))
                            state.Files[parts[0]] = new InstalledFile { Size = size, MtimeTicks = mtime, Sha256 = parts[3] };
                    }
                }
            }
            catch (IOException) { }
            return state;
        }

        /// <summary>Files installed since the last <see cref="Save"/>, one line each: rewriting the whole state (36k
        /// entries) after every file would cost more than the small files themselves.</summary>
        public static string JournalPath(string path) => path + ".journal";

        public static StreamWriter OpenJournal(string path) =>
            new StreamWriter(new FileStream(JournalPath(path), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

        public static void WriteJournal(StreamWriter journal, string relative, InstalledFile file)
        {
            journal.Write(relative + "\t" + file.Size + "\t" + file.MtimeTicks + "\t" + file.Sha256 + "\n");
            journal.Flush();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            var sb = new StringBuilder(Files.Count * 140 + 200);
            sb.Append("{\"version\":");
            MiniJson.WriteString(sb, Version);
            sb.Append(",\"manifestSha256\":");
            MiniJson.WriteString(sb, ManifestSha256);
            sb.Append(",\"complete\":").Append(Complete ? "true" : "false").Append(",\"files\":{");
            bool first = true;
            foreach (var pair in Files)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('\n');
                MiniJson.WriteString(sb, pair.Key);
                sb.Append(":{\"size\":").Append(pair.Value.Size).Append(",\"mtime\":").Append(pair.Value.MtimeTicks).Append(",\"sha256\":");
                MiniJson.WriteString(sb, pair.Value.Sha256);
                sb.Append('}');
            }
            sb.Append("}}");
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            if (File.Exists(JournalPath(path))) File.Delete(JournalPath(path));
        }
    }

    /// <summary>A failure whose message can be shown to the player as it is.</summary>
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message, Exception inner = null) : base(message, inner) { }
    }
}
