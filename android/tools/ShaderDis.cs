#:property AllowUnsafeBlocks=true
// Disassembles the D3D11 programs of the game's shaders, for porting them to Vulkan by hand. Input: AssetRipper's
// YAML export of a Shader (ShaderExportMode Yaml, which keeps the compiled blob as hex). The D3D11 platform's blob is
// LZ4 segments; segment 0 starts with the entry table (count, then offset/length/segment per entry): the parameter
// blobs (SerializedProgramParameters: which cbuffer offset, texture or sampler slot each property name gets) and the
// programs (Unity's sub-program header + a DXBC container). Each program is disassembled with the Windows
// d3dcompiler_47.dll (D3DDisassemble) into <out>\<shader>\<pass>_<stage>_<blob>.asm, headed by its keywords and
// its bindings (Unity strips the DXBC reflection, so names come from the YAML's m_NameIndices), plus index.txt.
// Usage: dotnet run ShaderDis.cs -- <shader .asset (YAML) or folder> <out dir> [shader name filter]
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

var input = args[0];
var outDir = args[1];
var filter = args.Length > 2 ? args[2] : null;
var files = Directory.Exists(input) ? Directory.GetFiles(input, "*.asset", SearchOption.AllDirectories) : new[] { input };
foreach (var file in files)
{
    var text = File.ReadAllText(file);
    var name = Regex.Match(text, @"^\s*m_Name: (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
    if (!text.Contains("compressedBlob:") || name.Length == 0) continue;
    if (filter != null && !name.Contains(filter)) continue;
    var platforms = U32s(Field(text, "platforms"));
    var offsets = ListField(text, "offsets").Select(U32s).ToList();
    var compressed = ListField(text, "compressedLengths").Select(U32s).ToList();
    var decompressed = ListField(text, "decompressedLengths").Select(U32s).ToList();
    var blob = Convert.FromHexString(Field(text, "compressedBlob"));
    int p = Array.IndexOf(platforms, 4u);     // ShaderCompilerPlatform.D3D11
    if (p < 0) { Console.WriteLine($"{name}: no D3D11 programs"); continue; }
    var segments = new List<byte[]>();
    for (int s = 0; s < offsets[p].Length; s++)
        segments.Add(Lz4(blob.AsSpan((int)offsets[p][s], (int)compressed[p][s]), (int)decompressed[p][s]));
    var head = segments[0];
    int count = BitConverter.ToInt32(head, 0);
    byte[] Entry(int e) => segments[BitConverter.ToInt32(head, 12 + e * 12)].AsSpan(BitConverter.ToInt32(head, 4 + e * 12), BitConverter.ToInt32(head, 8 + e * 12)).ToArray();

    if (Environment.GetEnvironmentVariable("SHADERDIS_ENTRY") is string dumpEntry)
    {
        var e = Entry(int.Parse(dumpEntry));
        for (int r = 0; r < e.Length; r += 32)
            Console.WriteLine($"{r,5}: {Convert.ToHexString(e.AsSpan(r, Math.Min(32, e.Length - r)))}  {new string(e.AsSpan(r, Math.Min(32, e.Length - r)).ToArray().Select(c => c >= 32 && c < 127 ? (char)c : '.').ToArray())}");
        return;
    }
    var keywordNames =Regex.Matches(Regex.Match(text, @"m_KeywordNames:\s*\n((?:\s*- .*\n)+)").Groups[1].Value, @"- (.*)").Select(m => m.Groups[1].Value.Trim()).ToList();
    var dir = Path.Combine(outDir, Regex.Replace(name, @"[^\w.-]+", "_"));
    Directory.CreateDirectory(dir);
    var index = new StringBuilder($"# {name}: {count} blob entries\n");
    int pass = -1, programs = 0;
    var names = new Dictionary<int, string>();
    var pending = new List<(int Blob, string Keywords, int Type)>();
    var lines = text.Split('\n');
    for (int li = 0; li < lines.Length; li++)
    {
        var line = lines[li];
        if (line.Contains("m_NameIndices:"))
        {
            pass++;
            names.Clear();
            for (int k = li + 1; k < lines.Length; k++)
            {
                var m = Regex.Match(lines[k], @"^\s{10}(\S+): (\d+)\s*$");
                if (!m.Success) break;
                names[int.Parse(m.Groups[2].Value)] = m.Groups[1].Value;
            }
        }
        var bm = Regex.Match(line, @"m_BlobIndex: (\d+)");
        if (bm.Success)
        {
            var kw = Regex.Match(lines[li + 1], @"m_KeywordIndices: ([0-9a-f]*)").Groups[1].Value;
            var kws = kw.Length == 0 ? "" : string.Join(" ", Enumerable.Range(0, kw.Length / 4).Select(q => Convert.ToUInt16(kw.Substring(q * 4 + 2, 2) + kw.Substring(q * 4, 2), 16)).Select(q => q < keywordNames.Count ? keywordNames[q] : $"#{q}"));
            int type = int.Parse(Regex.Match(lines[li + 3] + lines[li + 2], @"m_GpuProgramType: (\d+)").Groups[1].Value is var t && t.Length > 0 ? t : "-1");
            pending.Add((int.Parse(bm.Groups[1].Value), kws, type));
        }
        if (line.Contains("m_ParameterBlobIndices:"))
        {
            var param = new List<uint>();
            for (int k = li + 1; k < lines.Length && Regex.IsMatch(lines[k], @"^\s*- ?[0-9a-f]*\s*$"); k++)
            {
                var hex = lines[k].Trim().TrimStart('-').Trim();
                if (hex.Length > 0) param = U32s(hex).ToList();
            }
            for (int q = 0; q < pending.Count; q++)
            {
                var (b, kws, type) = pending[q];
                var data = Entry(b);
                int at = IndexOf(data, "DXBC"u8);
                if (at < 0) continue;
                int size = BitConverter.ToInt32(data, at + 24);
                var asm = Disassemble(data.AsSpan(at, Math.Min(size, data.Length - at)).ToArray());
                var stage = Regex.Match(asm, @"^\s*(vs|ps|gs|hs|ds|cs)_\d_\d", RegexOptions.Multiline).Groups[1].Value;
                var bindings = q < param.Count ? Parameters(Entry((int)param[q]), names) : "// (no parameter blob)\n";
                var fileName = $"p{pass}_{stage}_{b:D3}.asm";
                File.WriteAllText(Path.Combine(dir, fileName), $"// pass {pass}, blob {b}, keywords: {kws}\n{bindings}\n{asm}");
                index.AppendLine($"{fileName} [{kws}]");
                programs++;
            }
            pending.Clear();
        }
    }
    File.WriteAllText(Path.Combine(dir, "index.txt"), index.ToString());
    Console.WriteLine($"{name}: {programs} programs -> {dir}");
}

// A parameter blob is a run of length-prefixed names (4-byte aligned), each followed by its ints. For a cbuffer
// member: type (0 float, 1 int), rows, columns, is-matrix, array size, byte offset; for a cbuffer: size, member
// count; for a texture: slot, sampler, dimension... The ints are printed raw, with c<offset/16> for the last one.
static string Parameters(byte[] d, Dictionary<int, string> names)
{
    var sb = new StringBuilder();
    for (int k = 0; k + 8 <= d.Length; k += 4)
    {
        int len = BitConverter.ToInt32(d, k);
        if (len < 1 || len > 64 || k + 4 + len > d.Length) continue;
        bool ok = true;
        for (int c = 0; c < len && ok; c++) { byte ch = d[k + 4 + c]; ok = ch == '_' || ch == '$' || (ch < 128 && char.IsLetterOrDigit((char)ch)); }
        if (!ok || char.IsDigit((char)d[k + 4])) continue;
        var name = Encoding.ASCII.GetString(d, k + 4, len);
        int after = k + 4 + ((len + 3) & ~3);
        var ints = new List<int>();
        for (int q = 0; q < 6 && after + q * 4 + 4 <= d.Length; q++) ints.Add(BitConverter.ToInt32(d, after + q * 4));
        var last = ints.Count == 6 ? $"  (c{ints[5] / 16}.{"xyzw"[(ints[5] % 16) / 4]})" : "";
        sb.AppendLine($"// {name}: {string.Join(" ", ints)}{last}");
        k = after - 4;
    }
    return sb.ToString();
}

static int IndexOf(byte[] data, ReadOnlySpan<byte> what) => data.AsSpan().IndexOf(what);

static string Field(string text, string key) => Regex.Match(text, $@"^\s*{key}: ([0-9a-f]*)\s*$", RegexOptions.Multiline).Groups[1].Value;

static IEnumerable<string> ListField(string text, string key)
{
    var m = Regex.Match(text, $@"^\s*{key}:\s*\n((?:\s*- [0-9a-f]+\s*\n)+)", RegexOptions.Multiline);
    return Regex.Matches(m.Groups[1].Value, @"- ([0-9a-f]+)").Select(x => x.Groups[1].Value);
}

static uint[] U32s(string hex)
{
    var b = Convert.FromHexString(hex);
    var r = new uint[b.Length / 4];
    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToUInt32(b, i * 4);
    return r;
}

static byte[] Lz4(ReadOnlySpan<byte> src, int outLength)
{
    var dst = new byte[outLength];
    int s = 0, d = 0;
    while (s < src.Length)
    {
        int token = src[s++];
        int lit = token >> 4;
        if (lit == 15) { int b; do { b = src[s++]; lit += b; } while (b == 255); }
        src.Slice(s, lit).CopyTo(dst.AsSpan(d)); s += lit; d += lit;
        if (s >= src.Length) break;
        int off = src[s] | (src[s + 1] << 8); s += 2;
        int len = token & 15;
        if (len == 15) { int b; do { b = src[s++]; len += b; } while (b == 255); }
        len += 4;
        for (int k = 0; k < len; k++, d++) dst[d] = dst[d - off];
    }
    return dst;
}

static unsafe string Disassemble(byte[] dxbc)
{
    IntPtr blob;
    int hr = D3DDisassemble(dxbc, (nuint)dxbc.Length, 0, null, out blob);
    if (hr < 0 || blob == IntPtr.Zero) return $"// D3DDisassemble failed: 0x{hr:X8}\n";
    var vtable = *(IntPtr**)blob;
    var getPointer = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtable[3];
    var getSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtable[4];
    var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
    var text = Marshal.PtrToStringAnsi(getPointer(blob), (int)getSize(blob)).TrimEnd('\0');
    release(blob);
    return text;
}

[DllImport("d3dcompiler_47.dll")]
static extern int D3DDisassemble(byte[] src, nuint size, uint flags, string? comments, out IntPtr disassembly);
