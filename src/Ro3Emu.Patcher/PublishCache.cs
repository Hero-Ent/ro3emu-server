using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Patcher;

/// <summary>
/// Remembers which chunks each source file was cut into, keyed by its full path, size and mtime, so publishing the
/// same unchanged client again (the local-server variant right after the production one, or the next release) does not
/// re-read ~20 GB. An entry is only used when the chunking parameters match and every chunk is still in the repository.
/// </summary>
public static class PublishCache
{
    public sealed class Entry
    {
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("mtime")] public long MtimeTicks { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        /// <summary>Chunk hash and raw size, in file order.</summary>
        [JsonPropertyName("chunks")] public List<CachedChunk> Chunks { get; set; } = new();

        public static Entry From(ManifestFile file, FileInfo source) => new()
        {
            Size = source.Length,
            MtimeTicks = source.LastWriteTimeUtc.Ticks,
            Sha256 = file.Sha256,
            Chunks = file.Chunks.Select(c => new CachedChunk { Hash = c.Hash, Size = c.Size }).ToList(),
        };
    }

    public sealed class CachedChunk
    {
        [JsonPropertyName("h")] public string Hash { get; set; } = "";
        [JsonPropertyName("s")] public int Size { get; set; }
    }

    sealed class CacheFile
    {
        [JsonPropertyName("chunking")] public string Chunking { get; set; } = "";
        [JsonPropertyName("files")] public Dictionary<string, Entry> Files { get; set; } = new();
    }

    static string Key(ChunkingParameters p) => $"{p.Min}/{p.Avg}/{p.Max}";

    public static Dictionary<string, Entry> Load(string? path, ChunkingParameters chunking)
    {
        var empty = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return empty;
        try
        {
            using var decompressor = new ZstdSharp.Decompressor();
            byte[] json = decompressor.Unwrap(File.ReadAllBytes(path)).ToArray();
            var file = JsonSerializer.Deserialize<CacheFile>(json, Json.Compact);
            if (file is null || file.Chunking != Key(chunking)) return empty;
            return new Dictionary<string, Entry>(file.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or ZstdSharp.ZstdException or InvalidDataException)
        {
            return empty; // a damaged cache only costs a full read
        }
    }

    public static void Save(string? path, ChunkingParameters chunking, Dictionary<string, Entry> files)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new CacheFile { Chunking = Key(chunking), Files = files }, Json.Compact);
            using var compressor = new ZstdSharp.Compressor(3);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, compressor.Wrap(json).ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation only.
        }
    }

    /// <summary>The manifest entry for a cached file, with stored sizes read from the repository; null if a chunk is missing.</summary>
    public static ManifestFile? TryRestore(Entry entry, string destination, string repo)
    {
        if (!Manifest.IsSha256Hex(entry.Sha256)) return null;
        var file = new ManifestFile { Path = destination, Size = entry.Size, Sha256 = entry.Sha256 };
        long total = 0;
        foreach (var chunk in entry.Chunks)
        {
            if (!Manifest.IsSha256Hex(chunk.Hash)) return null;
            var stored = new FileInfo(Path.Combine(repo, PathRules.ChunkRelative(chunk.Hash)));
            if (!stored.Exists || stored.Length == 0) return null;
            file.Chunks.Add(new ChunkRef { Hash = chunk.Hash, Size = chunk.Size, StoredSize = checked((int)stored.Length) });
            total += chunk.Size;
        }
        return total == entry.Size ? file : null;
    }
}
