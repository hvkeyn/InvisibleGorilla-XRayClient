namespace InvisibleGorillaXRay.Android.Handlers.Tunnels
{
    using InvisibleGorillaXRay.Core;
    using InvisibleGorillaXRay.Handlers.Tunnels;
    using InvisibleGorillaXRay.Models;

    public sealed class AndroidTunnel : ITunnel
    {
        private const string UnsupportedMessage =
            "Android TUN mode requires a VpnService-backed mobile tunnel bridge. " +
            "The APK groundwork is now present, but the native mobile tunnel runtime still needs to be bundled.";

        public Status Enable(string ip, int port, string address, string server, string dns)
        {
            DiagnosticLog.Write(
                "AndroidTunnel",
                $"TUN mode requested for proxy={ip}:{port}, address={address}, server={server}, dns={dns}");

            return new Status(
                code: Code.ERROR,
                subCode: SubCode.CANT_TUNNEL,
                content: UnsupportedMessage
            );
        }

        public void Disable()
        {
            DiagnosticLog.Write("AndroidTunnel", "Disable requested");
        }

        public void Cancel()
        {
            DiagnosticLog.Write("AndroidTunnel", "Cancel requested");
        }
    }
}
