using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    using Values;

    /// <summary>
    /// Local meek_lite SOCKS tunnel, the same path Orbot uses to reach Moat when the
    /// circumvention host is blocked. Bridge arguments travel in the SOCKS username.
    /// </summary>
    public sealed class MeekFrontSession : IDisposable
    {
        public static readonly string[] Fronts =
        {
            "url=https://1723079976.rsc.cdn77.org;front=www.phpmyadmin.net",
            "url=https://bespoke-strudel-c243cc.netlify.app/;front=vuejs.org"
        };

        private readonly Process process;
        private int port;
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);

        private MeekFrontSession(Process process)
        {
            this.process = process;
        }

        public static MeekFrontSession TryStart()
        {
            string exe = Path.PLUGGABLE_TRANSPORT_EXE;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return null;

            string stateDir = System.IO.Path.Combine(Directory.TOR_DATA, "meek-state");
            try
            {
                System.IO.Directory.CreateDirectory(stateDir);
            }
            catch
            {
                return null;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Directory.TOR_DATA,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["TOR_PT_MANAGED_TRANSPORT_VER"] = "1";
            startInfo.EnvironmentVariables["TOR_PT_CLIENT_TRANSPORTS"] = "meek_lite";
            startInfo.EnvironmentVariables["TOR_PT_STATE_LOCATION"] = stateDir;
            startInfo.EnvironmentVariables["TOR_PT_EXIT_ON_STDIN_CLOSE"] = "1";

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var session = new MeekFrontSession(process);
            process.OutputDataReceived += session.OnOutput;
            process.ErrorDataReceived += session.OnError;

            try
            {
                if (!process.Start())
                    return null;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                session.Dispose();
                return null;
            }

            if (!session.ready.Wait(8000) || session.port <= 0 || process.HasExited)
            {
                session.Dispose();
                return null;
            }

            return session;
        }

        public Stream Open(string frontUser, string host, int port)
        {
            var tcp = new TcpClient();
            IAsyncResult ar = tcp.BeginConnect("127.0.0.1", this.port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(8000))
            {
                tcp.Dispose();
                throw new IOException("Meek tunnel did not accept a connection.");
            }
            tcp.EndConnect(ar);

            NetworkStream net = tcp.GetStream();
            net.ReadTimeout = 20000;
            net.WriteTimeout = 20000;
            try
            {
                SocksConnect(net, frontUser, host, port);
                return new OwnedTcpStream(tcp, net);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            Match match = Regex.Match(e.Data, @"CMETHOD meek(?:_lite)? socks5 127\.0\.0\.1:(\d+)");
            if (!match.Success)
                return;

            if (int.TryParse(match.Groups[1].Value, out int parsed) && parsed > 0)
            {
                SetPort(parsed);
                ready.Set();
            }
        }

        private void SetPort(int value)
        {
            // Assigned from the stdout thread before any connection is opened.
            System.Threading.Interlocked.Exchange(ref port, value);
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
        }

        private static void SocksConnect(NetworkStream net, string username, string host, int port)
        {
            byte[] user = Encoding.ASCII.GetBytes(username ?? string.Empty);
            net.Write(new byte[] { 5, 1, 2 }, 0, 3);
            int version = net.ReadByte();
            int method = net.ReadByte();
            if (version != 5 || method != 2)
                throw new IOException("Meek SOCKS rejected username auth.");

            byte[] auth = new byte[3 + user.Length + 1];
            auth[0] = 1;
            auth[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, auth, 2, user.Length);
            auth[2 + user.Length] = 1;
            auth[3 + user.Length] = 0;
            net.Write(auth, 0, auth.Length);
            int authVer = net.ReadByte();
            int authStatus = net.ReadByte();
            if (authVer != 1 || authStatus != 0)
                throw new IOException("Meek SOCKS auth failed.");

            byte[] hostBytes = Encoding.ASCII.GetBytes(host);
            byte[] request = new byte[7 + hostBytes.Length];
            request[0] = 5;
            request[1] = 1;
            request[2] = 0;
            request[3] = 3;
            request[4] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
            request[5 + hostBytes.Length] = (byte)(port >> 8);
            request[6 + hostBytes.Length] = (byte)(port & 0xff);
            net.Write(request, 0, request.Length);

            int replyVersion = net.ReadByte();
            int replyCode = net.ReadByte();
            net.ReadByte();
            int addressType = net.ReadByte();
            if (replyVersion != 5 || replyCode != 0)
                throw new IOException("Meek SOCKS connect failed (" + replyCode + ").");

            if (addressType == 1)
                ReadExact(net, 6);
            else if (addressType == 3)
                ReadExact(net, net.ReadByte() + 2);
            else if (addressType == 4)
                ReadExact(net, 18);
        }

        private static void ReadExact(NetworkStream net, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = net.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new IOException("Meek SOCKS reply ended early.");
                read += n;
            }
        }

        public void Dispose()
        {
            try
            {
                process.StandardInput?.Close();
            }
            catch
            {
            }

            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }

            try { process?.Dispose(); } catch { }
            ready.Dispose();
        }

        private sealed class OwnedTcpStream : Stream
        {
            private readonly TcpClient client;
            private readonly NetworkStream inner;

            public OwnedTcpStream(TcpClient client, NetworkStream inner)
            {
                this.client = client;
                this.inner = inner;
            }

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { inner.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }
    }
}
