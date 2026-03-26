using Android.App;
using Android.Content;
using Android.Net;

namespace InvisibleGorillaXRay.Android.Services
{
    [Service(
        Name = "io.invisiblegorilla.xray.AndroidVpnService",
        Enabled = true,
        Exported = true,
        Permission = "android.permission.BIND_VPN_SERVICE")]
    [IntentFilter(new[] { "android.net.VpnService" })]
    public class AndroidVpnService : VpnService
    {
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            InvisibleGorillaXRay.Core.DiagnosticLog.Write("AndroidVpnService", "Foreground VPN service start requested");
            return StartCommandResult.NotSticky;
        }

        public override void OnDestroy()
        {
            InvisibleGorillaXRay.Core.DiagnosticLog.Write("AndroidVpnService", "Foreground VPN service destroyed");
            base.OnDestroy();
        }
    }
}
