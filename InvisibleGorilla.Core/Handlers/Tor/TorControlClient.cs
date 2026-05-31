using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    /// <summary>
    /// Minimal Tor control-port client. Authenticates with the cookie file and polls the
    /// bootstrap phase. Implements just enough of the control protocol for status reporting.
    /// </summary>
    public sealed class TorControlClient : IDisposable
    {
        private TcpClient client;
        private NetworkStream stream;
        private StreamReader reader;
        private StreamWriter writer;

        public bool Connect(int controlPort, string cookieFilePath, int connectTimeoutMs = 5000)
        {
            try
            {
                client = new TcpClient();
                IAsyncResult ar = client.BeginConnect("127.0.0.1", controlPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(connectTimeoutMs))
                    return false;
                client.EndConnect(ar);

                stream = client.GetStream();
                stream.ReadTimeout = 8000;
                stream.WriteTimeout = 8000;
                reader = new StreamReader(stream, Encoding.ASCII);
                writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

                return Authenticate(cookieFilePath);
            }
            catch
            {
                Dispose();
                return false;
            }
        }

        private bool Authenticate(string cookieFilePath)
        {
            string auth;
            if (!string.IsNullOrEmpty(cookieFilePath) && File.Exists(cookieFilePath))
            {
                byte[] cookie = File.ReadAllBytes(cookieFilePath);
                auth = $"AUTHENTICATE {ToHex(cookie)}";
            }
            else
            {
                auth = "AUTHENTICATE";
            }

            writer.WriteLine(auth);
            string reply = reader.ReadLine();
            return reply != null && reply.StartsWith("250");
        }

        /// <summary>
        /// Returns the bootstrap percentage [0..100], or -1 on error.
        /// </summary>
        public int GetBootstrapPercent(out string summary)
        {
            summary = string.Empty;
            try
            {
                writer.WriteLine("GETINFO status/bootstrap-phase");
                string line;
                int percent = -1;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains("PROGRESS="))
                    {
                        percent = ParseInt(line, "PROGRESS=");
                        summary = ExtractQuoted(line, "SUMMARY=");
                    }
                    if (line.StartsWith("250 OK") || line.StartsWith("250-OK") || line == ".")
                        break;
                    if (line.StartsWith("250 ") && !line.Contains("status/bootstrap-phase"))
                        break;
                    if (line.StartsWith("5"))
                        break;
                }
                return percent;
            }
            catch
            {
                return -1;
            }
        }

        public void SignalShutdown()
        {
            try
            {
                writer?.WriteLine("SIGNAL HALT");
                reader?.ReadLine();
            }
            catch
            {
            }
        }

        private static int ParseInt(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return -1;
            idx += key.Length;
            int end = idx;
            while (end < line.Length && char.IsDigit(line[end]))
                end++;
            return int.TryParse(line.Substring(idx, end - idx), out int value) ? value : -1;
        }

        private static string ExtractQuoted(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return string.Empty;
            idx += key.Length;
            if (idx >= line.Length)
                return string.Empty;
            if (line[idx] == '"')
            {
                int end = line.IndexOf('"', idx + 1);
                if (end > idx)
                    return line.Substring(idx + 1, end - idx - 1);
            }
            return string.Empty;
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public void Dispose()
        {
            try { reader?.Dispose(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { client?.Close(); } catch { }
            reader = null;
            writer = null;
            stream = null;
            client = null;
        }
    }
}
