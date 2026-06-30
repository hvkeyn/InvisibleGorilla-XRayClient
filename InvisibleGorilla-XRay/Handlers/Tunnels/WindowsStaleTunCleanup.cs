using System;
using System.Net.Sockets;
using System.Text;

namespace InvisibleGorillaXRay.Handlers.Tunnels
{
    using Core;

    internal static class WindowsStaleTunCleanup
    {
        private const int DefaultTunServicePort = 10802;

        public static void TryDisableStaleTunnel(int port = DefaultTunServicePort)
        {
            try
            {
                using TcpClient client = new TcpClient();
                IAsyncResult connect = client.BeginConnect("127.0.0.1", port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(350)))
                    return;

                client.EndConnect(connect);
                using NetworkStream stream = client.GetStream();
                byte[] payload = Encoding.ASCII.GetBytes("-command=disable<EOF>");
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                DiagnosticLog.Write("WindowsStaleTunCleanup", $"Sent stale TUN disable command to 127.0.0.1:{port}");
            }
            catch (Exception ex)
            {
                // No stale service listening is the normal case.
                DiagnosticLog.Write("WindowsStaleTunCleanup", $"No stale TUN cleanup needed: {ex.Message}");
            }
        }
    }
}
