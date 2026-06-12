using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using InvisibleGorillaXRay.Core;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaOperationEntry
    {
        public DateTime TimestampUtc { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Persisted ring buffer of the most recent Goida operations (refresh, probe, active node
    /// changes, auto-switch, connect/disconnect). Survives restarts so the user can see what
    /// happened last, even after an unexpected close.
    /// </summary>
    public sealed class GoidaOperationLog
    {
        private const int MaxEntries = 40;

        private readonly object sync = new();
        private readonly string filePath;
        private List<GoidaOperationEntry> entries = new();

        public GoidaOperationLog(string filePath)
        {
            this.filePath = filePath;
            Load();
        }

        public void Add(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            lock (sync)
            {
                GoidaOperationEntry? last = entries.Count > 0 ? entries[entries.Count - 1] : null;
                // Skip consecutive duplicates so a chatty background loop doesn't flood the list.
                if (last != null && string.Equals(last.Message, message.Trim(), StringComparison.Ordinal))
                {
                    last.TimestampUtc = DateTime.UtcNow;
                }
                else
                {
                    entries.Add(new GoidaOperationEntry
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Message = message.Trim()
                    });
                }

                if (entries.Count > MaxEntries)
                    entries.RemoveRange(0, entries.Count - MaxEntries);

                Save();
            }
        }

        public IReadOnlyList<GoidaOperationEntry> GetRecent(int count = 20)
        {
            lock (sync)
            {
                return entries
                    .AsEnumerable()
                    .Reverse()
                    .Take(Math.Max(1, count))
                    .ToList();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(filePath))
                    return;

                string json = File.ReadAllText(filePath);
                entries = JsonConvert.DeserializeObject<List<GoidaOperationEntry>>(json)
                    ?? new List<GoidaOperationEntry>();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GoidaOperationLog.Load", ex);
                entries = new List<GoidaOperationEntry>();
            }
        }

        private void Save()
        {
            try
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                InvisibleGorillaXRay.Utilities.FileUtility.WriteAllTextAtomic(
                    filePath,
                    JsonConvert.SerializeObject(entries, Formatting.Indented));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GoidaOperationLog.Save", ex);
            }
        }
    }
}
