#:package K4os.Compression.LZ4@1.3.8
// Unity AssetBundle (UnityFS) tool for the Android port.
//   retarget <in.bundle|dir> <out.bundle|dir> [platform]   rewrites m_TargetPlatform of every SerializedFile in the
//       bundle (default 13 = Android; the PC client's are 19 = StandaloneWindows64, which an Android player refuses:
//       "The file can not be loaded because it was created for another build target"). A directory is walked
//       recursively and only the rewritten UnityFS bundles are written (same relative paths), to overlay a copy of
//       the original tree.
//   info <in.bundle>   prints the header, blocks and nodes (and each SerializedFile's version and platform).
//   scan <dir>   counts the objects of every UnityFS bundle under dir by Unity class ID, and lists the bundles
//       holding shaders (48 = Shader, 200 = ShaderVariantCollection).
// Output keeps the block layout; blocks are written back LZ4HC-compressed and the block table uncompressed.
using K4os.Compression.LZ4;
using System.Buffers.Binary;
using System.Text;

if (args.Length < 2) { Console.Error.WriteLine("usage: BundleTool retarget <in> <out> [platform] | info <in>"); return 2; }
switch (args[0])
{
    case "info":
        Info(Bundle.Read(File.ReadAllBytes(args[1])));
        return 0;
    case "retarget":
        int platform = args.Length > 3 ? int.Parse(args[3]) : 13;
        if (Directory.Exists(args[1])) return RetargetTree(args[1], args[2], platform);
        File.WriteAllBytes(args[2], Retarget(File.ReadAllBytes(args[1]), platform, out var n) ?? File.ReadAllBytes(args[1]));
        Console.WriteLine($"{args[1]}: {n} serialized file(s) retargeted");
        return 0;
    case "scan":
        return Scan(args[1]);
    case "dump":                                  // dump <in.bundle> <out dir>: the decompressed nodes, one file each
        var dumped = Bundle.Read(File.ReadAllBytes(args[1]));
        Directory.CreateDirectory(args[2]);
        foreach (var node in dumped.Nodes)
            File.WriteAllBytes(Path.Combine(args[2], node.Path), dumped.Data.AsSpan((int)node.Offset, (int)node.Size).ToArray());
        return 0;
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

static int Scan(string dir)
{
    var totals = new SortedDictionary<int, (int Count, long Bytes)>();
    var shaderBundles = new List<(string Path, int Shaders, long Bytes)>();
    int bundles = 0, failed = 0;
    foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    {
        var data = File.ReadAllBytes(path);
        if (!Bundle.IsUnityFS(data)) continue;
        bundles++;
        try
        {
            var b = Bundle.Read(data);
            int shaders = 0; long shaderBytes = 0;
            foreach (var n in b.Nodes)
            {
                if ((n.Flags & 4) == 0) continue;
                foreach (var (classId, size) in SerializedFile.Objects(b.Data.AsSpan((int)n.Offset, (int)n.Size)))
                {
                    totals[classId] = totals.TryGetValue(classId, out var t) ? (t.Count + 1, t.Bytes + size) : (1, size);
                    if (classId == 48) { shaders++; shaderBytes += size; }
                }
            }
            if (shaders > 0) shaderBundles.Add((Path.GetRelativePath(dir, path), shaders, shaderBytes));
        }
        catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); failed++; }
    }
    Console.WriteLine($"{bundles} bundles ({failed} unreadable)");
    foreach (var (id, (count, bytes)) in totals.OrderByDescending(kv => kv.Value.Bytes))
        Console.WriteLine($"  class {id,5}: {count,7} objects {bytes / 1048576.0,9:F1} MB");
    Console.WriteLine($"bundles with shaders: {shaderBundles.Count}");
    foreach (var (p, s, bytes) in shaderBundles.OrderByDescending(x => x.Bytes).Take(25))
        Console.WriteLine($"  {p}: {s} shader(s), {bytes / 1048576.0:F1} MB");
    return 0;
}

static int RetargetTree(string inDir, string outDir, int platform)
{
    int bundles = 0, other = 0, unchanged = 0, failed = 0;
    Span<byte> magic = stackalloc byte[8];
    foreach (var path in Directory.EnumerateFiles(inDir, "*", SearchOption.AllDirectories))
    {
        using (var f = File.OpenRead(path))
            if (f.Read(magic) < 8 || !magic.SequenceEqual("UnityFS\0"u8)) { other++; continue; }
        var rel = Path.GetRelativePath(inDir, path);
        var target = Path.Combine(outDir, rel);
        try
        {
            var output = Retarget(File.ReadAllBytes(path), platform, out _);
            if (output == null) { unchanged++; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, output);
            bundles++;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{rel}: {e.Message}");
            failed++;
        }
    }
    Console.WriteLine($"retargeted {bundles} bundle(s), {unchanged} already right, {other} non-UnityFS file(s) left out, {failed} failed");
    return failed == 0 ? 0 : 1;
}

