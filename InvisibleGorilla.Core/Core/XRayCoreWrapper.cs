using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace InvisibleGorillaXRay.Core
{
    using Models;
    using Values;

    internal class XRayCoreWrapper
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

        public static void StartServer(string config, int port, LogLevel logLevel, string logPath, bool isSocks, bool isUdpEnabled)
        {
            DiagnosticLog.Write("XRayWrapper", $"StartServer: port={port}, logLevel={logLevel}, logPath={logPath}, isSocks={isSocks}, isUdpEnabled={isUdpEnabled}");
            DiagnosticLog.Write("XRayWrapper", $"Config size: {config?.Length ?? 0} bytes");

            IntPtr logPathPtr = StringToUtf8Ptr(logPath);
            try
            {
                DiagnosticLog.Write("XRayWrapper", "Calling native StartServer...");
                StartServerNative(config, port, logLevel.ToString(), logPathPtr, isSocks, isUdpEnabled);
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
            }

            [DllImport(LIB_NAME, EntryPoint = "StartServer")]
            static extern void StartServerNative(string config, int port, string logLevel, IntPtr logPathPtr, bool isSocks, bool isUdpEnabled);
        }

        public static void StopServer()
        {
            StopServerNative();

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
