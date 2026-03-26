using System;
using System.IO;

namespace InvisibleGorillaXRay.Core
{
    using Values;

    public static class DiagnosticLog
    {
        private static readonly object LockObj = new object();

        private static string LogFilePath => Values.Path.DIAGNOSTIC_LOG;

        public static void Write(string message)
        {
            try
            {
                Values.Directory.EnsureWritableDirectories();
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                lock (LockObj)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging should never crash the app
            }
        }

        public static void Write(string tag, string message)
        {
            Write($"[{tag}] {message}");
        }

        public static void WriteException(string tag, Exception ex)
        {
            Write($"[{tag}] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Write($"[{tag}] StackTrace: {ex.StackTrace}");
        }

        public static void Clear()
        {
            try
            {
                Values.Directory.EnsureWritableDirectories();
                lock (LockObj)
                {
                    File.WriteAllText(LogFilePath, $"=== Diagnostic Log Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
