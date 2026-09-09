using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InvisibleGorillaXRay.Handlers.Tunnels
{
    using Core;
    using Foundation;
    using Handlers;
    using InvisibleGorillaXRay.Services;
    using Models;
    using Values;

    public class WindowsTunnel : ITunnel
    {
        private const int ServiceStartTimeoutMs = 10000;
        private const int ServicePortTimeoutMs = 10000;
        private const int InterfaceReadyTimeoutMs = 20000;

        private bool isCanceled;
        private Scheduler scheduler;

        private Action onStartTunnelingService;
        private Func<bool> isServiceRunning;
        private Func<bool> isServicePortActive;
        private Func<Status> connectTunnelingService;
        private Func<string, Status> executeCommand;

        private const string NETWORK_INTERFACE_NAME = "InvisibleGorilla-XRay";

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public WindowsTunnel()
        {
            this.scheduler = new Scheduler();
        }

        public void Setup(
            Action onStartTunnelingService,
            Func<bool> isServiceRunning,
            Func<bool> isServicePortActive,
            Func<Status> connectTunnelingService,
            Func<string, Status> executeCommand
        )
        {
            this.onStartTunnelingService = onStartTunnelingService;
            this.isServiceRunning = isServiceRunning;
            this.isServicePortActive = isServicePortActive;
            this.connectTunnelingService = connectTunnelingService;
            this.executeCommand = executeCommand;
        }

        public Status Enable(string ip, int port, string address, string server, string dns, LocalProxyCredentials localProxyCredentials)
        {
            isCanceled = false;
            DiagnosticLog.Write(
                "WindowsTunnel",
                $"Enable requested: proxy={ip}:{port}, address={address}, server={server}, dns={dns}, authEnabled={localProxyCredentials?.HasValue == true}");

            try
            {
                if (string.IsNullOrWhiteSpace(server))
                {
                    DiagnosticLog.Write("WindowsTunnel", "Cannot enable TUN because server address is empty.");
                    return new Status(
                        code: Code.ERROR,
                        subCode: SubCode.CANT_TUNNEL,
                        content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                    );
                }

                FetchServerIP();
                DiagnosticLog.Write("WindowsTunnel", $"Resolved server IP={server}");
                string proxyArgument = BuildTunnelProxyArgument(ip, port, localProxyCredentials);

                DiagnosticLog.Write("WindowsTunnel", "Starting tunneling service");
                StartTunnelingService();

                bool isServiceStartTimedOut = WaitUntilServiceWasRun(out bool isServiceRunConditionSatisfied);
                DiagnosticLog.Write(
                    "WindowsTunnel",
                    $"WaitUntilServiceWasRun: satisfied={isServiceRunConditionSatisfied}, timedOut={isServiceStartTimedOut}, isCanceled={isCanceled}");
                if (!isServiceRunConditionSatisfied)
                    return isServiceStartTimedOut ? ServiceStartTimeoutStatus() : CancelStatus();
                
                bool isServicePortTimedOut = WaitUntilServicePortWasActive(out bool isServicePortConditionSatisfied);
                DiagnosticLog.Write(
                    "WindowsTunnel",
                    $"WaitUntilServicePortWasActive: satisfied={isServicePortConditionSatisfied}, timedOut={isServicePortTimedOut}, isCanceled={isCanceled}");
                if (!isServicePortConditionSatisfied)
                    return isServicePortTimedOut ? ServicePortTimeoutStatus() : CancelStatus();
                
                Status connectingStatus = ConnectToTunnelingService();
                DiagnosticLog.Write(
                    "WindowsTunnel",
                    $"ConnectToTunnelingService: code={connectingStatus.Code}, subCode={connectingStatus.SubCode}");
                if (connectingStatus.Code == Code.ERROR)
                    return connectingStatus;

                string enableCommand =
                    $"-command=enable " +
                    $"-device={NETWORK_INTERFACE_NAME} " +
                    $"-proxy={proxyArgument} " +
                    $"-address={address} " +
                    $"-server={server} " +
                    $"-dns={dns}" +
                    BuildAppRulesCommandSuffix();

                DiagnosticLog.Write("WindowsTunnel", "Sending enable command to TUN service");
                Status enablingCommandStatus = ExecuteCommand(command: enableCommand);
                
                if(enablingCommandStatus.Code == Code.ERROR)
                {
                    DiagnosticLog.Write(
                        "WindowsTunnel",
                        $"Enable command failed: code={enablingCommandStatus.Code}, subCode={enablingCommandStatus.SubCode}");
                    return enablingCommandStatus;
                }

                if (!WaitUntilTunInterfaceReady(address))
                {
                    DiagnosticLog.Write("WindowsTunnel", "TUN interface not ready after first enable; retrying command");
                    enablingCommandStatus = ExecuteCommand(command: enableCommand);
                    if (enablingCommandStatus.Code == Code.ERROR
                        || !WaitUntilTunInterfaceReady(address))
                    {
                        Disable();
                        return new Status(
                            code: Code.ERROR,
                            subCode: SubCode.CANT_TUNNEL,
                            content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                        );
                    }
                }
                
                DiagnosticLog.Write(
                    "WindowsTunnel",
                    $"Enable command result: code={enablingCommandStatus.Code}, subCode={enablingCommandStatus.SubCode}");
                
                if (isCanceled)
                    return CancelStatus();

                DiagnosticLog.Write("WindowsTunnel", "Enable completed successfully");
                return new Status(
                    code: Code.SUCCESS,
                    subCode: SubCode.SUCCESS,
                    content: null
                );
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsTunnel.Enable", ex);
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_TUNNEL,
                    content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                );
            }

            void FetchServerIP()
            {
                Uri serverUri = new UriBuilder(server).Uri;
                IPAddress[] addresses = Dns.GetHostAddresses(serverUri.Host);
                IPAddress? ipv4 = addresses.FirstOrDefault(item =>
                    item.AddressFamily == AddressFamily.InterNetwork);
                server = (ipv4 ?? addresses[0]).ToString();
            }

            static string BuildTunnelProxyArgument(string host, int localPort, LocalProxyCredentials credentials)
            {
                if (credentials?.HasValue == true)
                    return credentials.BuildSocks5Uri(host, localPort);

                return $"{host}:{localPort}";
            }

            void StartTunnelingService() => onStartTunnelingService.Invoke();

            bool WaitUntilServiceWasRun(out bool isConditionSatisfied)
            {
                return scheduler.WaitUntil(
                    condition: IsServiceRunning,
                    cancellation: IsServiceCanceled,
                    timeoutMs: ServiceStartTimeoutMs,
                    isConditionSatisfied: out isConditionSatisfied
                );
            }

            bool WaitUntilServicePortWasActive(out bool isConditionSatisfied)
            {
                return scheduler.WaitUntil(
                    condition: IsServicePortActive,
                    cancellation: IsServiceCanceled,
                    timeoutMs: ServicePortTimeoutMs,
                    isConditionSatisfied: out isConditionSatisfied
                );
            }

            bool IsServiceRunning() => isServiceRunning.Invoke();

            bool IsServicePortActive() => isServicePortActive.Invoke();

            bool IsServiceCanceled() => isCanceled;

            Status ConnectToTunnelingService() => connectTunnelingService.Invoke();

            Status ServiceStartTimeoutStatus()
            {
                Disable();

                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_TUNNEL,
                    content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                );
            }

            Status ServicePortTimeoutStatus()
            {
                Disable();

                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_CONNECT_TO_TUNNEL_SERVICE,
                    content: LocalizationService.GetTerm(Localization.CANT_CONNECT_TO_TUNNEL_SERVICE)
                );
            }
        }

        public void Disable()
        {
            DiagnosticLog.Write("WindowsTunnel", "Disable requested");
            isCanceled = false;
            Status status = ExecuteCommand(command: $"-command=disable");
            if (status.Code == Code.ERROR)
            {
                DiagnosticLog.Write("WindowsTunnel", "Primary disable command failed; trying stale TUN cleanup fallback.");
                WindowsStaleTunCleanup.TryDisableStaleTunnel();
            }
        }

        public void Cancel()
        {
            DiagnosticLog.Write("WindowsTunnel", "Cancel requested");
            isCanceled = true;
        }

        private Status CancelStatus()
        {
            isCanceled = false;

            return new Status(
                code: Code.INFO,
                subCode: SubCode.CANCELED,
                content: null
            );
        }

        private Status ExecuteCommand(string command) => executeCommand.Invoke(command);

        private bool WaitUntilTunInterfaceReady(string address)
        {
            int elapsed = 0;
            const int sliceMs = 200;
            while (elapsed < InterfaceReadyTimeoutMs)
            {
                if (isCanceled)
                    return false;

                if (IsTunInterfaceReady(address))
                {
                    DiagnosticLog.Write("WindowsTunnel", $"TUN interface ready after {elapsed}ms");
                    Thread.Sleep(300);
                    return true;
                }

                Thread.Sleep(sliceMs);
                elapsed += sliceMs;
            }

            DiagnosticLog.Write("WindowsTunnel", $"TUN interface not ready after {InterfaceReadyTimeoutMs}ms");
            return false;
        }

        private static bool IsTunInterfaceReady(string address)
        {
            try
            {
                string expectedPrefix = address?.Split('/')[0]?.Trim() ?? string.Empty;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(nic.Name, NETWORK_INTERFACE_NAME, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(nic.Description, NETWORK_INTERFACE_NAME, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (nic.OperationalStatus is not (OperationalStatus.Up or OperationalStatus.Dormant))
                        continue;

                    if (string.IsNullOrWhiteSpace(expectedPrefix))
                        return true;

                    foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (string.Equals(unicast.Address.ToString(), expectedPrefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("WindowsTunnel.IsTunInterfaceReady", ex);
            }

            return false;
        }

        private static string BuildAppRulesCommandSuffix()
        {
            SettingsHandler settingsHandler = new();
            UserSettings settings = settingsHandler.UserSettings;
            AppRulesMode mode = settings.GetEffectiveAppRulesMode();

            string[] appPaths = settings.GetEffectiveEnabledAppRules()
                .Select(rule => rule.AppId?.Trim())
                .Where(appId => !string.IsNullOrWhiteSpace(appId) && System.IO.File.Exists(appId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

            StringBuilder payloadBuilder = new();
            payloadBuilder.Append("MODE=").Append(mode);

            foreach (string appPath in appPaths)
                payloadBuilder.Append('\n').Append(appPath);

            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadBuilder.ToString()));
            DiagnosticLog.Write(
                "WindowsTunnel",
                $"Passing app rules payload with mode={mode} and {appPaths.Length} apps");
            return $" -appRules={payload}";
        }
    }
}