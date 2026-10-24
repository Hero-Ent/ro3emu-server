using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ro3Android.Updater
{
    /// <summary>
    /// A small HTTP/1.1 GET client with its own keep-alive pool. The HttpClient of Unity's Mono kept 2 connections per
    /// host whatever ServicePointManager said and leaked the ones the server closed (CLOSE_WAIT), until every request
    /// timed out; thousands of small chunks need many parallel requests. Runs on worker threads, so it keeps going
    /// while the app is in the background (UnityWebRequest stops with the player loop).
    /// </summary>
    public sealed class RawHttp : IDisposable
    {
        readonly SemaphoreSlim _slots;
        readonly ConcurrentDictionary<string, ConcurrentBag<Connection>> _idle = new ConcurrentDictionary<string, ConcurrentBag<Connection>>();
        public TimeSpan Timeout = TimeSpan.FromSeconds(30);
        public string UserAgent = "Ro3Android/1.0";

        public RawHttp(int maxConnections) => _slots = new SemaphoreSlim(maxConnections, maxConnections);

        public struct Response
        {
            public int Status;
            public byte[] Body;
        }

        public async Task<Response> GetAsync(Uri uri, CancellationToken ct)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string key = uri.Scheme + "://" + uri.Authority;
                var pool = _idle.GetOrAdd(key, _ => new ConcurrentBag<Connection>());
                // A pooled connection the server has closed fails on first use: then once more on a new one.
                while (pool.TryTake(out var reused))
                {
                    if (reused.Age > TimeSpan.FromSeconds(50)) { reused.Dispose(); continue; }
                    try { return await SendAsync(reused, uri, pool, ct).ConfigureAwait(false); }
                    catch (IOException) when (!ct.IsCancellationRequested) { break; }
                }
                var fresh = await Connection.OpenAsync(uri, Timeout, ct).ConfigureAwait(false);
                return await SendAsync(fresh, uri, pool, ct).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }

        async Task<Response> SendAsync(Connection c, Uri uri, ConcurrentBag<Connection> pool, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(Timeout);
                // Mono's socket streams ignore cancellation tokens: closing the socket is what ends a stuck read.
                using (timeout.Token.Register(c.Dispose))
                {
                    try
                    {
                        string request = "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Authority + "\r\nUser-Agent: " + UserAgent
                                         + "\r\nAccept-Encoding: identity\r\nConnection: keep-alive\r\n\r\n";
                        byte[] head = Encoding.ASCII.GetBytes(request);
                        await c.Stream.WriteAsync(head, 0, head.Length, timeout.Token).ConfigureAwait(false);
                        await c.Stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                        var response = await c.ReadResponseAsync(timeout.Token).ConfigureAwait(false);
                        if (c.KeepAlive) { c.Touch(); pool.Add(c); }
                        else c.Dispose();
                        return response;
                    }
                    catch (Exception e)
                    {
                        c.Dispose();
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        if (e is IOException) throw;
                        throw new IOException((timeout.IsCancellationRequested ? "timeout: " : "") + e.Message, e);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var pool in _idle.Values)
                while (pool.TryTake(out var c)) c.Dispose();
        }

        sealed class Connection : IDisposable
        {
            TcpClient _tcp;
            public Stream Stream;
            public bool KeepAlive;
            DateTime _used = DateTime.UtcNow;
            readonly byte[] _buffer = new byte[64 * 1024];
            int _start, _end;

            public TimeSpan Age => DateTime.UtcNow - _used;
            public void Touch() => _used = DateTime.UtcNow;

            public static async Task<Connection> OpenAsync(Uri uri, TimeSpan timeout, CancellationToken ct)
            {
                var c = new Connection { _tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 256 * 1024 } };
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(timeout);
                    using (limit.Token.Register(c.Dispose))
                    {
                        try
                        {
                            await c._tcp.ConnectAsync(uri.Host, uri.Port).ConfigureAwait(false);
                            c.Stream = c._tcp.GetStream();
                            if (uri.Scheme == "https")
                            {
                                var ssl = new SslStream(c.Stream, false);
                                await ssl.AuthenticateAsClientAsync(uri.Host).ConfigureAwait(false);
                                c.Stream = ssl;
                            }
                            return c;
                        }
                        catch (Exception e)
                        {
                            c.Dispose();
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            throw new IOException("connect " + uri.Authority + ": " + e.Message, e);
                        }
                    }
                }
            }

            async Task<bool> FillAsync(CancellationToken ct)
            {
                if (_start > 0 && _start == _end) _start = _end = 0;
                if (_end == _buffer.Length)
                {
                    if (_start == 0) throw new IOException("HTTP header too long");
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                int n = await Stream.ReadAsync(_buffer, _end, _buffer.Length - _end, ct).ConfigureAwait(false);
                if (n <= 0) return false;
                _end += n;
                return true;
            }

            async Task<string> ReadLineAsync(CancellationToken ct)
            {
                while (true)
                {
                    for (int i = _start; i + 1 < _end; i++)
                    {
                        if (_buffer[i] == '\r' && _buffer[i + 1] == '\n')
                        {
                            string line = Encoding.ASCII.GetString(_buffer, _start, i - _start);
                            _start = i + 2;
                            return line;
                        }
                    }
                    if (!await FillAsync(ct).ConfigureAwait(false)) throw new IOException("connection closed by the server");
                }
            }

            async Task ReadExactAsync(byte[] target, int offset, int count, CancellationToken ct)
            {
                int buffered = Math.Min(count, _end - _start);
                Buffer.BlockCopy(_buffer, _start, target, offset, buffered);
                _start += buffered;
                offset += buffered;
                count -= buffered;
                while (count > 0)
                {
                    int n = await Stream.ReadAsync(target, offset, count, ct).ConfigureAwait(false);
                    if (n <= 0) throw new IOException("connection closed mid-body");
                    offset += n;
                    count -= n;
                }
            }

            public async Task<Response> ReadResponseAsync(CancellationToken ct)
            {
                string status = await ReadLineAsync(ct).ConfigureAwait(false);
                var parts = status.Split(' ');
                if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || !int.TryParse(parts[1], out int code))
                    throw new IOException("bad HTTP status line: " + status);
                long length = -1;
                bool chunked = false;
                KeepAlive = parts[0] == "HTTP/1.1";
                string line;
                while ((line = await ReadLineAsync(ct).ConfigureAwait(false)).Length > 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = long.Parse(value, CultureInfo.InvariantCulture);
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) chunked = value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                    else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) KeepAlive = value.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0;
                }
                byte[] body;
                if (chunked) body = await ReadChunkedAsync(ct).ConfigureAwait(false);
                else if (length >= 0)
                {
                    if (length > 256L << 20) throw new IOException("HTTP body too large");
                    body = new byte[length];
                    await ReadExactAsync(body, 0, body.Length, ct).ConfigureAwait(false);
                }
                else
                {
                    // No length: the body runs to the end of the connection.
                    var all = new MemoryStream();
                    all.Write(_buffer, _start, _end - _start);
                    _start = _end = 0;
                    while (await FillAsync(ct).ConfigureAwait(false)) { all.Write(_buffer, 0, _end); _start = _end = 0; }
                    body = all.ToArray();
                    KeepAlive = false;
                }
                return new Response { Status = code, Body = body };
            }

            async Task<byte[]> ReadChunkedAsync(CancellationToken ct)
            {
                var all = new MemoryStream();
                while (true)
                {
                    string sizeLine = await ReadLineAsync(ct).ConfigureAwait(false);
                    int semi = sizeLine.IndexOf(';');
                    int size = int.Parse(semi >= 0 ? sizeLine.Substring(0, semi) : sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        while ((await ReadLineAsync(ct).ConfigureAwait(false)).Length > 0) { } // trailers
                        return all.ToArray();
                    }
                    var part = new byte[size];
                    await ReadExactAsync(part, 0, size, ct).ConfigureAwait(false);
                    all.Write(part, 0, size);
                    await ReadLineAsync(ct).ConfigureAwait(false);
                }
            }

            public void Dispose()
            {
                KeepAlive = false;
                try { Stream?.Dispose(); } catch (Exception) { }
                try { _tcp?.Close(); } catch (Exception) { }
            }
        }
    }
}
