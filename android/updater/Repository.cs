using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ro3Android.Updater
{
    /// <summary>The patch repository over HTTP(S) (patch.herosaga.com.br, or the PC's patch server on the LAN).</summary>
    public sealed class Repository : IDisposable
    {
        readonly RawHttp _raw;
        readonly HttpClient _http;
        public Uri BaseUri { get; }

        /// <summary>The app's: <see cref="RawHttp"/> with <paramref name="connections"/> parallel connections.</summary>
        public Repository(Uri baseUri, int connections = 48)
        {
            BaseUri = WithSlash(baseUri);
            _raw = new RawHttp(connections);
        }

        /// <summary>Through an HttpMessageHandler (tests serve a folder without sockets).</summary>
        public Repository(Uri baseUri, HttpMessageHandler handler)
        {
            BaseUri = WithSlash(baseUri);
            _http = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(60) };
        }

        static Uri WithSlash(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");

        public Uri Resolve(string relative) => new Uri(BaseUri, relative);

        public async Task<Channel> GetChannelAsync(string name, CancellationToken ct)
        {
            // Query string: no CDN or proxy may answer with an old copy of the one mutable file.
            var (status, body) = await GetAsync(Resolve("channels/" + name + ".json?t=" + DateTime.UtcNow.Ticks), ct).ConfigureAwait(false);
            if (status == 404) throw new UpdateException($"O canal '{name}' não existe no servidor de atualização.");
            if (status != 200) throw new IOException($"channel: HTTP {status}");
            return Channel.Parse(body);
        }

        public async Task<byte[]> GetManifestAsync(Channel channel, CancellationToken ct)
        {
            byte[] raw = await GetOkAsync(channel.Manifest, ct).ConfigureAwait(false);
            if (!string.Equals(Hex.Sha256(raw, 0, raw.Length), channel.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("O manifesto da atualização veio corrompido. Tente de novo em instantes.");
            return raw;
        }

        public async Task<byte[]> GetChunkAsync(ChunkRef chunk, CancellationToken ct)
        {
            byte[] data = await GetOkAsync(PathRules.ChunkRelative(chunk.Hash), ct).ConfigureAwait(false);
            if (data.Length != chunk.StoredSize) throw new IOException($"chunk {chunk.Hash.Substring(0, 12)}: {data.Length} bytes, expected {chunk.StoredSize}");
            return data;
        }

        async Task<byte[]> GetOkAsync(string relative, CancellationToken ct)
        {
            var (status, body) = await GetAsync(Resolve(relative), ct).ConfigureAwait(false);
            if (status != 200) throw new IOException($"{relative}: HTTP {status}");
            return body;
        }

        async Task<(int, byte[])> GetAsync(Uri uri, CancellationToken ct)
        {
            if (_raw != null)
            {
                var r = await _raw.GetAsync(uri, ct).ConfigureAwait(false);
                return (r.Status, r.Body);
            }
            using (var response = await _http.GetAsync(uri, ct).ConfigureAwait(false))
                return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }

        public void Dispose()
        {
            _raw?.Dispose();
            _http?.Dispose();
        }
    }
}
