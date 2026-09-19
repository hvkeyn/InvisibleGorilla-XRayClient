using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Handlers.OpenFlux
{
    using InvisibleGorillaXRay.Core;

    /// <summary>
    /// Local HTTP/CONNECT proxy in front of OpenFlux SOCKS5.
    /// Chrome/WinINET speak HTTP proxy reliably; they often send SOCKS4
    /// when the system proxy is set as socks=host:port, which OpenFlux rejects.
    /// </summary>
    public sealed class OpenFluxHttpBridge : IDisposable
    {
        private TcpListener listener;
        private CancellationTokenSource cts;
        private int socksPort;

        public int ListenPort { get; private set; }

        public int Start(int openFluxSocksPort)
        {
            Stop();
            socksPort = openFluxSocksPort;
            cts = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ListenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            CancellationToken token = cts.Token;
            Task.Run(() => AcceptLoop(token), token);
            DiagnosticLog.Write("OpenFlux.HttpBridge", $"listening 127.0.0.1:{ListenPort} -> socks5 127.0.0.1:{socksPort}");
            return ListenPort;
        }

        public void Stop()
        {
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            listener = null;
            cts = null;
            ListenPort = 0;
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    if (token.IsCancellationRequested)
                        return;
                    await Task.Delay(50, token).ConfigureAwait(false);
                    continue;
                }

                _ = Task.Run(() => HandleClient(client, token), token);
            }
        }

        private async Task HandleClient(TcpClient browser, CancellationToken token)
        {
            using (browser)
            {
                browser.NoDelay = true;
                NetworkStream browserStream = browser.GetStream();
                browserStream.ReadTimeout = 20000;
                browserStream.WriteTimeout = 20000;

                string first = await ReadLineAsync(browserStream, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(first))
                    return;

                string[] parts = first.Split(' ');
                if (parts.Length < 2)
                    return;

                string method = parts[0].ToUpperInvariant();
                string target = parts[1];

                if (method == "CONNECT")
                {
                    await DrainHeadersAsync(browserStream, token).ConfigureAwait(false);
                    if (!TrySplitHostPort(target, 443, out string host, out int port))
                    {
                        await WriteAscii(browserStream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                        return;
                    }

                    using TcpClient socks = await ConnectSocksAsync(host, port, token).ConfigureAwait(false);
                    if (socks == null)
                    {
                        await WriteAscii(browserStream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                        return;
                    }

                    await WriteAscii(browserStream, "HTTP/1.1 200 Connection Established\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    await Pump(browserStream, socks.GetStream(), token).ConfigureAwait(false);
                    return;
                }

                string leftover = first + "\r\n";
                leftover += await ReadHeadersAsync(browserStream, token).ConfigureAwait(false);
                if (!TryHostFromAbsoluteOrHeader(target, leftover, out string httpHost, out int httpPort, out string rewrite))
                {
                    await WriteAscii(browserStream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    return;
                }

                using TcpClient origin = await ConnectSocksAsync(httpHost, httpPort, token).ConfigureAwait(false);
                if (origin == null)
                {
                    await WriteAscii(browserStream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    return;
                }

                byte[] head = Encoding.ASCII.GetBytes(rewrite);
                await origin.GetStream().WriteAsync(head, token).ConfigureAwait(false);
                await Pump(browserStream, origin.GetStream(), token).ConfigureAwait(false);
            }
        }

        private async Task<TcpClient> ConnectSocksAsync(string host, int port, CancellationToken token)
        {
            TcpClient socks = new TcpClient { NoDelay = true };
            try
            {
                await socks.ConnectAsync(IPAddress.Loopback, socksPort, token).ConfigureAwait(false);
                NetworkStream stream = socks.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, token).ConfigureAwait(false);
                byte[] greet = new byte[2];
                if (await ReadExact(stream, greet, token).ConfigureAwait(false) != 2 || greet[0] != 5 || greet[1] != 0)
                    throw new IOException("socks greet");

                byte[] name = Encoding.ASCII.GetBytes(host);
                byte[] req = new byte[7 + name.Length];
                req[0] = 5;
                req[1] = 1;
                req[2] = 0;
                req[3] = 3;
                req[4] = (byte)name.Length;
                Buffer.BlockCopy(name, 0, req, 5, name.Length);
                req[5 + name.Length] = (byte)(port >> 8);
                req[6 + name.Length] = (byte)(port & 0xff);
                await stream.WriteAsync(req, token).ConfigureAwait(false);

                byte[] reply = new byte[4];
                if (await ReadExact(stream, reply, token).ConfigureAwait(false) != 4 || reply[1] != 0)
                    throw new IOException("socks connect");

                int skip = reply[3] == 1 ? 6 : reply[3] == 4 ? 18 : 0;
                if (reply[3] == 3)
                {
                    byte[] len = new byte[1];
                    await ReadExact(stream, len, token).ConfigureAwait(false);
                    skip = len[0] + 2;
                }
                if (skip > 0)
                {
                    byte[] rest = new byte[skip];
                    await ReadExact(stream, rest, token).ConfigureAwait(false);
                }

                return socks;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("OpenFlux.HttpBridge", "socks connect failed: " + ex.Message);
                try { socks.Dispose(); } catch { }
                return null;
            }
        }

        private static async Task Pump(Stream a, Stream b, CancellationToken token)
        {
            Task a2b = a.CopyToAsync(b, token);
            Task b2a = b.CopyToAsync(a, token);
            try
            {
                await Task.WhenAny(a2b, b2a).ConfigureAwait(false);
            }
            catch { }
        }

        private static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
        {
            host = "";
            port = defaultPort;
            if (string.IsNullOrWhiteSpace(target))
                return false;
            target = target.Trim();
            int colon = target.LastIndexOf(':');
            if (colon > 0 && int.TryParse(target.AsSpan(colon + 1), out int parsed))
            {
                host = target.Substring(0, colon).Trim('[', ']');
                port = parsed;
                return host.Length > 0;
            }
            host = target.Trim('[', ']');
            return host.Length > 0;
        }

        private static bool TryHostFromAbsoluteOrHeader(string target, string request, out string host, out int port, out string rewrite)
        {
            host = "";
            port = 80;
            rewrite = request;
            if (Uri.TryCreate(target, UriKind.Absolute, out Uri uri) && !string.IsNullOrEmpty(uri.Host))
            {
                host = uri.Host;
                port = uri.IsDefaultPort ? 80 : uri.Port;
                string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                int nl = request.IndexOf('\n');
                string rest = nl >= 0 ? request.Substring(nl + 1) : "";
                string method = request.Split(' ')[0];
                rewrite = method + " " + path + " HTTP/1.1\r\n" + rest;
                return true;
            }

            foreach (string line in request.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    return TrySplitHostPort(trimmed.Substring(5).Trim(), 80, out host, out port);
            }
            return false;
        }

        private static async Task DrainHeadersAsync(Stream stream, CancellationToken token)
        {
            await ReadHeadersAsync(stream, token).ConfigureAwait(false);
        }

        private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                string line = await ReadLineAsync(stream, token).ConfigureAwait(false);
                if (line == null)
                    break;
                sb.Append(line).Append("\r\n");
                if (line.Length == 0)
                    break;
            }
            return sb.ToString();
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
        {
            StringBuilder sb = new StringBuilder();
            byte[] one = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
                if (n <= 0)
                    return sb.Length == 0 ? null : sb.ToString();
                if (one[0] == (byte)'\n')
                    break;
                if (one[0] != (byte)'\r')
                    sb.Append((char)one[0]);
                if (sb.Length > 8192)
                    return sb.ToString();
            }
            return sb.ToString();
        }

        private static async Task<int> ReadExact(Stream stream, byte[] buffer, CancellationToken token)
        {
            int off = 0;
            while (off < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(off, buffer.Length - off), token).ConfigureAwait(false);
                if (n <= 0)
                    return off;
                off += n;
            }
            return off;
        }

        private static Task WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            return stream.WriteAsync(bytes).AsTask();
        }
    }
}
