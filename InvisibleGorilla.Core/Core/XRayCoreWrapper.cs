using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InvisibleGorillaXRay.Core
{
    using Models;
    using Values;

    public class XRayCoreWrapper
    {
        private const string LIB_NAME = "XRayCore";

        static XRayCoreWrapper()
        {
            NativeLibrary.SetDllImportResolver(typeof(XRayCoreWrapper).Assembly, ResolveDllImport);
        }

        private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != LIB_NAME)
                return IntPtr.Zero;

            // Android 7+ expects native code to come from the app's packaged lib/<abi> directory.
            // Loading from writable app-private storage is unreliable and can be rejected by the linker.
            if (OperatingSystem.IsAndroid())
                return NativeLibrary.Load(libraryName, assembly, searchPath);

            string libDir = Values.Directory.LIBRARIES;

            string libPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                libPath = System.IO.Path.Combine(libDir, "XRayCore.dll");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                libPath = System.IO.Path.Combine(libDir, "XRayCore.dylib");
            else
                libPath = System.IO.Path.Combine(libDir, "libXRayCore.so");

            if (File.Exists(libPath))
                return NativeLibrary.Load(libPath);

            return NativeLibrary.Load(libraryName, assembly, searchPath);
        }

        public static string GetConfigFormat(string path)
        {
            IntPtr pathPtr = StringToUtf8Ptr(path);
            try
            {
                return Marshal.PtrToStringAnsi(GetConfigFormatNative(pathPtr));
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }

            [DllImport(LIB_NAME, EntryPoint = "GetConfigFormat")]
            static extern IntPtr GetConfigFormatNative(IntPtr pathPtr);
        }

        public static bool IsFileExists(string path)
        {
            IntPtr pathPtr = StringToUtf8Ptr(path);
            try
            {
                return IsFileExistsNative(pathPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }

            [DllImport(LIB_NAME, EntryPoint = "IsFileExists")]
            [return: MarshalAs(UnmanagedType.I1)]
            static extern bool IsFileExistsNative(IntPtr pathPtr);
        }

        public static string LoadConfig(string fileFormat, string filePath)
        {
            IntPtr formatPtr = StringToUtf8Ptr(fileFormat);
            IntPtr pathPtr = StringToUtf8Ptr(filePath);
            try
            {
                return Marshal.PtrToStringAnsi(LoadConfigNative(formatPtr, pathPtr));
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
                Marshal.FreeHGlobal(pathPtr);
            }

            [DllImport(LIB_NAME, EntryPoint = "LoadConfig")]
            static extern IntPtr LoadConfigNative(IntPtr formatPtr, IntPtr pathPtr);
        }

        public static void StartServer(
            string config,
            int port,
            LogLevel logLevel,
            string logPath,
            bool isSocks,
            bool isUdpEnabled,
            LocalProxyCredentials? localProxyCredentials = null)
        {
            LocalProxyCredentials credentials = localProxyCredentials ?? LocalProxyCredentials.None;
            DiagnosticLog.Write("XRayWrapper", $"StartServer: port={port}, logLevel={logLevel}, logPath={logPath}, isSocks={isSocks}, isUdpEnabled={isUdpEnabled}, authEnabled={credentials.HasValue}");
            DiagnosticLog.Write("XRayWrapper", $"Config size: {config?.Length ?? 0} bytes");

            IntPtr logPathPtr = StringToUtf8Ptr(logPath);
            IntPtr usernamePtr = StringToUtf8Ptr(credentials.Username);
            IntPtr passwordPtr = StringToUtf8Ptr(credentials.Password);
            try
            {
                DiagnosticLog.Write("XRayWrapper", "Calling native StartServer...");
                StartServerNative(config, port, logLevel.ToString(), logPathPtr, isSocks, isUdpEnabled, usernamePtr, passwordPtr);
                DiagnosticLog.Write("XRayWrapper", "Native StartServer returned normally");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("XRayWrapper.StartServer", ex);
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(logPathPtr);
                Marshal.FreeHGlobal(usernamePtr);
                Marshal.FreeHGlobal(passwordPtr);
            }

            [DllImport(LIB_NAME, EntryPoint = "StartServer")]
            static extern void StartServerNative(
                string config,
                int port,
                string logLevel,
                IntPtr logPathPtr,
                [MarshalAs(UnmanagedType.I1)] bool isSocks,
                [MarshalAs(UnmanagedType.I1)] bool isUdpEnabled,
                IntPtr usernamePtr,
                IntPtr passwordPtr);
        }

        private static int serverStopInFlight;

        public static void StopServer()
        {
            if (Interlocked.CompareExchange(ref serverStopInFlight, 1, 0) != 0)
            {
                DiagnosticLog.Write("XRayWrapper", "StopServer already in flight; skipping reentry");
                return;
            }

            var stop = Task.Run(() =>
            {
                try
                {
                    StopServerNative();
                }
                finally
                {
                    Interlocked.Exchange(ref serverStopInFlight, 0);
                }
            });

            if (!stop.Wait(2000))
                DiagnosticLog.Write("XRayWrapper", "StopServer timed out after 2000ms");

            [DllImport(LIB_NAME, EntryPoint = "StopServer")]
            static extern void StopServerNative();
        }

        public static int TestConnection(string config, int port)
        {
            return TestConnectionNative(config, port);

            [DllImport(LIB_NAME, EntryPoint = "TestConnection")]
            static extern int TestConnectionNative(string config, int port);
        }

        public static string GetVersion()
        {
            return Marshal.PtrToStringAnsi(GetXRayCoreVersionNative());

            [DllImport(LIB_NAME, EntryPoint = "GetXrayCoreVersion")]
            static extern IntPtr GetXRayCoreVersionNative();
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AndroidProtectDelegate(int fileDescriptor);

        private static AndroidProtectDelegate? androidProtectDelegate;

        public static void BindAndroidSocketProtect(Func<int, bool>? protect)
        {
            if (!OperatingSystem.IsAndroid())
                return;

            if (protect == null)
            {
                // Clear the native pointer first: dropping the delegate while the Go side can
                // still reach it would let the GC collect a callback that is about to be called.
                SetAndroidSocketProtectNative(IntPtr.Zero);
                androidProtectDelegate = null;
                return;
            }

            androidProtectDelegate = fd => protect(fd) ? 1 : 0;
            SetAndroidSocketProtectNative(Marshal.GetFunctionPointerForDelegate(androidProtectDelegate));
        }

        [DllImport(LIB_NAME, EntryPoint = "SetAndroidSocketProtect", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetAndroidSocketProtectNative(IntPtr callback);

        public static string? StartAndroidTunnel(
            int fileDescriptor,
            int proxyPort,
            bool isUdpEnabled,
            LocalProxyCredentials? localProxyCredentials = null,
            bool limitMux = false)
        {
            if (!WaitForAndroidTunnelStop(2500))
                return "Android tunnel is still stopping";

            LocalProxyCredentials credentials = localProxyCredentials ?? LocalProxyCredentials.None;
            IntPtr usernamePtr = StringToUtf8Ptr(credentials.Username);
            IntPtr passwordPtr = StringToUtf8Ptr(credentials.Password);
            try
            {
                IntPtr errorPtr = StartAndroidTunnelNative(fileDescriptor, proxyPort, isUdpEnabled, usernamePtr, passwordPtr, limitMux);
                return errorPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(errorPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(usernamePtr);
                Marshal.FreeHGlobal(passwordPtr);
            }

            [DllImport(LIB_NAME, EntryPoint = "StartAndroidTun2Socks")]
            static extern IntPtr StartAndroidTunnelNative(
                int fileDescriptor,
                int proxyPort,
                [MarshalAs(UnmanagedType.I1)] bool isUdpEnabled,
                IntPtr usernamePtr,
                IntPtr passwordPtr,
                [MarshalAs(UnmanagedType.I1)] bool limitMux);
        }

        private static int androidTunnelStopInFlight;
        private static int androidTunnelStopTicket;

        public static bool WaitForAndroidTunnelStop(int timeoutMs)
        {
            long started = Environment.TickCount64;
            while (Volatile.Read(ref androidTunnelStopInFlight) != 0)
            {
                if (Environment.TickCount64 - started >= timeoutMs)
                {
                    // A native stop that never returns used to fail every later
                    // OpenFlux start. Detach that attempt and let the new session own the TUN.
                    DiagnosticLog.Write("AndroidTunnel", "Previous stop still running; detaching so the next tunnel can start");
                    Interlocked.Increment(ref androidTunnelStopTicket);
                    Interlocked.Exchange(ref androidTunnelStopInFlight, 0);
                    return true;
                }

                Thread.Sleep(50);
            }

            return true;
        }

        public static void StopAndroidTunnel()
        {
            if (Interlocked.CompareExchange(ref androidTunnelStopInFlight, 1, 0) != 0)
            {
                DiagnosticLog.Write("AndroidTunnel", "Stop already in flight; skipping reentry");
                return;
            }

            int ticket = Interlocked.Increment(ref androidTunnelStopTicket);
            var stop = Task.Run(() =>
            {
                try
                {
                    StopAndroidTunnelNative();
                }
                finally
                {
                    if (Volatile.Read(ref androidTunnelStopTicket) == ticket)
                        Interlocked.Exchange(ref androidTunnelStopInFlight, 0);
                }
            });

            if (!stop.Wait(2000))
                DiagnosticLog.Write("AndroidTunnel", "Stop timed out after 2000ms");

            [DllImport(LIB_NAME, EntryPoint = "StopAndroidTun2Socks")]
            static extern void StopAndroidTunnelNative();
        }

        public static bool IsAndroidTunnelRunning()
        {
            return IsAndroidTunnelRunningNative();

            [DllImport(LIB_NAME, EntryPoint = "IsAndroidTun2SocksRunning")]
            [return: MarshalAs(UnmanagedType.I1)]
            static extern bool IsAndroidTunnelRunningNative();
        }

        public static string? GetAndroidTunnelLastError()
        {
            IntPtr errorPtr = GetAndroidTunnelLastErrorNative();
            return errorPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(errorPtr);

            [DllImport(LIB_NAME, EntryPoint = "GetAndroidTun2SocksLastError")]
            static extern IntPtr GetAndroidTunnelLastErrorNative();
        }

        private static IntPtr StringToUtf8Ptr(string str)
        {
            if (string.IsNullOrEmpty(str))
                str = string.Empty;

            byte[] bytes = Encoding.UTF8.GetBytes(str);
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            return pointer;
        }
    }
}
