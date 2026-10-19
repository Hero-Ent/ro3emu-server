using System.Net.Http.Headers;
using System.Text.Json;

namespace Ro3Emu.Patcher;

/// <summary>Reads a release repository over HTTP(S). Only <c>channels/*.json</c> and <c>news.json</c> are fetched uncached.</summary>
public sealed class RepositoryClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsClient;
    readonly Uri _primaryUri;
    readonly Uri? _fallbackUri;
    Uri _activeBaseUri;

    public RepositoryClient(Uri baseUri, HttpMessageHandler? handler = null)
        : this(baseUri, fallbackUri: null, handler)
    {
    }

    public RepositoryClient(Uri baseUri, Uri? fallbackUri, HttpMessageHandler? handler = null)
    {
        _primaryUri = EnsureTrailingSlash(baseUri);
        _fallbackUri = fallbackUri is not null ? EnsureTrailingSlash(fallbackUri) : null;
        _activeBaseUri = _primaryUri;

        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                MaxConnectionsPerServer = 32,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            };
            _http = new HttpClient(sockets)
            {
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Timeout = TimeSpan.FromSeconds(45)
            };
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);
        }

        _ownsClient = true;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ro3EmuLauncher/1.0");
    }

    static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    public Uri BaseUri => _activeBaseUri;

    public async Task<ChannelInfo> GetChannelAsync(string channel, CancellationToken ct)
    {
        try
        {
            return await FetchChannelFromUriAsync(_activeBaseUri, channel, ct);
        }
        catch (Exception) when (_fallbackUri != null && _activeBaseUri != _fallbackUri)
        {
            var result = await FetchChannelFromUriAsync(_fallbackUri, channel, ct);
            _activeBaseUri = _fallbackUri;
            return result;
        }
    }

    async Task<ChannelInfo> FetchChannelFromUriAsync(Uri baseUri, string channel, CancellationToken ct)
    {
        var targetUri = new Uri(baseUri, $"channels/{channel}.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new PatcherException($"Channel '{channel}' does not exist on the update server.");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ChannelInfo>(stream, Json.Compact, ct)
            ?? throw new PatcherException("The update server returned an empty channel.");
    }

    public async Task<(Manifest Manifest, byte[] Raw)> GetManifestAsync(ChannelInfo channel, CancellationToken ct)
    {
        PathRules.ValidateRelative(channel.Manifest);
        byte[] raw;
        try
        {
            raw = await _http.GetByteArrayAsync(new Uri(_activeBaseUri, channel.Manifest), ct);
        }
        catch (Exception) when (_fallbackUri != null && _activeBaseUri != _fallbackUri)
        {
            _activeBaseUri = _fallbackUri;
            raw = await _http.GetByteArrayAsync(new Uri(_activeBaseUri, channel.Manifest), ct);
        }
        if (!string.Equals(Hashing.Sha256Hex(raw), channel.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherException("The release manifest is corrupt (hash mismatch). Try again in a moment.");
        }
        return (Manifest.FromCompressedBytes(raw), raw);
    }

    /// <summary>Downloads one stored chunk; <paramref name="onBytes"/> reports compressed bytes as they arrive.</summary>
    public async Task<byte[]> GetChunkAsync(string hash, int storedSize, Action<int>? onBytes, CancellationToken ct)
    {
        var targetUri = new Uri(_activeBaseUri, PathRules.ChunkRelative(hash));
        using var response = await _http.GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[storedSize];
        int total = 0;
        while (total < storedSize)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(64 * 1024, storedSize - total)), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
            onBytes?.Invoke(read);
        }
        if (total != storedSize || await stream.ReadAsync(new byte[1], ct) != 0)
        {
            throw new IOException($"Chunk {hash[..12]} has the wrong size.");
        }
        return buffer;
    }

    public async Task<byte[]?> TryGetBytesAsync(string relative, CancellationToken ct)
    {
        var targetUri = new Uri(_activeBaseUri, relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (_fallbackUri != null && _activeBaseUri != _fallbackUri)
            {
                using var fbReq = new HttpRequestMessage(HttpMethod.Get, new Uri(_fallbackUri, relative));
                fbReq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var fbResp = await _http.SendAsync(fbReq, ct);
                if (fbResp.IsSuccessStatusCode) return await fbResp.Content.ReadAsByteArrayAsync(ct);
            }
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>An expected failure whose message can be shown to the player as is.</summary>
public sealed class PatcherException(string message, Exception? inner = null) : Exception(message, inner);
