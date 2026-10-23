using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace RaudelControlPanelLite
{
    public class ProcessSnapshot
    {
        public int Pid { get; set; }
        public int ParentPid { get; set; }
        public string Name { get; set; } = "";
        public string CmdLine { get; set; } = "";
    }

    public class CpuTracker
    {
        private readonly Dictionary<int, (TimeSpan cpuTime, DateTime sampleTime)> _history = new();

        public float GetCpuUsage(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var currentCpuTime = proc.TotalProcessorTime;
                var currentSampleTime = DateTime.UtcNow;

                if (_history.TryGetValue(pid, out var last))
                {
                    var cpuDiff = currentCpuTime - last.cpuTime;
                    var timeDiff = currentSampleTime - last.sampleTime;

                    _history[pid] = (currentCpuTime, currentSampleTime);

                    if (timeDiff.TotalMilliseconds <= 0) return 0f;

                    double usage = (cpuDiff.TotalMilliseconds / timeDiff.TotalMilliseconds) * 100.0 / Environment.ProcessorCount;
                    return (float)Math.Clamp(usage, 0.0, 100.0);
                }
                else
                {
                    _history[pid] = (currentCpuTime, currentSampleTime);
                    return 0f;
                }
            }
            catch
            {
                return 0f;
            }
        }

        public void CleanOrphans(HashSet<int> activePids)
        {
            var keysToRemove = _history.Keys.Where(k => !activePids.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                _history.Remove(key);
            }
        }
    }

    public static class ProcessHelper
    {
        private static readonly Dictionary<int, string> _ownerCache = new();

        public static string GetProcessOwner(int pid)
        {
            if (pid <= 0) return "-";
            lock (_ownerCache)
            {
                if (_ownerCache.TryGetValue(pid, out var cached))
                    return cached;
            }

            string owner = QueryProcessOwner(pid);
            lock (_ownerCache)
            {
                _ownerCache[pid] = owner;
            }
            return owner;
        }

        public static void CleanOwnerCache(HashSet<int> activePids)
        {
            lock (_ownerCache)
            {
                var keysToRemove = _ownerCache.Keys.Where(k => !activePids.Contains(k)).ToList();
                foreach (var k in keysToRemove)
                {
                    _ownerCache.Remove(k);
                }
            }
        }

        private static string QueryProcessOwner(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ProcessId = {pid}");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    var args = new string[2];
                    int returnCode = Convert.ToInt32(obj.InvokeMethod("GetOwner", (object[])args));
                    if (returnCode == 0)
                    {
                        string user = args[0];
                        if (!string.IsNullOrEmpty(user)) return user;
                    }

                    string cmd = obj["CommandLine"]?.ToString() ?? "";
                    string name = obj["Name"]?.ToString() ?? "";
                    string exePath = obj["ExecutablePath"]?.ToString() ?? "";
                    if (cmd.Contains(@"C:\Users\llm", StringComparison.OrdinalIgnoreCase) ||
                        exePath.Contains(@"C:\Users\llm", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("LM Studio", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("lms.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return "llm";
                    }
                }
            }
            catch { }
            return "SYSTEM";
        }

        public static List<ProcessSnapshot> GetProcessSnapshots()
        {
            var list = new List<ProcessSnapshot>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    try
                    {
                        int pid = Convert.ToInt32(obj["ProcessId"]);
                        int ppid = Convert.ToInt32(obj["ParentProcessId"]);
                        string name = obj["Name"]?.ToString() ?? "";
                        string cmd = obj["CommandLine"]?.ToString() ?? "";

                        list.Add(new ProcessSnapshot
                        {
                            Pid = pid,
                            ParentPid = ppid,
                            Name = name,
                            CmdLine = cmd
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText("wmi_error.txt", ex.ToString() + "\n"); } catch { }
            }
            return list;
        }

        public static Dictionary<int, int> GetListeningPortsPids()
        {
            var map = new Dictionary<int, int>();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c netstat -ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Contains("LISTENING"))
                        {
                            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 5)
                            {
                                var localAddress = parts[1];
                                int portColonIdx = localAddress.LastIndexOf(':');
                                if (portColonIdx >= 0)
                                {
                                    string portStr = localAddress.Substring(portColonIdx + 1);
                                    if (int.TryParse(portStr, out int port) && int.TryParse(parts[parts.Length - 1], out int pid))
                                    {
                                        map[port] = pid;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText("netstat_error.txt", ex.ToString() + "\n"); } catch { }
            }
            return map;
        }

        public static (float cpu, float memMb) GetProcessResources(int rootPid, List<ProcessSnapshot> snapshots, CpuTracker cpuTracker)
        {
            var pidsToQuery = new HashSet<int> { rootPid };
            FindChildPids(rootPid, snapshots, pidsToQuery);

            float totalCpu = 0f;
            float totalMem = 0f;

            foreach (var pid in pidsToQuery)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    totalMem += (float)(proc.WorkingSet64 / (1024.0 * 1024.0));
                    totalCpu += cpuTracker.GetCpuUsage(pid);
                }
                catch { }
            }

            return (totalCpu, totalMem);
        }

        private static void FindChildPids(int parentPid, List<ProcessSnapshot> snapshots, HashSet<int> accumulator)
        {
            foreach (var snap in snapshots)
            {
                if (snap.ParentPid == parentPid && !accumulator.Contains(snap.Pid))
                {
                    accumulator.Add(snap.Pid);
                    FindChildPids(snap.Pid, snapshots, accumulator);
                }
            }
        }
    }
}
