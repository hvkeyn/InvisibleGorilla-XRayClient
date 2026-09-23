using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Handlers.OpenFlux;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Android.Services
{
    [Service(
        Name = "io.invisiblegorilla.xray.AndroidVpnService",
        Enabled = true,
        Exported = true,
        Permission = "android.permission.BIND_VPN_SERVICE",
        ForegroundServiceType = ForegroundService.TypeSpecialUse)]
    [IntentFilter(new[] { "android.net.VpnService" })]
    [MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "device_wide_vpn_tunnel_routing")]
    public class AndroidVpnService : VpnService
    {
        private const string ActionStart = "io.invisiblegorilla.xray.action.START_VPN";
        private const string ActionStop = "io.invisiblegorilla.xray.action.STOP_VPN";
        private const string ExtraProxyPort = "proxy_port";
        private const string ExtraHttpProxyPort = "http_proxy_port";
        private const string ExtraLimitMux = "limit_mux";
        private const string ExtraProxyUsername = "proxy_username";
        private const string ExtraProxyPassword = "proxy_password";
        private const string ExtraUdpEnabled = "udp_enabled";
        private const string ExtraEnableIpv6 = "enable_ipv6";
        private const string ExtraTunAddress = "tun_address";
        private const string ExtraDns = "dns";
        private const string ExtraBypassServer = "bypass_server";
        private const string ExtraSessionName = "session_name";
        private const string ExtraAppRulesMode = "app_rules_mode";
        private const string ExtraAppPackages = "app_packages";
        private const int DefaultMtu = 1500;
        private const string DefaultIpv6Address = "fdfe:dcba:9876::1";
        private const int DefaultIpv6PrefixLength = 126;
        private static readonly object SyncRoot = new();
        private static AndroidVpnService? current;
        private static int vpnGeneration;

        private static int protectAllowed;
        private static ParcelFileDescriptor? activeTun;

        private Timer? healthTimer;
        private int healthGeneration;
        private int healthMisses;
        private long tunHealthySinceMs;

        internal static Intent CreateStartIntent(Context context, AndroidVpnStartOptions options)
        {
            Intent intent = new Intent(context, typeof(AndroidVpnService));
            intent.SetAction(ActionStart);
            intent.PutExtra(ExtraProxyPort, options.ProxyPort);
            intent.PutExtra(ExtraHttpProxyPort, options.HttpProxyPort);
            intent.PutExtra(ExtraLimitMux, options.LimitMux);
            intent.PutExtra(ExtraProxyUsername, options.ProxyUsername);
            intent.PutExtra(ExtraProxyPassword, options.ProxyPassword);
            intent.PutExtra(ExtraUdpEnabled, options.UdpEnabled);
            intent.PutExtra(ExtraEnableIpv6, options.EnableIpv6);
            intent.PutExtra(ExtraTunAddress, options.TunAddress);
            intent.PutExtra(ExtraDns, options.Dns);
            intent.PutExtra(ExtraBypassServer, options.BypassServer ?? string.Empty);
            intent.PutExtra(ExtraSessionName, options.SessionName);
            intent.PutExtra(ExtraAppRulesMode, (int)options.AppRulesMode);
            if (options.AppPackages.Count > 0)
                intent.PutStringArrayListExtra(ExtraAppPackages, new List<string>(options.AppPackages));
            return intent;
        }

        internal static Intent CreateStopIntent(Context context)
        {
            Intent intent = new Intent(context, typeof(AndroidVpnService));
            intent.SetAction(ActionStop);
            return intent;
        }

        private static bool IsMainLooper()
        {
            Looper? main = Looper.MainLooper;
            return main != null && Looper.MyLooper() == main;
        }

        internal static void StopFromClient(string reason)
        {
            AndroidVpnService? service = current;
            if (service == null)
            {
                AndroidVpnServiceController.NotifyStopped(reason);
                return;
            }

            int generation = Volatile.Read(ref vpnGeneration);
            void RunStop()
            {
                try { service.StopVpn(reason, generation); }
                catch (Exception ex) { DiagnosticLog.WriteException("AndroidVpnService.StopFromClient", ex); }
            }

            if (IsMainLooper())
                _ = Task.Run(RunStop);
            else
                RunStop();
        }

        internal static bool TryRefreshForegroundNotification()
        {
            AndroidVpnService? service = current;
            if (service == null)
                return false;

            try
            {
                Notification notification = AndroidConnectionNotificationManager.BuildForegroundNotification(service);
                service.StartForegroundCompat(notification);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.RefreshNotification", ex);
                return false;
            }
        }

        public override void OnCreate()
        {
            base.OnCreate();
            current = this;
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            string? action = intent?.Action;
            DiagnosticLog.Write("AndroidVpnService", $"OnStartCommand action={action ?? "<null>"}");

            if (string.Equals(action, ActionStop, StringComparison.Ordinal))
            {
                int stopGeneration = Volatile.Read(ref vpnGeneration);
                _ = Task.Run(() =>
                {
                    try { StopVpn("Stop requested", stopGeneration); }
                    catch (Exception ex) { DiagnosticLog.WriteException("AndroidVpnService.StopQueued", ex); }
                });
                return StartCommandResult.NotSticky;
            }

            if (!string.Equals(action, ActionStart, StringComparison.Ordinal))
                return StartCommandResult.NotSticky;

            try
            {
                try { StopVpn("Replaced by new start", endSession: false); }
                catch (Exception ex) { DiagnosticLog.WriteException("AndroidVpnService.StopBeforeStart", ex); }

                int startGeneration = Interlocked.Increment(ref vpnGeneration);
                StartForegroundFast();
                Intent capturedIntent = intent!;
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref vpnGeneration) != startGeneration)
                            return;

                        StartVpn(capturedIntent);
                        if (Volatile.Read(ref vpnGeneration) != startGeneration)
                            return;

                        AndroidVpnServiceController.NotifyStarted();
                        AndroidConnectionNotificationManager.MarkRunning();
                        TryRefreshForegroundNotification();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException("AndroidVpnService.StartQueued", ex);
                        AndroidVpnServiceController.NotifyStartFailed(ex.Message);
                        try { StopVpn(ex.Message, startGeneration); } catch { }
                    }
                });
                return StartCommandResult.Sticky;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.Start", ex);
                AndroidVpnServiceController.NotifyStartFailed(ex.Message);
                _ = Task.Run(() =>
                {
                    try { StopVpn(ex.Message); } catch { }
                });
                return StartCommandResult.NotSticky;
            }
        }

        public override void OnDestroy()
        {
            DiagnosticLog.Write("AndroidVpnService", "Foreground VPN service destroyed");
            if (ReferenceEquals(current, this))
                current = null;
            _ = Task.Run(() =>
            {
                try { StopVpn("Android VPN service destroyed"); }
                catch (Exception ex) { DiagnosticLog.WriteException("AndroidVpnService.OnDestroy", ex); }
            });
            base.OnDestroy();
        }

        private void StartVpn(Intent intent)
        {
            if (Prepare(this) != null)
                throw new InvalidOperationException("Android VPN permission has not been granted.");

            int proxyPort = intent.GetIntExtra(ExtraProxyPort, 0);
            int httpProxyPort = intent.GetIntExtra(ExtraHttpProxyPort, 0);
            bool limitMux = intent.GetBooleanExtra(ExtraLimitMux, httpProxyPort > 0);
            if (proxyPort <= 0)
                throw new InvalidOperationException("Android VPN proxy port is missing.");

            LocalProxyCredentials localProxyCredentials = new(
                username: intent.GetStringExtra(ExtraProxyUsername) ?? string.Empty,
                password: intent.GetStringExtra(ExtraProxyPassword) ?? string.Empty);
            if (!localProxyCredentials.HasValue)
            {
                DiagnosticLog.Write("AndroidVpnService", "Starting VPN without local SOCKS credentials (OpenFlux/no-auth listener)");
            }

            bool udpEnabled = intent.GetBooleanExtra(ExtraUdpEnabled, true);
            bool enableIpv6 = intent.GetBooleanExtra(ExtraEnableIpv6, true);
            string tunAddress = intent.GetStringExtra(ExtraTunAddress)?.Trim() ?? "10.0.236.10";
            string dns = intent.GetStringExtra(ExtraDns)?.Trim() ?? "8.8.8.8";
            string bypassServer = intent.GetStringExtra(ExtraBypassServer)?.Trim() ?? string.Empty;
            string sessionName = intent.GetStringExtra(ExtraSessionName)?.Trim() ?? "Invisible Gorilla XRay";
            AppRulesMode appRulesMode = NormalizeAppRulesMode(intent.GetIntExtra(ExtraAppRulesMode, (int)AppRulesMode.ALL_APPS));
            string[] appPackages = intent.GetStringArrayListExtra(ExtraAppPackages)?
                .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>();

            lock (SyncRoot)
            {
                ResetVpnCore("Restarting Android VPN");
            }

            Builder builder = new Builder(this)
                .SetSession(sessionName)
                .SetMtu(DefaultMtu)
                .AddAddress(tunAddress, 32)
                .AddRoute("0.0.0.0", 0);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                builder.SetBlocking(true);
                try { builder.AllowFamily(2); } catch { }
                if (httpProxyPort > 0)
                {
                    try
                    {
                        builder.SetHttpProxy(ProxyInfo.BuildDirectProxy(tunAddress, httpProxyPort));
                        DiagnosticLog.Write("AndroidVpnService", $"VPN HTTP proxy {tunAddress}:{httpProxyPort}");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException("AndroidVpnService.SetHttpProxy", ex);
                    }
                }
            }

            foreach (string dnsServer in SplitDnsServers(dns))
                builder.AddDnsServer(dnsServer);

            ExcludeServerFromTunnel(builder, bypassServer);

            if (enableIpv6)
                TryEnableIpv6(builder);
            else
                DiagnosticLog.Write("AndroidVpnService", "IPv6 TUN routes skipped (OpenFlux / IPv4-only)");

            bool ownProcessExcluded = ApplyApplicationRules(builder, appRulesMode, appPackages);

            DiagnosticLog.Write(
                "AndroidVpnService",
                $"Calling Builder.Establish() (mtu={DefaultMtu}, address={tunAddress}, mode={appRulesMode}, packages={appPackages.Length})...");
            // Must stay off the Android UI thread. Posting Establish() to the main looper
            // caused ANR when the user left the app (FocusEvent hasFocus=false waited 10s)
            // — that is the "crashed while opening 2ip.io" report.
            ParcelFileDescriptor? tunInterface = builder.Establish();
            if (tunInterface == null)
            {
                Thread.Sleep(400);
                tunInterface = builder.Establish();
            }
            if (tunInterface == null)
                throw new InvalidOperationException("Android VPN interface could not be established (Builder.Establish returned null). "
                    + "Verify the VPN consent dialog was accepted and that no other always-on VPN is owning the tunnel.");
            DiagnosticLog.Write("AndroidVpnService", "Builder.Establish() returned a TUN file descriptor");

            // Keep the original descriptor in Java. Android removes the tun
            // interface only when that descriptor is closed. The Go side gets
            // a dup and must not be the last owner.
            int tunFd;
            try
            {
                ParcelFileDescriptor reader = tunInterface.Dup();
                tunFd = reader.DetachFd();
                reader.Dispose();
                lock (SyncRoot)
                {
                    CloseOwnedTunLocked();
                    activeTun = tunInterface;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.DupTun", ex);
                tunFd = tunInterface.DetachFd();
                tunInterface.Dispose();
            }

            lock (SyncRoot)
            {
                if (httpProxyPort > 0)
                {
                    try
                    {
                        OpenFluxHttpBridge.Shared.Start(proxyPort, tunAddress, httpProxyPort);
                        DiagnosticLog.Write("AndroidVpnService", $"HTTP CONNECT {tunAddress}:{httpProxyPort} -> SOCKS {proxyPort}");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteException("AndroidVpnService.HttpBridge", ex);
                    }
                }

                // Every reverse call from the Go tun2socks threads into managed code attaches
                // that Go thread to the Mono runtime, and a Mono stop-the-world then has to
                // suspend it. When VpnService.protect() blocks on the system_server VPN lock
                // (which happens exactly while the tunnel is being torn down) the whole app
                // freezes until that binder call returns. The app is never routed into its own
                // TUN — it is disallowed in bypass mode and skipped in whitelist mode — so the
                // callback is only registered on the devices where that exclusion failed.
                if (ownProcessExcluded)
                {
                    XRayCoreWrapper.BindAndroidSocketProtect(null);
                    DiagnosticLog.Write("AndroidVpnService", "Socket protect callback not needed (own process excluded from TUN)");
                }
                else
                {
                    Volatile.Write(ref protectAllowed, 1);
                    XRayCoreWrapper.BindAndroidSocketProtect(ProtectSocketSafe);
                    DiagnosticLog.Write("AndroidVpnService", "Socket protect callback bound (own process is inside the TUN)");
                }

                string? bridgeError = XRayCoreWrapper.StartAndroidTunnel(
                    tunFd,
                    proxyPort,
                    udpEnabled,
                    localProxyCredentials,
                    limitMux: limitMux);
                if (!string.IsNullOrWhiteSpace(bridgeError))
                {
                    XRayCoreWrapper.StopAndroidTunnel();
                    throw new InvalidOperationException(bridgeError);
                }

                EnsureHealthTimer();
            }

            DiagnosticLog.Write(
                "AndroidVpnService",
                $"Android VPN established with proxyPort={proxyPort}, tunAddress={tunAddress}, dns={dns}, udpEnabled={udpEnabled}, authEnabled={localProxyCredentials.HasValue}, appRulesMode={appRulesMode}, appPackages={string.Join(",", appPackages)}");
        }

        private void StartForegroundFast()
        {
            Notification notification = AndroidConnectionNotificationManager.BuildForegroundNotification(this);
            StartForegroundCompat(notification);
        }

        internal void StartForegroundCompat(Notification notification)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(
                    AndroidConnectionNotificationManager.ForegroundNotificationId,
                    notification,
                    ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(AndroidConnectionNotificationManager.ForegroundNotificationId, notification);
            }
        }

        private void EnsureHealthTimer()
        {
            healthTimer?.Dispose();
            int generation = Interlocked.Increment(ref healthGeneration);
            healthMisses = 0;
            tunHealthySinceMs = System.Environment.TickCount64;
            healthTimer = new Timer(
                _ => OnHealthTimerTick(generation),
                state: null,
                dueTime: TimeSpan.FromSeconds(3),
                period: TimeSpan.FromSeconds(2));
        }

        private void OnHealthTimerTick(int generation)
        {
            if (Volatile.Read(ref healthGeneration) != generation)
                return;

            if (XRayCoreWrapper.IsAndroidTunnelRunning())
            {
                healthMisses = 0;
                return;
            }

            // After Wi-Fi/cell flaps the TUN fd can drop for a few seconds.
            // Killing the service here made the next RUN end in Stopped.
            if (System.Environment.TickCount64 - tunHealthySinceMs < 15000)
                return;

            healthMisses++;
            if (healthMisses < 4)
            {
                DiagnosticLog.Write(
                    "AndroidVpnService",
                    $"tun2socks not running ({healthMisses}/4), waiting");
                return;
            }

            string message = XRayCoreWrapper.GetAndroidTunnelLastError()
                ?? "Android tunnel bridge stopped unexpectedly.";
            DiagnosticLog.Write("AndroidVpnService", message);
            StopVpn(message, Volatile.Read(ref vpnGeneration));
        }

        public override void OnRevoke()
        {
            DiagnosticLog.Write("AndroidVpnService", "VPN revoked");
            int revokeGeneration = Volatile.Read(ref vpnGeneration);
            _ = Task.Run(() =>
            {
                try { StopVpn("Android VPN revoked", revokeGeneration); }
                catch (Exception ex) { DiagnosticLog.WriteException("AndroidVpnService.OnRevoke", ex); }
            });
            base.OnRevoke();
        }

        private bool ProtectSocketSafe(int fileDescriptor)
        {
            if (fileDescriptor <= 0 || Volatile.Read(ref protectAllowed) == 0)
                return false;

            try
            {
                return Protect(fileDescriptor);
            }
            catch (Exception ex)
            {
                // This runs on a Go thread; letting the exception escape into cgo kills the process.
                DiagnosticLog.WriteException("AndroidVpnService.Protect", ex);
                return false;
            }
        }

        private static void UnbindSocketProtect()
        {
            Volatile.Write(ref protectAllowed, 0);
            try { XRayCoreWrapper.BindAndroidSocketProtect(null); }
            catch (Exception ex) { DiagnosticLog.WriteException("AndroidVpnService.UnbindProtect", ex); }
        }

        private void ResetVpnCore(string reason)
        {
            Interlocked.Increment(ref healthGeneration);
            healthTimer?.Dispose();
            healthTimer = null;

            UnbindSocketProtect();

            try
            {
                XRayCoreWrapper.StopAndroidTunnel();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.ResetTunnel", ex);
            }

            CloseOwnedTun();
        }

        private void StopVpnCore(string reason)
        {
            Interlocked.Increment(ref healthGeneration);
            healthTimer?.Dispose();
            healthTimer = null;

            // Must happen before the tunnel teardown: a protect() call racing with the
            // system_server VPN teardown is what used to freeze the whole process.
            UnbindSocketProtect();

            long startedMs = System.Environment.TickCount64;
            try
            {
                XRayCoreWrapper.StopAndroidTunnel();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.StopTunnel", ex);
            }
            long elapsedMs = System.Environment.TickCount64 - startedMs;
            DiagnosticLog.Write("AndroidVpnService", $"StopAndroidTunnel took {elapsedMs}ms");
            CloseOwnedTun();

            try { OpenFluxHttpBridge.Shared.Stop(); } catch { }

            AndroidVpnServiceController.NotifyStopped(reason);
        }

        private void StopVpn(string reason, int expectedGeneration = -1, bool endSession = true)
        {
            int currentGeneration = Volatile.Read(ref vpnGeneration);
            if (expectedGeneration >= 0 && expectedGeneration != currentGeneration)
            {
                DiagnosticLog.Write(
                    "AndroidVpnService",
                    $"Skip stale stop gen={expectedGeneration} current={currentGeneration} reason={reason}");
                return;
            }

            lock (SyncRoot)
            {
                currentGeneration = Volatile.Read(ref vpnGeneration);
                if (expectedGeneration >= 0 && expectedGeneration != currentGeneration)
                    return;

                StopVpnCore(reason);
            }

            if (!endSession)
                return;

            if (expectedGeneration >= 0 && expectedGeneration != Volatile.Read(ref vpnGeneration))
                return;

            AndroidConnectionNotificationManager.MarkStopped();

            try
            {
                StopForeground(StopForegroundFlags.Detach);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.StopForeground", ex);
            }

            AndroidConnectionNotificationManager.Republish();
        }

        private static void CloseOwnedTun()
        {
            lock (SyncRoot)
                CloseOwnedTunLocked();
        }

        private static void CloseOwnedTunLocked()
        {
            ParcelFileDescriptor? owned = activeTun;
            activeTun = null;
            if (owned == null)
                return;

            try
            {
                owned.Close();
                DiagnosticLog.Write("AndroidVpnService", "Closed the VPN interface descriptor");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.CloseTun", ex);
            }
        }

        private static void ExcludeServerFromTunnel(Builder builder, string server)
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                DiagnosticLog.Write("AndroidVpnService", "No server address to exclude from the TUN");
                return;
            }

            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            {
                DiagnosticLog.Write(
                    "AndroidVpnService",
                    $"Server bypass route needs Android 13+, current API={Build.VERSION.SdkInt}, server={server}");
                return;
            }

            foreach (string ip in ResolveBypassAddresses(server))
            {
                if (IPAddress.TryParse(ip, out IPAddress parsed) && IPAddress.IsLoopback(parsed))
                {
                    DiagnosticLog.Write("AndroidVpnService", $"Skip loopback {ip}; it is not a TUN bypass route");
                    continue;
                }

                try
                {
                    Java.Net.InetAddress address = Java.Net.InetAddress.GetByName(ip);
                    int prefix = address is Java.Net.Inet6Address ? 128 : 32;
                    builder.ExcludeRoute(new IpPrefix(address, prefix));
                    DiagnosticLog.Write("AndroidVpnService", $"Excluded {ip}/{prefix} from the TUN so the tunnel can reach its server");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("AndroidVpnService.ExcludeRoute", ex);
                }
            }
        }

        private static IEnumerable<string> ResolveBypassAddresses(string server)
        {
            HashSet<string> addresses = new(StringComparer.OrdinalIgnoreCase);
            foreach (string part in server.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = part.Trim();
                if (candidate.StartsWith('[') && candidate.Contains(']'))
                    candidate = candidate.Substring(1, candidate.IndexOf(']') - 1);
                else if (candidate.Count(ch => ch == ':') == 1)
                    candidate = candidate.Substring(0, candidate.IndexOf(':'));

                if (IPAddress.TryParse(candidate, out IPAddress parsed))
                {
                    addresses.Add(parsed.ToString());
                    continue;
                }

                try
                {
                    foreach (IPAddress resolved in Dns.GetHostAddresses(candidate))
                        addresses.Add(resolved.ToString());
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("AndroidVpnService.ResolveBypass", ex);
                }
            }

            return addresses;
        }

        private static string[] SplitDnsServers(string dns)
        {
            string[] servers = dns
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(server => server.Trim())
                .Where(server => !string.IsNullOrWhiteSpace(server))
                .ToArray();

            return servers.Length == 0 ? new[] { "8.8.8.8" } : servers;
        }

        /// <returns><c>true</c> when this app's own traffic is guaranteed to stay outside the TUN.</returns>
        private bool ApplyApplicationRules(Builder builder, AppRulesMode mode, IEnumerable<string> packages)
        {
            string[] packageArray = packages as string[] ?? packages.ToArray();
            DiagnosticLog.Write($"[AppRules] ApplyApplicationRules: mode={mode}, packageCount={packageArray.Length}");

            if (mode == AppRulesMode.ONLY_SELECTED_APPS)
            {
                if (packageArray.Length == 0)
                {
                    // Whitelist with zero entries would silently route ALL apps through the VPN
                    // because Android falls back to "no allow-list" when no allowed package is
                    // registered. The user explicitly opted for "only selected apps", so refuse
                    // to start instead of producing surprising routing.
                    throw new InvalidOperationException(
                        "Whitelist mode is enabled but no apps are selected. "
                        + "Open Settings → App rules → Manage and choose at least one application, "
                        + "or switch the mode back to \"All apps\".");
                }

                DiagnosticLog.Write("[AppRules] → TryAllowUserSelectedApplications (AddAllowedApplication)");
                int allowed = TryAllowUserSelectedApplications(builder, packageArray);

                if (allowed == 0)
                {
                    // Every requested package failed to register (e.g. all of them were uninstalled
                    // after the template was saved). Without at least one allowed app the VPN would
                    // route everything; fail closed with a clear message instead.
                    throw new InvalidOperationException(
                        "Whitelist mode is enabled but none of the selected apps are installed on this device. "
                        + "Open Settings → App rules → Manage and re-pick the applications you want to route through the VPN.");
                }

                // A whitelist never contains this app, so its sockets stay off the TUN.
                return true;
            }

            DiagnosticLog.Write("[AppRules] → TryExcludeOwnProcess + TryExcludeUserSelectedApplications (AddDisallowedApplication)");
            bool ownProcessExcluded = TryExcludeOwnProcess(builder);
            TryExcludeUserSelectedApplications(builder, packageArray);
            return ownProcessExcluded;
        }

        private bool TryExcludeOwnProcess(Builder builder)
        {
            try
            {
                builder.AddDisallowedApplication(PackageName!);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.AddDisallowedApplication", ex);
                return false;
            }
        }

        private void TryExcludeUserSelectedApplications(Builder builder, IEnumerable<string> packages)
        {
            int added = 0;
            foreach (string packageName in packages)
            {
                try
                {
                    if (string.Equals(packageName, PackageName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    builder.AddDisallowedApplication(packageName);
                    added++;
                    DiagnosticLog.Write($"[AppRules] Disallowed: {packageName}");
                }
                catch (PackageManager.NameNotFoundException ex)
                {
                    DiagnosticLog.WriteException($"AndroidVpnService.AddDisallowedApplication.{packageName}", ex);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException($"AndroidVpnService.AddDisallowedApplication.{packageName}", ex);
                }
            }

            DiagnosticLog.Write($"[AppRules] Total disallowed: {added}");
        }

        private int TryAllowUserSelectedApplications(Builder builder, IEnumerable<string> packages)
        {
            int added = 0;
            int skippedSelf = 0;
            int skippedMissing = 0;

            foreach (string packageName in packages)
            {
                try
                {
                    if (string.Equals(packageName, PackageName, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedSelf++;
                        DiagnosticLog.Write($"[AppRules] Skipped own package in whitelist: {packageName}");
                        continue;
                    }

                    builder.AddAllowedApplication(packageName);
                    added++;
                    DiagnosticLog.Write($"[AppRules] Allowed: {packageName}");
                }
                catch (PackageManager.NameNotFoundException ex)
                {
                    skippedMissing++;
                    DiagnosticLog.WriteException($"AndroidVpnService.AddAllowedApplication.{packageName}", ex);
                }
                catch (Exception ex)
                {
                    skippedMissing++;
                    DiagnosticLog.WriteException($"AndroidVpnService.AddAllowedApplication.{packageName}", ex);
                }
            }

            DiagnosticLog.Write($"[AppRules] Total allowed: {added}, skippedSelf={skippedSelf}, skippedMissing={skippedMissing}");
            return added;
        }

        private void TryEnableIpv6(Builder builder)
        {
            try
            {
                builder.AddAddress(DefaultIpv6Address, DefaultIpv6PrefixLength);
                builder.AddRoute("::", 0);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("AndroidVpnService.EnableIpv6", ex);
            }
        }

        private static AppRulesMode NormalizeAppRulesMode(int rawValue)
        {
            return rawValue switch
            {
                (int)AppRulesMode.BYPASS_SELECTED_APPS => AppRulesMode.BYPASS_SELECTED_APPS,
                (int)AppRulesMode.ONLY_SELECTED_APPS => AppRulesMode.ONLY_SELECTED_APPS,
                _ => AppRulesMode.ALL_APPS
            };
        }
    }
}
