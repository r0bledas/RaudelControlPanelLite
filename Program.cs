namespace RaudelControlPanelLite;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_startup.log");
        string currentProcName = Process.GetCurrentProcess().ProcessName;
        int currentPid = Process.GetCurrentProcess().Id;

        if (currentProcName.Equals("RaudelControlPanelLite", StringComparison.OrdinalIgnoreCase))
        {
            var existing = Process.GetProcessesByName(currentProcName).Where(p => p.Id != currentPid).ToList();
            if (existing.Count > 0)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.Now}] Duplicate instance detected (PID {currentPid}), signaling existing and exiting. Existing PIDs: {string.Join(", ", existing.Select(p => p.Id))}\n"); } catch { }
                try
                {
                    if (EventWaitHandle.TryOpenExisting("RaudelControlPanelLite_ShowEvent", out var showEvent))
                    {
                        using (showEvent)
                        {
                            showEvent.Set();
                        }
                    }
                }
                catch { }
                return;
            }
        }

        try {
            File.AppendAllText(logPath, $"[{DateTime.Now}] Starting MainForm: PID={currentPid}, args={string.Join(" ", args)}\n");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new MainForm(args));
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
        }
    }
}