// Returns the rewritten bundle, or null when every SerializedFile already targets the platform.
static byte[]? Retarget(byte[] data, int platform, out int count)
{
    var bundle = Bundle.Read(data);
    count = 0;
    foreach (var node in bundle.Nodes)
    {
        if ((node.Flags & 4) == 0) continue;    // 4 = SerializedFile; .resS / .resource nodes are raw data
        var file = bundle.Data.AsSpan((int)node.Offset, (int)node.Size);
        int at = SerializedFile.PlatformOffset(file, out bool little);
        int current = little ? BinaryPrimitives.ReadInt32LittleEndian(file[at..]) : BinaryPrimitives.ReadInt32BigEndian(file[at..]);
        if (current == platform) continue;
        if (little) BinaryPrimitives.WriteInt32LittleEndian(file[at..], platform);
        else BinaryPrimitives.WriteInt32BigEndian(file[at..], platform);
        count++;
    }
    return count == 0 ? null : bundle.Write();
}

static void Info(Bundle b)
{
    Console.WriteLine($"UnityFS v{b.FormatVersion} {b.UnityVersion} {b.UnityRevision} flags=0x{b.Flags:x} blocks={b.Blocks.Count} data={b.Data.Length}");
    foreach (var blk in b.Blocks) Console.WriteLine($"  block u={blk.Uncompressed} c={blk.Compressed} flags=0x{blk.Flags:x}");
    foreach (var n in b.Nodes)
    {
        var line = $"  node {n.Path} off={n.Offset} size={n.Size} flags={n.Flags}";
        if ((n.Flags & 4) != 0)
        {
            var file = b.Data.AsSpan((int)n.Offset, (int)n.Size);
            int at = SerializedFile.PlatformOffset(file, out bool little);
            line += $" serialized v{BinaryPrimitives.ReadUInt32BigEndian(file[8..])} platform={(little ? BinaryPrimitives.ReadInt32LittleEndian(file[at..]) : BinaryPrimitives.ReadInt32BigEndian(file[at..]))}";
        }
        Console.WriteLine(line);
    }
}

static class SerializedFile
{
    // (class ID, byte size) of every object, from the metadata's type and object tables (SerializedFile v19+).
    public static List<(int ClassId, long Size)> Objects(ReadOnlySpan<byte> f)
    {
        uint version = BinaryPrimitives.ReadUInt32BigEndian(f[8..]);
        int p = PlatformOffset(f, out bool little) + 4;
        if (version < 19 || !little) throw new InvalidDataException($"SerializedFile v{version} (little={little}) not supported");
        int I32(ReadOnlySpan<byte> s) { int v = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4; return v; }
        bool typeTrees = f[p++] != 0;
        int typeCount = I32(f);
        var classIds = new int[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            classIds[i] = I32(f);
            p += 1 + 2;                                   // isStrippedType, scriptTypeIndex
            if (classIds[i] == 114) p += 16;              // script ID
            p += 16;                                      // old type hash
            if (typeTrees)
            {
                int nodes = I32(f), strings = I32(f);
                p += nodes * 32 + strings;                // v19+: 32-byte nodes, then the string buffer
                if (version >= 21) { int deps = I32(f); p += deps * 4; }
            }
        }
        int objectCount = I32(f);
        var result = new List<(int, long)>(objectCount);
        for (int i = 0; i < objectCount; i++)
        {
            p = (p + 3) & ~3;                             // v14+: path IDs are 4-byte aligned
            p += 8;                                       // path ID
            p += version >= 22 ? 8 : 4;                   // byte start
            long size = BinaryPrimitives.ReadUInt32LittleEndian(f[p..]); p += 4;
            int typeIndex = I32(f);
            result.Add((typeIndex >= 0 && typeIndex < typeCount ? classIds[typeIndex] : -1, size));
        }
        return result;
    }

    // Offset of m_TargetPlatform: right after the Unity version string that opens the metadata.
    public static int PlatformOffset(ReadOnlySpan<byte> f, out bool little)
    {
        uint version = BinaryPrimitives.ReadUInt32BigEndian(f[8..]);
        if (version < 9) throw new InvalidDataException($"SerializedFile v{version} not supported");
        int header = version >= 22 ? 48 : 20;     // v22+: the legacy 20 bytes, then metadataSize, fileSize, dataOffset, unknown
        little = f[16] == 0;
        int end = f[header..].IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("no Unity version string in SerializedFile metadata");
        return header + end + 1;
    }
}

sealed class Bundle
{
    public uint FormatVersion, Flags;
    public string UnityVersion = "", UnityRevision = "";
    public byte[] Hash = new byte[16];
    public List<(uint Uncompressed, uint Compressed, ushort Flags)> Blocks = new();
    public List<(long Offset, long Size, uint Flags, string Path)> Nodes = new();
    public byte[] Data = Array.Empty<byte>();

    public static bool IsUnityFS(byte[] d) => d.Length > 8 && Encoding.ASCII.GetString(d, 0, 7) == "UnityFS" && d[7] == 0;

