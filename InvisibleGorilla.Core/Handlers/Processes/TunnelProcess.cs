using System;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace InvisibleGorillaXRay.Handlers.Processes
{
    using Foundation;
    using Services;
    using Models;
    using Utilities;
    using Values;

    public class TunnelProcess
    {
        private static readonly string[] TunProcessNames = { "InvisibleGorilla-TUN", "InvisibleMan-TUN" };

        private IPEndPoint endPoint;
        private Socket sender;

        private Func<int> getPort;
        private Processor processor;
        private readonly string tunProcessName;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public TunnelProcess()
        {
            this.processor = new Processor();
            this.tunProcessName = System.IO.Path.GetFileNameWithoutExtension(Path.TUN_EXE);
        }

        public void Setup(Func<int> getPort)
        {
            this.getPort = getPort;
        }

        public void Start()
        {
            if (IsProcessRunning())
                return;
            
            foreach (string processName in TunProcessNames)
                processor.StopSystemProcesses(processName);

            processor.StartProcess(
                processName: tunProcessName,
                fileName: System.IO.Path.GetFullPath(Path.TUN_EXE),
                workingDirectory: Directory.TUN,
                command: $"-port={getPort.Invoke()}",
                runAsAdmin: true
            );
        }

        public Status Connect()
        {
            if (IsConnected())
                return new Status(
                    code: Code.SUCCESS,
                    subCode: SubCode.SUCCESS,
                    content: null
                );

            try
            {
                endPoint = new IPEndPoint(IPAddress.Loopback, getPort.Invoke());
                sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sender.Connect(endPoint);
                System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();

                return new Status(
                    code: Code.SUCCESS,
                    subCode: SubCode.SUCCESS,
                    content: null
                );
            }
            catch
            {
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_CONNECT_TO_TUNNEL_SERVICE,
                    content: LocalizationService.GetTerm(Localization.CANT_CONNECT_TO_TUNNEL_SERVICE)
                );
            }

            bool IsConnected()
            {
                return sender != null && 
                    !((sender.Poll(1000, SelectMode.SelectRead) && (sender.Available == 0)) || !sender.Connected);
            }
        }

        public Status Execute(string command)
        {
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(command + "<EOF>");
                int bytesCount = sender.Send(bytes);

                return new Status(
                    code: Code.SUCCESS,
                    subCode: SubCode.SUCCESS,
                    content: null
                );
            }
            catch
            {
                return new Status(
                    code: Code.ERROR,
                    subCode: SubCode.CANT_TUNNEL,
                    content: LocalizationService.GetTerm(Localization.CANT_TUNNEL_SYSTEM)
                );
            }
        }

        public bool IsProcessRunning() => processor.IsProcessRunning(tunProcessName);

        public bool IsProcessPortActive() => NetworkUtility.IsPortActive(getPort.Invoke());
    }
}