using System;
using System.Collections.Generic;
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

        /// <summary>
        /// First hop of each built circuit. With bridges that hop is the bridge,
        /// returned as a hex fingerprint and nothing else.
        /// </summary>
        public List<string> ReadFirstHopFingerprints()
        {
            var found = new List<string>();
            try
            {
                writer.WriteLine("GETINFO circuit-status");
                bool inBlock = false;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == ".")
                    {
                        inBlock = false;
                        continue;
                    }
                    if (line.StartsWith("250+") && line.Contains("circuit-status"))
                    {
                        inBlock = true;
                        continue;
                    }
                    if (line.StartsWith("250 OK") || (line.StartsWith("250 ") && !line.StartsWith("250-")))
                        break;
                    if (line.Length > 0 && line[0] == '5')
                        break;

                    string body = line.StartsWith("250-") ? line.Substring(4) : line;
                    if (!inBlock && !line.StartsWith("250-"))
                        continue;
                    if (body.IndexOf(" BUILT ", StringComparison.Ordinal) < 0)
                        continue;

                    int dollar = body.IndexOf('$');
                    if (dollar < 0)
                        continue;
                    int end = dollar + 1;
                    while (end < body.Length && Uri.IsHexDigit(body[end]))
                        end++;
                    if (end - dollar > 1)
                        found.Add(body.Substring(dollar + 1, end - dollar - 1));
                }
            }
            catch
            {
            }
            return found;
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

        public bool SignalNewNym(out string reply) => Command("SIGNAL NEWNYM", out reply);

        public bool ResetConf(out string reply, params string[] keywords)
        {
            reply = string.Empty;
            if (keywords == null || keywords.Length == 0)
                return false;
            return Command("RESETCONF " + string.Join(" ", keywords), out reply);
        }

        public bool SetConf(out string reply, params string[] assignments)
        {
            reply = string.Empty;
            if (assignments == null || assignments.Length == 0)
                return false;
            return Command("SETCONF " + string.Join(" ", assignments), out reply);
        }

        private bool Command(string command, out string reply)
        {
            reply = string.Empty;
            try
            {
                writer.WriteLine(command);
                var sb = new StringBuilder();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(line);
                    if (line == "250" || line.StartsWith("250 "))
                    {
                        reply = sb.ToString();
                        return true;
                    }
                    if (line.Length > 0 && (line[0] == '4' || line[0] == '5'))
                    {
                        reply = line;
                        return false;
                    }
                    if (!line.StartsWith("250"))
                    {
                        reply = line;
                        return false;
                    }
                }

                reply = "no reply";
                return false;
            }
            catch (Exception ex)
            {
                reply = ex.GetType().Name;
                return false;
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
