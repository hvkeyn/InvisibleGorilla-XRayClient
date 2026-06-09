using System;
using System.IO;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;
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
                    if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                        return Availability.ERROR;

                    Status status = loadConfig(configPath);
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
