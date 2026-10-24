using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Android.Updater
{
    // The repository format of src\Ro3Emu.Patcher (Manifest.cs), read with MiniJson.

    public sealed class ChunkRef
    {
        public string Hash;
        public int Size;
        public int StoredSize;
    }

    public sealed class ManifestFile
    {
        public string Path;
        public long Size;
        public string Sha256;
        public List<ChunkRef> Chunks = new List<ChunkRef>();
    }

    public sealed class Manifest
    {
        public string Version;
        public List<ManifestFile> Files = new List<ManifestFile>();

        public static Manifest Parse(byte[] compressed)
        {
            byte[] json;
            using (var decompressor = new ZstdSharp.Decompressor()) json = decompressor.Unwrap(compressed).ToArray();
            var root = MiniJson.Parse(json) as Dictionary<string, object> ?? throw new InvalidDataException("Manifesto inválido.");
            if (MiniJson.Long(root, "format") != 1) throw new InvalidDataException("Formato de manifesto desconhecido: atualize o app.");
            var manifest = new Manifest { Version = MiniJson.Str(root, "version") };
            foreach (Dictionary<string, object> f in MiniJson.Arr(root, "files"))
            {
                var file = new ManifestFile { Path = MiniJson.Str(f, "path"), Size = MiniJson.Long(f, "size"), Sha256 = MiniJson.Str(f, "sha256") };
                PathRules.Validate(file.Path);
                if (!Hex.IsSha256(file.Sha256)) throw new InvalidDataException("Hash inválido no manifesto: " + file.Path);
                foreach (Dictionary<string, object> c in MiniJson.Arr(f, "chunks"))
                {
                    var chunk = new ChunkRef { Hash = MiniJson.Str(c, "h"), Size = (int)MiniJson.Long(c, "s"), StoredSize = (int)MiniJson.Long(c, "c") };
                    if (!Hex.IsSha256(chunk.Hash) || chunk.Size < 0 || chunk.StoredSize <= 0) throw new InvalidDataException("Chunk inválido no manifesto: " + file.Path);
                    file.Chunks.Add(chunk);
                }
                manifest.Files.Add(file);
            }
            return manifest;
        }
    }

    public sealed class ApkRelease
    {
        public string Version = "", MinVersion = "", Url = "", Sha256 = "";
        public long Size;
    }

    public sealed class Channel
    {
        public string Name = "", Version = "", Manifest = "", ManifestSha256 = "", Signature;
        public ApkRelease Apk;

        public static Channel Parse(byte[] json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object> ?? throw new InvalidDataException("Canal de atualização inválido.");
            var channel = new Channel
            {
                Name = MiniJson.Str(root, "channel"),
                Version = MiniJson.Str(root, "version"),
                Manifest = MiniJson.Str(root, "manifest"),
                ManifestSha256 = MiniJson.Str(root, "manifestSha256"),
                Signature = root.TryGetValue("signature", out var sig) ? sig as string : null,
            };
            var apk = MiniJson.Obj(root, "apk");
            if (apk != null)
            {
                channel.Apk = new ApkRelease
                {
                    Version = MiniJson.Str(apk, "version"),
                    MinVersion = MiniJson.Str(apk, "minVersion"),
                    Url = MiniJson.Str(apk, "url"),
                    Sha256 = MiniJson.Str(apk, "sha256"),
                    Size = MiniJson.Long(apk, "size"),
                };
            }
            PathRules.Validate(channel.Manifest);
            if (!Hex.IsSha256(channel.ManifestSha256)) throw new InvalidDataException("Canal de atualização inválido.");
            return channel;
        }

        /// <summary>Same bytes as Ro3Emu.Patcher.ChannelSigning.Payload.</summary>
        public byte[] SignedPayload() => Encoding.UTF8.GetBytes(string.Join("\n", new[]
        {
            "ro3-channel-v1", Name, Version, Manifest, ManifestSha256.ToLowerInvariant(),
            Apk?.Version ?? "", Apk?.MinVersion ?? "", Apk?.Url ?? "", (Apk?.Sha256 ?? "").ToLowerInvariant(),
        }));

        /// <summary><paramref name="publicKey"/> is "modulus-base64:exponent-base64" (ChannelSigning.PublicKeyText).</summary>
        public bool VerifySignature(string publicKey)
        {
            if (string.IsNullOrEmpty(Signature)) return false;
            var parts = publicKey.Trim().Split(':');
            if (parts.Length != 2) throw new ArgumentException("bad public key");
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(parts[0]), Exponent = Convert.FromBase64String(parts[1]) });
                    return rsa.VerifyData(SignedPayload(), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    public static class PathRules
    {
        /// <summary>Rejects absolute paths and "..": a manifest must never write outside the app's folder.</summary>
        public static void Validate(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0)
                throw new InvalidDataException("Caminho inválido no manifesto: " + path);
            foreach (var part in path.Split('/'))
                if (part.Length == 0 || part == "." || part == "..") throw new InvalidDataException("Caminho inválido no manifesto: " + path);
        }

        public static string ChunkRelative(string hash) => "chunks/" + hash.Substring(0, 2) + "/" + hash + ".zst";
    }

    public static class Hex
    {
        public static string Of(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char ch in value)
                if (!(ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f')) return false;
            return true;
        }

        public static string Sha256(byte[] data, int offset, int count)
        {
            using (var sha = SHA256.Create()) return Of(sha.ComputeHash(data, offset, count));
        }
    }
}
