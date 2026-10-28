using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace CodeAstrogator.Core
{
    /// <summary>A direct child process as seen by <see cref="ProcessTree.GetChildren"/>.</summary>
    public sealed class ChildProcessInfo
    {
        public int ProcessId { get; set; }
        public string Name { get; set; } = "";
        public string CommandLine { get; set; } = "";
    }

    /// <summary>
    /// Process-tree helpers for the CLI child. net472 has no <c>Process.Kill(entireProcessTree)</c>,
    /// and a bare <see cref="Process.Kill()"/> on <c>claude.exe</c> orphans everything it started —
    /// a running Bash/PowerShell command (and whatever that command launched) kept running after a
    /// turn was stopped.
    /// </summary>
    public static class ProcessTree
    {
        /// <summary>Kills a process and all its descendants (<c>taskkill /T /F</c>); falls back to a
        /// plain kill of the root when taskkill is unavailable. Blocks up to a few seconds — call it
        /// off the UI thread.</summary>
        public static void KillTree(int pid)
        {
            try
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (killer != null && killer.WaitForExit(5000))
                    return;
            }
            catch
            {
                // taskkill missing/blocked — fall through to the plain kill
            }

            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    p.Kill();
            }
            catch
            {
                // already gone
            }
        }

        /// <summary>Direct children of a process with their command lines (WMI). Empty on failure.</summary>
        public static IReadOnlyList<ChildProcessInfo> GetChildren(int parentPid)
        {
            var result = new List<ChildProcessInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE ParentProcessId = " + parentPid);
                using var items = searcher.Get();
                foreach (ManagementObject item in items)
                {
                    using (item)
                    {
                        result.Add(new ChildProcessInfo
                        {
                            ProcessId = Convert.ToInt32(item["ProcessId"]),
                            Name = item["Name"] as string ?? "",
                            CommandLine = item["CommandLine"] as string ?? "",
                        });
                    }
                }
            }
            catch
            {
                // WMI unavailable / access denied — caller treats "no children" as "not found"
            }
            return result;
        }
    }
}
