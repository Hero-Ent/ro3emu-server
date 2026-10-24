using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// AssetBundle.LoadFromFile[Async] for the game's code (android\tools\AndroidPatch.cs redirects every call here).
    /// The PC client's bundles - loose, or packed at an offset inside the RO3V files of the VFS - target
    /// StandaloneWindows64 (19), which an Android player refuses. The first time a bundle whose SerializedFiles target
    /// another platform is loaded, a copy retargeted to Android (13) is written to the app's bundle-cache folder: only the
    /// blocks that hold a SerializedFile header are decompressed (and stored raw), every other block is copied as it is.
    /// Every load then reads that copy with the engine's own LoadFromFile (on demand, nothing held in managed memory).
    /// Bundles already for Android take the engine's path untouched.
    /// </summary>
    public static class Bundles
    {
        private const int Android = 13;
        private static readonly HashSet<string> Untouched = new HashSet<string>();
        private static string _cacheDir;

        private static StreamWriter _trace;
        private static bool _traceChecked;
        private static readonly Stopwatch TraceClock = Stopwatch.StartNew();

        /// <summary>With a file named vfs-trace in the app's data folder, every bundle load is appended to bundle-access.log
        /// ("&lt;seconds&gt; bundle &lt;path&gt; &lt;offset&gt;"), next to libfsni's vfs-access.log: which data each part of the game
        /// needs, for the updater's on-demand download.</summary>
        private static void Trace(string path, ulong offset)
        {
            if (!_traceChecked)
            {
                _traceChecked = true;
                try
                {
                    if (File.Exists(Path.Combine(Application.persistentDataPath, "vfs-trace")))
                        _trace = new StreamWriter(new FileStream(Path.Combine(Application.persistentDataPath, "bundle-access.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }
                catch (IOException) { }
            }
            if (_trace == null) return;
            lock (_trace) _trace.WriteLine($"{(long)TraceClock.Elapsed.TotalSeconds} bundle {path} {offset}");
        }

        public static AssetBundle LoadFromFile(string path) => LoadFromFile(path, 0u, 0ul);
        public static AssetBundle LoadFromFile(string path, uint crc) => LoadFromFile(path, crc, 0ul);

        public static AssetBundle LoadFromFile(string path, uint crc, ulong offset)
        {
            var cached = Retargeted(path, offset);
            return cached != null ? AssetBundle.LoadFromFile(cached) : AssetBundle.LoadFromFile(path, crc, offset);
        }

        public static AssetBundleCreateRequest LoadFromFileAsync(string path) => LoadFromFileAsync(path, 0u, 0ul);
        public static AssetBundleCreateRequest LoadFromFileAsync(string path, uint crc) => LoadFromFileAsync(path, crc, 0ul);

        public static AssetBundleCreateRequest LoadFromFileAsync(string path, uint crc, ulong offset)
        {
            var cached = Retargeted(path, offset);
            return cached != null ? AssetBundle.LoadFromFileAsync(cached) : AssetBundle.LoadFromFileAsync(path, crc, offset);
        }

        /// <summary>The path of the Android copy of the bundle at path+offset (written on first use), or null when the
        /// bundle already targets Android or is not a UnityFS bundle (the engine then reports whatever is wrong).</summary>
        private static string Retargeted(string path, ulong offset)
        {
            Trace(path, offset);
            string key = null;
            try
            {
                key = $"{Path.GetFileName(path)}_{offset}_{new FileInfo(path).Length}";
                if (Untouched.Contains(key)) return null;
                if (_cacheDir == null) _cacheDir = Path.Combine(Application.persistentDataPath, "bundle-cache");
                var cached = Path.Combine(_cacheDir, key + ".bundle");
                if (File.Exists(cached)) return cached;

                var watch = Stopwatch.StartNew();
                byte[] d;
                uint format;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                {
                    f.Position = (long)offset;
                    var head = new byte[64];
                    if (Read(f, head, 0, head.Length) < 48 || Encoding.ASCII.GetString(head, 0, 7) != "UnityFS") { Untouched.Add(key); return null; }
                    int p = 8;
                    format = U32(head, ref p);
                    SkipCStr(head, ref p);
                    SkipCStr(head, ref p);
                    long total = (long)U64(head, ref p);
                    if (total <= 0 || total > int.MaxValue) { Untouched.Add(key); return null; }
                    d = new byte[total];
                    f.Position = (long)offset;
                    if (Read(f, d, 0, d.Length) != d.Length) return null;
                }
                Directory.CreateDirectory(_cacheDir);
                var temp = cached + ".tmp";
                if (!Retarget(d, format, temp)) { Untouched.Add(key); return null; }
                if (File.Exists(cached)) File.Delete(cached);
                File.Move(temp, cached);
                Debug.Log($"[Ro3Android] bundle cached {key} ({d.Length / 1024} KB) in {watch.ElapsedMilliseconds} ms");
                return cached;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ro3Android] bundle retarget failed for {path}@{offset}: {e.Message}");
                if (key != null) Untouched.Add(key);
                return null;
            }
        }

        /// <summary>Writes the retargeted bundle to outPath; false (nothing written) when nothing needed retargeting.</summary>
        private static bool Retarget(byte[] d, uint format, string outPath)
        {
            int p = 8 + 4;
            SkipCStr(d, ref p);
            SkipCStr(d, ref p);
            int sizeAt = p;
            p += 8;
            uint compressedInfo = U32(d, ref p), uncompressedInfo = U32(d, ref p), flags = U32(d, ref p);
            if (format >= 7) p = (p + 15) & ~15;
            int infoAt = (flags & 0x80) != 0 ? d.Length - (int)compressedInfo : p;
            var info = Decompress(d, infoAt, (int)compressedInfo, (int)uncompressedInfo, flags & 0x3F);
            if ((flags & 0x80) == 0) p += (int)compressedInfo;
            if ((flags & 0x200) != 0) p = (p + 15) & ~15;

            int q = 16;
            int blockCount = (int)U32(info, ref q);
            var blocks = new Blocks(d, blockCount);
            for (int i = 0; i < blockCount; i++)
            {
                int u = (int)U32(info, ref q), c = (int)U32(info, ref q);
                blocks.Add(i, u, c, U16(info, ref q), p);
                p += c;
            }
            int nodeCount = (int)U32(info, ref q);

            int changed = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                long off = (long)U64(info, ref q); U64(info, ref q);
                uint nodeFlags = U32(info, ref q);
                SkipCStr(info, ref q);
                if ((nodeFlags & 4) == 0) continue;
                // m_TargetPlatform: right after the Unity version string that opens a SerializedFile's metadata.
                uint version = (uint)(blocks[off + 8] << 24 | blocks[off + 9] << 16 | blocks[off + 10] << 8 | blocks[off + 11]);
                bool little = blocks[off + 16] == 0;
                long at = off + (version >= 22 ? 48 : 20);
                while (blocks[at] != 0) at++;
                at++;
                int current = little ? blocks[at] | blocks[at + 1] << 8 | blocks[at + 2] << 16 | blocks[at + 3] << 24
                                     : blocks[at] << 24 | blocks[at + 1] << 16 | blocks[at + 2] << 8 | blocks[at + 3];
                if (current == Android) continue;
                for (int k = 0; k < 4; k++) blocks[at + k] = (byte)(little ? Android >> (8 * k) : Android >> (8 * (3 - k)));
                changed++;
            }
            if (changed == 0) return false;

            // The block table inline and uncompressed; the blocks that were touched stored raw (compression bits 0).
            for (int i = 0; i < blockCount; i++)
            {
                if (!blocks.Touched(i)) continue;
                int e = 16 + 4 + i * 10;
                Bin.PutU32BE(info, e + 4, (uint)blocks.Size(i));
                Bin.PutU16BE(info, e + 8, (ushort)(blocks.Flags(i) & ~0x3F));
            }
            uint flagsOut = flags & ~0x3Fu & ~0x80u;
            using (var o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                o.Write(d, 0, sizeAt);
                o.Write(new byte[8], 0, 8);
                W32(o, (uint)info.Length); W32(o, (uint)info.Length); W32(o, flagsOut);
                if (format >= 7) while (o.Position % 16 != 0) o.WriteByte(0);
                o.Write(info, 0, info.Length);
                if ((flagsOut & 0x200) != 0) while (o.Position % 16 != 0) o.WriteByte(0);
                for (int i = 0; i < blockCount; i++) blocks.WriteTo(o, i);
                long length = o.Position;
                o.Position = sizeAt;
                var size = new byte[8];
                Bin.PutU64BE(size, 0, (ulong)length);
                o.Write(size, 0, 8);
            }
            return true;
        }

        /// <summary>A bundle's data blocks, addressed by position in the uncompressed stream; a block is decompressed
        /// only when one of its bytes is read or written.</summary>
        private sealed class Blocks
        {
            private readonly byte[] _file;
            private readonly long[] _start;
            private readonly int[] _size, _compressed, _at;
            private readonly ushort[] _flags;
            private readonly byte[][] _raw;

            public Blocks(byte[] file, int count)
            {
                _file = file;
                _start = new long[count]; _size = new int[count]; _compressed = new int[count]; _at = new int[count];
                _flags = new ushort[count]; _raw = new byte[count][];
            }

            public void Add(int i, int size, int compressed, ushort flags, int at)
            {
                _start[i] = i == 0 ? 0 : _start[i - 1] + _size[i - 1];
                _size[i] = size; _compressed[i] = compressed; _flags[i] = flags; _at[i] = at;
            }

            public bool Touched(int i) => _raw[i] != null;
            public int Size(int i) => _size[i];
            public ushort Flags(int i) => _flags[i];

            public byte this[long pos]
            {
                get { int b = Block(pos); return _raw[b][pos - _start[b]]; }
                set { int b = Block(pos); _raw[b][pos - _start[b]] = value; }
            }

            private int Block(long pos)
            {
                int lo = 0, hi = _start.Length - 1;
                while (lo < hi) { int mid = (lo + hi + 1) / 2; if (_start[mid] <= pos) lo = mid; else hi = mid - 1; }
                if (_raw[lo] == null) _raw[lo] = Decompress(_file, _at[lo], _compressed[lo], _size[lo], (uint)(_flags[lo] & 0x3F));
                return lo;
            }

            public void WriteTo(Stream o, int i)
            {
                if (_raw[i] != null) o.Write(_raw[i], 0, _raw[i].Length);
                else o.Write(_file, _at[i], _compressed[i]);
            }
        }

        private static byte[] Decompress(byte[] src, int at, int compressed, int size, uint kind)
        {
            var dst = new byte[size];
            if (kind == 0) { Buffer.BlockCopy(src, at, dst, 0, size); return dst; }
            if (kind != 2 && kind != 3) throw new NotSupportedException($"bundle compression {kind}");
            if (Lz4.Decode(src, at, compressed, dst) != size) throw new InvalidDataException("LZ4 block size mismatch");
            return dst;
        }

        private static int Read(Stream s, byte[] b, int o, int n) { int t = 0; while (t < n) { int r = s.Read(b, o + t, n - t); if (r <= 0) break; t += r; } return t; }
        private static uint U32(byte[] d, ref int p) { var v = Bin.U32BE(d, p); p += 4; return v; }
        private static ushort U16(byte[] d, ref int p) { var v = (ushort)(d[p] << 8 | d[p + 1]); p += 2; return v; }
        private static ulong U64(byte[] d, ref int p) { var v = (ulong)Bin.U32BE(d, p) << 32 | Bin.U32BE(d, p + 4); p += 8; return v; }
        private static void SkipCStr(byte[] d, ref int p) { p = Array.IndexOf(d, (byte)0, p) + 1; }
        private static void W32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    }

    /// <summary>LZ4 block format decoder (what UnityFS blocks with compression 2/3 hold).</summary>
    internal static class Lz4
    {
        public static int Decode(byte[] src, int at, int length, byte[] dst)
        {
            int s = at, end = at + length, o = 0;
            while (s < end)
            {
                int token = src[s++];
                int literals = token >> 4;
                if (literals == 15) { int b; do { b = src[s++]; literals += b; } while (b == 255); }
                Buffer.BlockCopy(src, s, dst, o, literals);
                s += literals; o += literals;
                if (s >= end) break;                    // the last sequence has literals only
                int distance = src[s] | (src[s + 1] << 8);
                s += 2;
                int match = (token & 15) + 4;
                if ((token & 15) == 15) { int b; do { b = src[s++]; match += b; } while (b == 255); }
                int from = o - distance;
                for (int i = 0; i < match; i++) dst[o++] = dst[from + i];   // overlapping copies go byte by byte
            }
            return o;
        }
    }
}

namespace Ro3Android.Hooks
{
    /// <summary>Byte-order helpers (Unity's mscorlib has no System.Buffers.Binary).</summary>
    internal static class Bin
    {
        public static uint U32BE(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);
        public static int I32LE(byte[] d, int p) => d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24;
        public static void PutU32BE(byte[] d, int p, uint v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }
        public static void PutU32BE(byte[] d, int p, int v) => PutU32BE(d, p, (uint)v);
        public static void PutI32LE(byte[] d, int p, int v) { d[p] = (byte)v; d[p + 1] = (byte)(v >> 8); d[p + 2] = (byte)(v >> 16); d[p + 3] = (byte)(v >> 24); }
        public static void PutU16BE(byte[] d, int p, ushort v) { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; }
        public static void PutU64BE(byte[] d, int p, ulong v) { PutU32BE(d, p, (uint)(v >> 32)); PutU32BE(d, p + 4, (uint)v); }
    }
}