    public static Bundle Read(byte[] d)
    {
        if (!IsUnityFS(d)) throw new InvalidDataException("not a UnityFS bundle");
        var b = new Bundle();
        int p = 8;
        b.FormatVersion = U32(d, ref p);
        b.UnityVersion = CStr(d, ref p);
        b.UnityRevision = CStr(d, ref p);
        p += 8;                                   // total size
        uint compressedInfo = U32(d, ref p), uncompressedInfo = U32(d, ref p);
        b.Flags = U32(d, ref p);
        if (b.FormatVersion >= 7) p = Align16(p);
        int infoAt = (b.Flags & 0x80) != 0 ? d.Length - (int)compressedInfo : p;
        var info = Decompress(d.AsSpan(infoAt, (int)compressedInfo), (int)uncompressedInfo, b.Flags & 0x3F);
        if ((b.Flags & 0x80) == 0) p += (int)compressedInfo;
        if ((b.Flags & 0x200) != 0) p = Align16(p);

        int q = 0;
        Array.Copy(info, 0, b.Hash, 0, 16); q = 16;
        int blockCount = (int)U32(info, ref q);
        for (int i = 0; i < blockCount; i++) b.Blocks.Add((U32(info, ref q), U32(info, ref q), U16(info, ref q)));
        int nodeCount = (int)U32(info, ref q);
        for (int i = 0; i < nodeCount; i++) b.Nodes.Add(((long)U64(info, ref q), (long)U64(info, ref q), U32(info, ref q), CStr(info, ref q)));

        var data = new MemoryStream();
        foreach (var blk in b.Blocks)
        {
            data.Write(Decompress(d.AsSpan(p, (int)blk.Compressed), (int)blk.Uncompressed, (uint)(blk.Flags & 0x3F)));
            p += (int)blk.Compressed;
        }
        b.Data = data.ToArray();
        return b;
    }

    public byte[] Write()
    {
        var blocks = new MemoryStream();
        var table = new List<(uint, uint, ushort)>();
        int at = 0;
        foreach (var blk in Blocks)
        {
            var src = Data.AsSpan(at, (int)blk.Uncompressed);
            at += (int)blk.Uncompressed;
            var dst = new byte[LZ4Codec.MaximumOutputSize(src.Length)];
            int n = LZ4Codec.Encode(src, dst, LZ4Level.L09_HC);
            ushort flags = (ushort)((blk.Flags & ~0x3F) | 3);
            if (n <= 0 || n >= src.Length) { dst = src.ToArray(); n = src.Length; flags = (ushort)(blk.Flags & ~0x3F); }
            blocks.Write(dst, 0, n);
            table.Add(((uint)src.Length, (uint)n, flags));
        }

        var info = new MemoryStream();
        info.Write(Hash);
        W32(info, (uint)table.Count);
        foreach (var (u, c, f) in table) { W32(info, u); W32(info, c); W16(info, f); }
        W32(info, (uint)Nodes.Count);
        foreach (var (off, size, f, path) in Nodes) { W64(info, (ulong)off); W64(info, (ulong)size); W32(info, f); info.Write(Encoding.UTF8.GetBytes(path)); info.WriteByte(0); }
        var infoBytes = info.ToArray();

        // Same header, block table inline (not at the end) and uncompressed.
        uint flagsOut = (Flags & ~0x3Fu & ~0x80u);
        var head = new MemoryStream();
        head.Write("UnityFS\0"u8);
        W32(head, FormatVersion);
        head.Write(Encoding.ASCII.GetBytes(UnityVersion)); head.WriteByte(0);
        head.Write(Encoding.ASCII.GetBytes(UnityRevision)); head.WriteByte(0);
        long sizeAt = head.Position;
        W64(head, 0);
        W32(head, (uint)infoBytes.Length);
        W32(head, (uint)infoBytes.Length);
        W32(head, flagsOut);
        if (FormatVersion >= 7) Pad16(head);
        head.Write(infoBytes);
        if ((flagsOut & 0x200) != 0) Pad16(head);
        blocks.Position = 0;
        blocks.CopyTo(head);
        var result = head.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizeAt), result.Length);
        return result;
    }

    static byte[] Decompress(ReadOnlySpan<byte> src, int size, uint kind)
    {
        switch (kind)
        {
            case 0: return src.ToArray();
            case 2:
            case 3:
                var dst = new byte[size];
                int n = LZ4Codec.Decode(src, dst);
                if (n != size) throw new InvalidDataException($"LZ4 block decoded to {n}, expected {size}");
                return dst;
            default: throw new NotSupportedException($"compression {kind} (LZMA) not supported");
        }
    }

    static int Align16(int p) => (p + 15) & ~15;
    static void Pad16(MemoryStream s) { while (s.Position % 16 != 0) s.WriteByte(0); }
    static uint U32(byte[] d, ref int p) { var v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)); p += 4; return v; }
    static ushort U16(byte[] d, ref int p) { var v = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)); p += 2; return v; }
    static ulong U64(byte[] d, ref int p) { var v = BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p)); p += 8; return v; }
    static string CStr(byte[] d, ref int p) { int e = Array.IndexOf(d, (byte)0, p); var s = Encoding.UTF8.GetString(d, p, e - p); p = e + 1; return s; }
    static void W32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    static void W16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    static void W64(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); s.Write(b); }
}
