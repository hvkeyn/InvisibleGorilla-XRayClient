using System;
using System.IO;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Utilities;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay.Services.Goida
{
    public static class GoidaConnectionTest
    {
        public static Func<string, int> CreateFromConfigPath(
            Func<string, Status> loadConfig,
            Func<string, int> testJson)
        {
            return configPath =>
            {
                try
                {
                    string resolvedPath = FileUtility.GetFullPath(configPath);
                    if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                    {
                        DiagnosticLog.Write("GoidaConnectionTest",
                            $"Config missing: {configPath}");
                        return Availability.ERROR;
                    }

                    Status status = loadConfig(resolvedPath);
                    if (status.Code != Code.SUCCESS)
                        return Availability.ERROR;

                    string json = status.Content?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(json))
                        return Availability.ERROR;

                    return testJson(json);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("GoidaConnectionTest", ex);
                    return Availability.ERROR;
                }
            };
        }
    }
}
