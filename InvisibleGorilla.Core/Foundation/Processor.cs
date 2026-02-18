using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace InvisibleGorillaXRay.Foundation
{
    using Values;

    public class Processor
    {
        private Dictionary<string, Process> processes;

        public Processor()
        {
            this.processes = new Dictionary<string, Process>();
        }

        public void StartProcess(string processName, string fileName, string workingDirectory, string command, bool runAsAdmin)
        {
            try
            {
                Process process = new Process();
                process.StartInfo.FileName = fileName;
                process.StartInfo.Arguments = command;
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.WorkingDirectory = workingDirectory;

                if (runAsAdmin)
                    process.StartInfo.Verb = "runas";
                
                process.Start();
                AddProcess(process, processName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start process '{processName}': {ex.Message}");
                StopProcess(processName);
            }
        }

        public void StopProcess(string processName)
        {
            if (!processes.TryGetValue(processName, out Process process))
                return;

            try
            {
                RemoveProcess(processName);
                process.Kill(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to stop process '{processName}': {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        public void StopSystemProcesses(string processName)
        {
            Process[] runningProcesses = Process.GetProcessesByName(processName);

            foreach (Process process in runningProcesses)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to kill system process '{processName}': {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private void AddProcess(Process process, string processName)
        {
            if (processes.ContainsKey(processName))
            {
                processes[processName].Dispose();
                processes.Remove(processName);
            }
            processes.Add(processName, process);
        }

        private void RemoveProcess(string processName)
        {
            if (!IsProcessExists(processName))
                return;
            
            processes.Remove(processName);
        }

        public bool IsProcessRunning(string processName) 
        {
            if (!processes.TryGetValue(processName, out Process process))
                return false;
            
            try
            {
                if (process.HasExited)
                {
                    RemoveProcess(processName);
                    process.Dispose();
                    return false;
                }
                
                return Process.GetProcessById(process.Id) != null;
            }
            catch
            {
                RemoveProcess(processName);
                return false;
            }
        }

        private bool IsProcessExists(string processName) => processes.ContainsKey(processName);
    }
}
