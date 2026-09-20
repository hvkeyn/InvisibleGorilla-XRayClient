using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Services
{
    /// <summary>
    /// Manual SOCKS5 CONNECT. Android/.NET HttpClient ignores socks5:// WebProxy
    /// and hangs, so probes must dial the handshake themselves.
    /// </summary>
    public static class Socks5Http
    {
        public static bool TryGetEndpoint(IWebProxy proxy, out string host, out int port)
        {
            host = "";
            port = 0;
            Uri address = null;
            if (proxy is WebProxy web && web.Address != null)
                address = web.Address;
            address ??= proxy?.GetProxy(new Uri("https://ifconfig.co/"));
            if (address == null)
                return false;

            string scheme = (address.Scheme ?? "").ToLowerInvariant();
            if (scheme != "socks5" && scheme != "socks" && scheme != "socks5h")
                return false;

            host = address.Host;
            port = address.Port > 0 ? address.Port : 1080;
            return !string.IsNullOrWhiteSpace(host) && port > 0;
        }

        public static void Attach(SocketsHttpHandler handler, IWebProxy proxy)
        {
            if (handler == null || !TryGetEndpoint(proxy, out string host, out int port))
                return;

            TryGetCredentials(proxy, out string user, out string pass);
            handler.UseProxy = false;
            handler.Proxy = null;
            handler.ConnectCallback = (context, token) =>
                new ValueTask<Stream>(ConnectAsync(host, port, context.DnsEndPoint, user, pass, token));
        }

        public static async Task<Stream> ConnectAsync(string proxyHost, int proxyPort, DnsEndPoint destination, CancellationToken token)
        {
            return await ConnectAsync(proxyHost, proxyPort, destination, null, null, token).ConfigureAwait(false);
        }

        public static async Task<Stream> ConnectAsync(
            string proxyHost,
            int proxyPort,
            DnsEndPoint destination,
            string username,
            string password,
            CancellationToken token)
        {
            TcpClient client = new TcpClient { NoDelay = true };
            try
            {
                using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (!connectCts.IsCancellationRequested)
                    connectCts.CancelAfter(TimeSpan.FromSeconds(12));

                await client.ConnectAsync(proxyHost, proxyPort, connectCts.Token).ConfigureAwait(false);
                NetworkStream stream = new NetworkStream(client.Client, ownsSocket: true);
                await HandshakeAsync(stream, destination.Host, destination.Port, username, password, token).ConfigureAwait(false);
                return stream;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static async Task<string> GetStringAsync(string proxyHost, int proxyPort, string url, int timeoutMs)
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)));
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)),
                ConnectCallback = (context, token) => new ValueTask<Stream>(ConnectAsync(proxyHost, proxyPort, context.DnsEndPoint, null, null, token))
            };
            using HttpClient client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs))
            };
            return await client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        }

        public static string GetHttpsBody(string proxyHost, int proxyPort, string destHost, string path, int timeoutMs)
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(2000, timeoutMs)));
            TcpClient client = new TcpClient { NoDelay = true };
            using (cts.Token.Register(() => { try { client.Dispose(); } catch { } }))
            {
                try
                {
                    client.ConnectAsync(proxyHost, proxyPort, cts.Token).AsTask().GetAwaiter().GetResult();
                    using NetworkStream raw = new NetworkStream(client.Client, ownsSocket: false);
                    HandshakeAsync(raw, destHost, 443, null, null, cts.Token).GetAwaiter().GetResult();
                    using SslStream ssl = new SslStream(raw, leaveInnerStreamOpen: true, static (_, _, _, _) => true);
                    ssl.AuthenticateAsClient(destHost);
                    string request = "GET " + (string.IsNullOrWhiteSpace(path) ? "/" : path) +
                        " HTTP/1.1\r\nHost: " + destHost +
                        "\r\nUser-Agent: InvisibleGorilla-XRay\r\nConnection: close\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(request);
                    ssl.Write(bytes, 0, bytes.Length);
                    ssl.Flush();

                    StringBuilder response = new StringBuilder();
                    byte[] buffer = new byte[4096];
                    while (!cts.IsCancellationRequested)
                    {
                        int read = ssl.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                            break;
                        response.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        if (response.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0
                            && LooksComplete(response.ToString()))
                            break;
                    }

                    string text = response.ToString();
                    int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    return sep >= 0 ? text.Substring(sep + 4).Trim() : text.Trim();
                }
                catch (ObjectDisposedException)
                {
                    throw new TimeoutException("SOCKS HTTPS probe timed out");
                }
                finally
                {
                    try { client.Dispose(); } catch { }
                }
            }
        }

        private static bool LooksComplete(string response)
        {
            int sep = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (sep < 0)
                return false;
            string headers = response.Substring(0, sep);
            string body = response.Substring(sep + 4);
            const string marker = "Content-Length:";
            int idx = headers.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return body.Length > 0;
            int start = idx + marker.Length;
            int end = headers.IndexOf('\r', start);
            if (end < 0)
                end = headers.Length;
            if (!int.TryParse(headers.Substring(start, end - start).Trim(), out int length))
                return body.Length > 0;
            return body.Length >= length;
        }

        private static bool TryGetCredentials(IWebProxy proxy, out string username, out string password)
        {
            username = "";
            password = "";
            if (proxy == null)
                return false;

            if (proxy.Credentials is NetworkCredential credential
                && !string.IsNullOrWhiteSpace(credential.UserName)
                && !string.IsNullOrWhiteSpace(credential.Password))
            {
                username = credential.UserName;
                password = credential.Password;
                return true;
            }

            if (proxy is WebProxy web && web.Address != null && !string.IsNullOrEmpty(web.Address.UserInfo))
            {
                string[] parts = web.Address.UserInfo.Split(':', 2);
                if (parts.Length == 2)
                {
                    username = Uri.UnescapeDataString(parts[0]);
                    password = Uri.UnescapeDataString(parts[1]);
                    return username.Length > 0 && password.Length > 0;
                }
            }

            return false;
        }

        private static async Task HandshakeAsync(
            Stream stream,
            string destHost,
            int destPort,
            string username,
            string password,
            CancellationToken token)
        {
            bool offerUserPass = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
            byte[] hello = offerUserPass
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(hello, token).ConfigureAwait(false);
            byte[] method = await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
            if (method[0] != 0x05)
                throw new IOException($"SOCKS5 auth rejected ({method[0]:X2} {method[1]:X2}) ({destHost}:{destPort})");

            if (method[1] == 0x02)
            {
                if (!offerUserPass)
                    throw new IOException($"SOCKS5 auth rejected ({method[0]:X2} {method[1]:X2}) ({destHost}:{destPort})");

                byte[] userBytes = Encoding.UTF8.GetBytes(username);
                byte[] passBytes = Encoding.UTF8.GetBytes(password);
                if (userBytes.Length > 255 || passBytes.Length > 255)
                    throw new IOException("SOCKS5 credentials are too long");

                byte[] auth = new byte[3 + userBytes.Length + passBytes.Length];
                auth[0] = 0x01;
                auth[1] = (byte)userBytes.Length;
                Buffer.BlockCopy(userBytes, 0, auth, 2, userBytes.Length);
                auth[2 + userBytes.Length] = (byte)passBytes.Length;
                Buffer.BlockCopy(passBytes, 0, auth, 3 + userBytes.Length, passBytes.Length);
                await stream.WriteAsync(auth, token).ConfigureAwait(false);
                byte[] authReply = await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
                if (authReply[1] != 0x00)
                    throw new IOException($"SOCKS5 username/password rejected ({destHost}:{destPort})");
            }
            else if (method[1] != 0x00)
            {
                throw new IOException($"SOCKS5 auth rejected ({method[0]:X2} {method[1]:X2}) ({destHost}:{destPort})");
            }

            byte[] hostBytes = Encoding.ASCII.GetBytes(destHost ?? "");
            if (hostBytes.Length == 0 || hostBytes.Length > 255)
                throw new IOException("SOCKS5 destination host is invalid");

            byte[] request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
            request[5 + hostBytes.Length] = (byte)(destPort >> 8);
            request[6 + hostBytes.Length] = (byte)(destPort & 0xFF);
            await stream.WriteAsync(request, token).ConfigureAwait(false);

            byte[] replyHead = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
            if (replyHead[0] != 0x05 || replyHead[1] != 0x00)
                throw new IOException($"SOCKS5 connect failed ({replyHead[1]})");

            int remaining = replyHead[3] switch
            {
                0x01 => 4 + 2,
                0x03 => 1 + (await ReadExactAsync(stream, 1, token).ConfigureAwait(false))[0] + 2,
                0x04 => 16 + 2,
                _ => throw new IOException($"SOCKS5 address type {replyHead[3]}")
            };
            if (replyHead[3] != 0x03)
                await ReadExactAsync(stream, remaining, token).ConfigureAwait(false);
            else
                await ReadExactAsync(stream, remaining - 1, token).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token).ConfigureAwait(false);
                if (read <= 0)
                    throw new EndOfStreamException("SOCKS5 stream closed");
                offset += read;
            }
            return buffer;
        }
    }
}
