using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RaudelControlPanelLite
{
    public class MainForm : Form
    {

    

        // ── Constants ──
        private const string STARTUP_SHORTCUT_NAME = "RaudelControlPanelLite.lnk";
        private const int DEFAULT_REFRESH_MS = 1500;

        // ── State ──
        private List<ServiceItem> _services = new();
        private readonly CpuTracker _cpuTracker = new();
        private readonly Dictionary<string, Process> _runningProcesses = new();
        private bool _stopThreads;
        private NotifyIcon? _trayIcon;
        private bool _isRegistered;
        private int _refreshIntervalMs = DEFAULT_REFRESH_MS;
        private readonly string[] _launchArgs;
        private string _configDirectory = "";

        // ── UI Controls ──
        private ListView _lvServices = null!;
        private Label _lblMetrics = null!;
        private Button _btnStartAll = null!;
        private Button _btnKillAll = null!;
        private Button _btnStart = null!;
        private Button _btnKill = null!;
        private Button _btnProperties = null!;
        private Dictionary<string, DateTime> _startingServices = new();
        private Button _btnSettings = null!;
        private Button _btnAdd = null!;
        private Button _btnImportDocker = null!;
        private ListBox _lbLog = null!;
        private Label _lblShortcutStatus = null!;
        private Button _btnRegisterPanel = null!;
        private EventWaitHandle? _showEvent;

        // ── Docker integration state ──
        private readonly List<ServiceItem> _dockerContainers = new();
        private readonly Dictionary<string, bool> _dockerRunning = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (float Cpu, float Mem)> _dockerStats = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _svcContainers = new(StringComparer.OrdinalIgnoreCase);
        private bool _svcContainersDirty = true;
        private bool _autoListDocker;
        private DateTime _lastDockerRefresh = DateTime.MinValue;
        private bool _pendingDynamicRebuild;
        private string _servicesFingerprint = "";

        public MainForm(string[] args) { try {
            _launchArgs = args;
            InitializeUI();
            ResolveConfigDirectory();
            LoadConfig();
            PopulateListView();
            LoadSettings();

            try
            {
                var _ = Handle; // Ensure Win32 window handle exists even if started minimized
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "RaudelControlPanelLite_ShowEvent");
                Task.Run(() =>
                {
                    while (!_stopThreads)
                    {
                        if (_showEvent.WaitOne(1000))
                        {
                            if (!_stopThreads && !IsDisposed && IsHandleCreated)
                            {
                                try { BeginInvoke(() => RestoreFromTray()); } catch { }
                            }
                        }
                    }
                });
            }
            catch { }

            Task.Run(StatusCheckerLoop);

            if (args.Contains("--startup") || args.Contains("--minimized"))
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                StartStartupServices();
                CreateTrayIcon(); } } catch (Exception ex) { System.IO.File.WriteAllText("crash.log", ex.ToString()); } }

        // ══════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ══════════════════════════════════════════════════════════════

        private void InitializeUI()
        {
            Text = "Raudeland Infrastructure Control Panel";
            ClientSize = new Size(780, 520);
            MinimumSize = new Size(750, 450);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;

            // ── Metrics label ──
            _lblMetrics = new Label
            {
                Text = "Services: 0/0  |  CPU: 0.0%  |  RAM: 0.0 MB",
                Location = new Point(12, 10),
                AutoSize = true
            };

            // ── Buttons ──
            _btnStart = new Button { Text = "Start", Size = new Size(80, 25), Location = new Point(12, 32), Enabled = false };
            _btnKill = new Button { Text = "Kill", Size = new Size(80, 25), Location = new Point(98, 32), Enabled = false };
            _btnProperties = new Button { Text = "Properties", Size = new Size(80, 25), Location = new Point(184, 32), Enabled = false };
            _btnProperties.Click += (s, e) => OpenPropertiesDialog();

            _btnStart.Click += CtxStart_Click;
            _btnKill.Click += CtxKill_Click;

            var _sep = new Label { AutoSize = false, Size = new Size(2, 25), Location = new Point(272, 32), BorderStyle = BorderStyle.Fixed3D };

            _btnStartAll = new Button { Text = "Start All", Size = new Size(80, 25), Location = new Point(282, 32) };
            _btnStartAll.Click += BtnStartAll_Click;

            _btnKillAll = new Button { Text = "Kill All", Size = new Size(80, 25), Location = new Point(368, 32) };
            _btnKillAll.Click += BtnKillAll_Click;

            _btnAdd = new Button { Text = "Add", Size = new Size(60, 25), Location = new Point(454, 32) };
            _btnAdd.Click += BtnAdd_Click;

            _btnImportDocker = new Button { Text = "Import Docker", Size = new Size(110, 25), Location = new Point(520, 32) };
            _btnImportDocker.Click += BtnImportDocker_Click;

            _btnSettings = new Button { Text = "Settings", Size = new Size(80, 25), Location = new Point(688, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnSettings.Click += BtnSettings_Click;


            // ── ListView ──
            _lvServices = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Location = new Point(12, 64),
                Size = new Size(756, 310),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                CheckBoxes = true
            };
            
            // Fix WinForms ListView flickering using reflection to enable DoubleBuffered
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                           ?.SetValue(_lvServices, true, null);
            

            _lvServices.Columns.Add("Service Name", 180);
            _lvServices.Columns.Add("Type", 60);
            _lvServices.Columns.Add("Status", 60);
            _lvServices.Columns.Add("User", 75);
            _lvServices.Columns.Add("Port", 90);
            _lvServices.Columns.Add("CPU", 65);
            _lvServices.Columns.Add("RAM", 80);
            _lvServices.Columns.Add("PIDs", 80);
            _lvServices.Columns.Add("ID", 130);
            _lvServices.Columns.Add("Directory", 260);

            _lvServices.ColumnClick += LvServices_ColumnClick;
            _lvServices.ItemCheck += LvServices_ItemCheck;
            _lvServices.MouseDoubleClick += LvServices_MouseDoubleClick;

            _lvServices.SelectedIndexChanged += (s, e) => {
                if (_lvServices.SelectedItems.Count > 0) {
                    var svc = _lvServices.SelectedItems[0].Tag as ServiceItem;
                    if (svc != null) {
                        _btnStart.Enabled = !svc.IsOnline;
                        _btnKill.Enabled = svc.IsOnline;
                        _btnProperties.Enabled = true;
                    }
                } else {
                    _btnStart.Enabled = false;
                    _btnKill.Enabled = false;
                    _btnProperties.Enabled = false;
                }
            };


            // ── Context menu for per-service actions ──
            var ctxMenu = new ContextMenuStrip();
            ctxMenu.Items.Add("Start Service", null, CtxStart_Click);
            ctxMenu.Items.Add("Kill Service", null, CtxKill_Click);
            ctxMenu.Items.Add("-");
            ctxMenu.Items.Add("Open Directory", null, CtxExplore_Click);
            ctxMenu.Items.Add("-");
            ctxMenu.Items.Add("Properties", null, (s, e) => OpenPropertiesDialog());

            _lvServices.ContextMenuStrip = ctxMenu;

            // ── Log box ──
            _lbLog = new ListBox
            {
                Location = new Point(12, 380),
                Size = new Size(756, 128),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Font = new Font("Consolas", 8f),
                HorizontalScrollbar = true
            };

            // ── Footer ──
            _lblShortcutStatus = new Label
            {
                Text = "Checking startup registration...",
                Location = new Point(12, 468),
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            _btnRegisterPanel = new Button
            {
                Text = "Register Startup",
                Size = new Size(120, 25),
                Location = new Point(648, 465),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            

            // ── Assemble ──
            Controls.AddRange(new Control[]
            {
                _lblMetrics, _btnStartAll, _btnKillAll, _btnSettings,
                _btnAdd, _btnImportDocker,
                _lvServices, _lbLog,
                _btnStart, _btnKill, _btnProperties, _sep
            });

            // Expand window to new size, triggering anchors to stretch the list
            ClientSize = new Size(940, 620);

            
        }

        // ══════════════════════════════════════════════════════════════
        //  CONFIG MANAGEMENT
        // ══════════════════════════════════════════════════════════════

        private void ResolveConfigDirectory()
        {
            string[] candidates = new[]
            {
                @"C:\Users\T450\Utilities\System",
                @"C:\Users\T450\Utilities\RaudelControlPanel\publish",
                @"C:\Users\T450\Utilities\RaudelControlPanel\bin\Debug\net10.0-windows",
                @"C:\Users\T450\Utilities\RaudelControlPanel\bin\Release\net10.0-windows",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "RaudelControlPanel", "publish"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "RaudelControlPanel", "bin", "Debug", "net10.0-windows"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "RaudelControlPanel", "bin", "Release", "net10.0-windows"),
            };

            foreach (var dir in candidates)
            {
                string configPath = Path.Combine(dir, "services_config.json");
                if (File.Exists(configPath))
                {
                    _configDirectory = dir;
                    AppendLog($"Shared config: {configPath}");
                    return;
                }
            }

            _configDirectory = AppDomain.CurrentDomain.BaseDirectory;
            AppendLog($"Using own directory: {_configDirectory}");
        }

        private void LoadConfig()
        {
            try {

                string configPath = Path.Combine(_configDirectory, "services_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var list = JsonSerializer.Deserialize<List<ServiceItem>>(json);
                    if (list != null)
                    {
                        foreach (var svc in list)
                        {
                            SyncStartupRegistration(svc);
                        }

                        var cf = list.FirstOrDefault(s => s.Id == "cloudflare_tunnel");
                        if (cf != null) _services.Add(cf);
                        foreach (var svc in list)
                        {
                            if (svc.Id != "cloudflare_tunnel") _services.Add(svc);
                        }
                        
                        AppendLog($"Loaded {_services.Count} services.");
                        
                        // Sort by RunOnStartup then by Name
                        _services = _services.OrderByDescending(s => s.RunOnStartup).ThenBy(s => s.Name).ToList();
                        UpdateServicesFingerprint();
                    }
                }
                else
                {
                    AppendLog("No services_config.json found.");
                }
            }
            catch (Exception ex) { AppendLog($"Config error: {ex.Message}"); }
        }

        private bool? IsScheduledTaskEnabled(string taskName)
        {
            try
            {
                string taskPath = Path.Combine(@"C:\Windows\System32\Tasks", taskName);
                if (File.Exists(taskPath))
                {
                    string xml = File.ReadAllText(taskPath);
                    int settingsIdx = xml.IndexOf("<Settings>", StringComparison.OrdinalIgnoreCase);
                    if (settingsIdx >= 0)
                    {
                        string settingsSection = xml.Substring(settingsIdx);
                        if (settingsSection.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase))
                            return false;
                        if (settingsSection.Contains("<Enabled>true</Enabled>", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }
            return null;
        }

        private void SyncStartupRegistration(ServiceItem svc)
        {
            try
            {
                string flag = svc.RunOnStartup ? "/ENABLE" : "/DISABLE";
                if (svc.Id == "antigravity_admin" || svc.Id == "antigravity_desktop_admin")
                {
                    RunSchtasksChange(@"\Antigravity_Desktop_Admin", flag);
                }
                else if (svc.Id == "lmstudio_telegram_bot" || svc.Id == "lmstudio_bot")
                {
                    RunSchtasksChange(@"\RunLLMBotOnBoot", flag);
                    RunSchtasksChange(@"\RunBotLLM", flag);
                }
            }
            catch (Exception ex) { AppendLog($"Task sync error: {ex.Message}"); }
        }

        private void RunSchtasksChange(string taskName, string enableFlag)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{taskName}\" {enableFlag}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_services, options);

                string configPath = Path.Combine(_configDirectory, "services_config.json");
                File.WriteAllText(configPath, json);

                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "services_config.json");
                if (!string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.WriteAllText(localPath, json); } catch { }
                }
            }
            catch (Exception ex) { AppendLog($"Save error: {ex.Message}"); }
        }

        private void LoadSettings()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lite_settings.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<LiteSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (settings != null)
                    {
                        _refreshIntervalMs = Math.Clamp(settings.RefreshIntervalMs, 500, 30000);
                        _autoListDocker = settings.AutoListDocker;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lite_settings.json");
                var settings = new LiteSettings { RefreshIntervalMs = _refreshIntervalMs, AutoListDocker = _autoListDocker };
                File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private sealed class LiteSettings
        {
            [JsonPropertyName("refresh_interval_ms")]
            public int RefreshIntervalMs { get; set; } = DEFAULT_REFRESH_MS;

            [JsonPropertyName("auto_list_docker")]
            public bool AutoListDocker { get; set; }
        }

        private void AppendLog(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            if (_lbLog != null && _lbLog.IsHandleCreated)
            {
                if (InvokeRequired)
                    Invoke(() => {
                        _lbLog.Items.Add(line);
                        if (_lbLog.Items.Count > 100) _lbLog.Items.RemoveAt(0);
                        _lbLog.TopIndex = _lbLog.Items.Count - 1;
                    });
                else
                    { _lbLog.Items.Add(line); _lbLog.TopIndex = _lbLog.Items.Count - 1; }
            }

            // Also write to file
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_lite.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════
        //  LISTVIEW POPULATION
        // ══════════════════════════════════════════════════════════════

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SyncListViewCheckboxes();
            _uiReady = true;

            Task.Run(() =>
            {
                SyncDockerStartupStates();
                if (!_stopThreads && !IsDisposed && IsHandleCreated)
                {
                    try { BeginInvoke(SyncListViewCheckboxes); } catch { }
                }
            });
        }

        /// <summary>
        /// Aligns each Docker-backed service's startup flag (and therefore its checkbox)
        /// with the restart policy configured on its container(s).
        /// </summary>
        private void SyncDockerStartupStates()
        {
            bool changed = false;

            foreach (var svc in _services)
            {
                if (!DockerHelper.IsDockerService(svc)) continue;

                bool? enabled = DockerHelper.GetStartupEnabled(svc);
                if (!enabled.HasValue || svc.RunOnStartup == enabled.Value) continue;

                svc.RunOnStartup = enabled.Value;
                changed = true;
                AppendLog($"Docker startup for {svc.Name}: {(enabled.Value ? "Enabled" : "Disabled")} (from container config)");
            }

            if (changed) SaveConfig();
        }

        private void SyncListViewCheckboxes()
        {
            _suppressItemCheck = true;
            try
            {
                foreach (ListViewItem lvi in _lvServices.Items)
                {
                    if (lvi.Tag is ServiceItem svc && lvi.Checked != svc.RunOnStartup)
                    {
                        lvi.Checked = svc.RunOnStartup;
                    }
                }
            }
            finally
            {
                _suppressItemCheck = false;
            }
        }

        private void PopulateListView()
        {
            _suppressItemCheck = true;
            try
            {
                _lvServices.Items.Clear();
                foreach (var svc in GetSortedViewServices())
                {
                    var lvi = new ListViewItem(svc.Name) { Tag = svc, Checked = svc.RunOnStartup };
                    lvi.SubItems.Add(svc.TypeDisplay);
                    lvi.SubItems.Add(svc.IsOnline ? "Online" : "Offline");
                    lvi.SubItems.Add(svc.User);
                    lvi.SubItems.Add(svc.PortDisplayString);
                    lvi.SubItems.Add("-");
                    lvi.SubItems.Add("-");
                    lvi.SubItems.Add(svc.PidsString);
                    lvi.SubItems.Add(svc.Id);
                    lvi.SubItems.Add(svc.DisplayDirectory);
                    UpdateRowColors(lvi, svc);
                    _lvServices.Items.Add(lvi);
                    lvi.Checked = svc.RunOnStartup;
                }
            }
            finally
            {
                _suppressItemCheck = false;
            }
        }

        private List<ServiceItem> GetViewServices()
        {
            if (!_autoListDocker || _dockerContainers.Count == 0)
                return _services;

            var result = new List<ServiceItem>(_services);
            result.AddRange(_dockerContainers);
            return result;
        }

        private List<ServiceItem> GetSortedViewServices()
        {
            var view = GetViewServices();
            if (string.IsNullOrEmpty(_sortField)) return view;

            return _sortField switch
            {
                "Name" => _sortAscending ? view.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : view.OrderByDescending(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                "Type" => _sortAscending ? view.OrderBy(s => s.TypeDisplay, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : view.OrderByDescending(s => s.TypeDisplay, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                "Status" => _sortAscending ? view.OrderByDescending(s => s.IsOnline).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : view.OrderBy(s => s.IsOnline).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                "User" => _sortAscending ? view.OrderBy(s => s.User == "-" ? "zzz" : s.User, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : view.OrderByDescending(s => s.User == "-" ? " " : s.User, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                "Port" => _sortAscending ? view.OrderBy(s => s.Port ?? int.MaxValue).ToList() : view.OrderByDescending(s => s.Port ?? -1).ToList(),
                "CPU" => _sortAscending ? view.OrderByDescending(s => s.IsOnline).ThenBy(s => s.Cpu).ToList() : view.OrderByDescending(s => s.IsOnline).ThenByDescending(s => s.Cpu).ToList(),
                "RAM" => _sortAscending ? view.OrderByDescending(s => s.IsOnline).ThenBy(s => s.Mem).ToList() : view.OrderByDescending(s => s.IsOnline).ThenByDescending(s => s.Mem).ToList(),
                "ID" => _sortAscending ? view.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList() : view.OrderByDescending(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => view
            };
        }

        private void UpdateServicesFingerprint()
        {
            _servicesFingerprint = string.Join("|", _services.Select(s => s.Id + ":" + s.Command));
            _svcContainersDirty = true;
        }

        private void RefreshDockerContainers()
        {
            try
            {
                var containers = DockerHelper.ListContainers(includeStopped: true);

                _dockerRunning.Clear();
                _dockerStats.Clear();
                foreach (var c in containers) _dockerRunning[c.Name] = c.Running;
                foreach (var (name, cpu, mem) in DockerHelper.GetContainerStats()) _dockerStats[name] = (cpu, mem);

                // Recompute service->containers mapping only when the configured list changes.
                string fingerprint = string.Join("|", _services.Select(s => s.Id + ":" + s.Command));
                if (fingerprint != _servicesFingerprint)
                {
                    _servicesFingerprint = fingerprint;
                    _svcContainersDirty = true;
                }

                if (_svcContainersDirty)
                {
                    _svcContainersDirty = false;
                    _svcContainers.Clear();
                    foreach (var svc in _services)
                    {
                        var names = DockerHelper.ResolveContainers(svc);
                        if (names.Count > 0) _svcContainers[svc.Id] = names;
                    }
                }

                var configuredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var names in _svcContainers.Values)
                    foreach (var n in names) configuredNames.Add(n);

                var previous = string.Join(",", _dockerContainers.Select(s => s.Id));

                _dockerContainers.Clear();
                if (_autoListDocker)
                {
                    foreach (var c in containers)
                    {
                        if (configuredNames.Contains(c.Name)) continue;
                        _dockerContainers.Add(DockerContainerToService(c));
                    }
                }

                string current = string.Join(",", _dockerContainers.Select(s => s.Id));
                if (!string.Equals(previous, current, StringComparison.Ordinal))
                    _pendingDynamicRebuild = true;
            }
            catch { }
        }

        private ServiceItem DockerContainerToService(DockerContainer c)
        {
            bool startup = c.RestartPolicy.Equals("always", StringComparison.OrdinalIgnoreCase)
                        || c.RestartPolicy.Equals("unless-stopped", StringComparison.OrdinalIgnoreCase);

            string directory = "";
            if (!string.IsNullOrEmpty(c.ComposeFile))
                directory = Path.GetDirectoryName(c.ComposeFile) ?? "";
            if (string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(c.Image))
                directory = $"Image: {c.Image}";

            return new ServiceItem
            {
                Id = "docker:" + c.Name,
                Name = c.Name,
                Directory = directory,
                Command = "docker start " + c.Name,
                Port = c.PublishedPort,
                RunOnStartup = startup,
                DockerContainerName = c.Name,
                IsDockerContainer = true
            };
        }

        private void BtnAdd_Click(object? sender, EventArgs e)
        {
            using var dlg = new AddServiceForm();
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;

            var svc = dlg.Result;
            if (_services.Any(s => s.Id.Equals(svc.Id, StringComparison.OrdinalIgnoreCase)))
                svc.Id += "_" + Guid.NewGuid().ToString("N")[..6];

            _services.Add(svc);
            UpdateServicesFingerprint();
            SaveConfig();
            PopulateListView();
            AppendLog($"Added service: {svc.Name} ({svc.Id})");
        }

        private void BtnImportDocker_Click(object? sender, EventArgs e)
        {
            var containers = DockerHelper.ListContainers(includeStopped: true);
            if (containers.Count == 0)
            {
                AppendLog("No Docker containers found to import.");
                return;
            }

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var names in _svcContainers.Values)
                foreach (var n in names) existing.Add(n);
            foreach (var svc in _services)
            {
                if (!string.IsNullOrEmpty(svc.DockerContainerName)) existing.Add(svc.DockerContainerName);
                foreach (var n in DockerHelper.ResolveContainers(svc)) existing.Add(n);
            }

            int added = 0;
            foreach (var c in containers)
            {
                if (existing.Contains(c.Name)) continue;
                var svc = DockerContainerToService(c);
                svc.Id = Regex.Replace(c.Name.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
                if (string.IsNullOrEmpty(svc.Id)) svc.Id = "container_" + Guid.NewGuid().ToString("N")[..6];
                if (_services.Any(s => s.Id.Equals(svc.Id, StringComparison.OrdinalIgnoreCase)))
                    svc.Id += "_" + Guid.NewGuid().ToString("N")[..6];
                svc.IsDockerContainer = false; // persisted entry, not a live listing
                _services.Add(svc);
                added++;
            }

            if (added == 0)
            {
                AppendLog("No new Docker containers to import.");
                return;
            }

            UpdateServicesFingerprint();
            SaveConfig();
            PopulateListView();
            AppendLog($"Imported {added} Docker container(s).");
        }

        private void UpdateRowColors(ListViewItem lvi, ServiceItem svc)
        {
            if (lvi.Checked != svc.RunOnStartup)
            {
                _suppressItemCheck = true;
                lvi.Checked = svc.RunOnStartup;
                _suppressItemCheck = false;
            }

            if (svc.IsOnline) {
                lvi.ForeColor = Color.DarkGreen;
                lvi.SubItems[2].Text = "Online";
                lvi.SubItems[5].Text = $"{svc.Cpu:F1}%";
                lvi.SubItems[6].Text = $"{svc.Mem:F1} MB";
            } else if (svc.IsStarting) {
                lvi.ForeColor = Color.DarkGoldenrod;
                lvi.SubItems[2].Text = "Starting...";
                lvi.SubItems[5].Text = "-";
                lvi.SubItems[6].Text = "-";
            } else {
                lvi.ForeColor = Color.DarkRed;
                lvi.SubItems[2].Text = "Offline";
                lvi.SubItems[5].Text = "-";
                lvi.SubItems[6].Text = "-";
            }
            lvi.SubItems[3].Text = svc.User;
            lvi.SubItems[4].Text = svc.PortDisplayString;
            lvi.SubItems[7].Text = svc.PidsString;
            lvi.SubItems[8].Text = svc.Id;
            lvi.SubItems[9].Text = svc.DisplayDirectory;
        }

        // ══════════════════════════════════════════════════════════════
        //  LISTVIEW EVENTS
        // ══════════════════════════════════════════════════════════════

        private bool _suppressItemCheck;
        private bool _uiReady;

        private void LvServices_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            // ListView raises ItemCheck while its handle is being created; ignore those
            // initialization events so startup flags aren't re-applied as user changes.
            if (!_uiReady || _suppressItemCheck || !IsHandleCreated) return;
            bool newChecked = e.NewValue == CheckState.Checked;
            if (e.Index >= 0 && e.Index < _lvServices.Items.Count && _lvServices.Items[e.Index].Tag is ServiceItem svc)
            {
                svc.RunOnStartup = newChecked;

                if (DockerHelper.IsDockerService(svc))
                {
                    Task.Run(() =>
                    {
                        bool updated = DockerHelper.SetStartupEnabled(svc, newChecked);
                        bool? actual = updated ? null : DockerHelper.GetStartupEnabled(svc);

                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(() =>
                            {
                                if (updated)
                                {
                                    AppendLog($"Docker restart policy for {svc.Name}: {(newChecked ? "unless-stopped" : "no")}");
                                }
                                else
                                {
                                    // Keep the checkbox honest if Docker rejected the change.
                                    svc.RunOnStartup = actual ?? !newChecked;
                                    SyncListViewCheckboxes();
                                    AppendLog($"Could not update Docker startup for {svc.Name}; checkbox reverted.");
                                }
                                SaveConfig();
                            });
                        }
                        catch { }
                    });
                }
                else
                {
                    BeginInvoke(() =>
                    {
                        SyncStartupRegistration(svc);
                        SaveConfig();
                        AppendLog($"Startup for {svc.Name}: {(svc.RunOnStartup ? "Enabled" : "Disabled")}");
                    });
                }
            }
        }

        private void LvServices_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            if (_lvServices.SelectedItems.Count > 0)
            {
                var svc = _lvServices.SelectedItems[0].Tag as ServiceItem;
                if (svc != null) OpenExplorer(svc);
            }
        }

        private void LvServices_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            string field = e.Column switch
            {
                0 => "Name",
                1 => "Type",
                2 => "Status",
                3 => "User",
                4 => "Port",
                5 => "CPU",
                6 => "RAM",
                8 => "ID",
                _ => ""
            };
            if (!string.IsNullOrEmpty(field)) 
            {
                SortServices(field);
                
                string[] baseNames = { "Service Name", "Type", "Status", "User", "Port", "CPU", "RAM", "PIDs", "ID", "Directory" };
                for (int i = 0; i < _lvServices.Columns.Count; i++)
                {
                    if (i == e.Column)
                    {
                        bool showAscending = _sortAscending;
                        if (i == 0 || i == 2) showAscending = !_sortAscending; // Reverse arrow for Name and Status
                        _lvServices.Columns[i].Text = baseNames[i] + (showAscending ? " ▲" : " ▼");
                    }
                    else
                    {
                        _lvServices.Columns[i].Text = baseNames[i];
                    }
                }
            }
        }

        private string _sortField = "";
        private bool _sortAscending = true;

        private void SortServices(string field)
        {
            if (_sortField == field) _sortAscending = !_sortAscending;
            else { _sortField = field; _sortAscending = true; }

            // Sort the combined view (configured + live Docker) without mutating the
            // persisted _services list — GetSortedViewServices applies the order on render.
            _suppressItemCheck = true;
            PopulateListView();
            _suppressItemCheck = false;
        }

        // ── Context menu handlers ──

        private void CtxStart_Click(object? sender, EventArgs e)
        {
            if (_lvServices.SelectedItems.Count > 0)
            {
                var svc = _lvServices.SelectedItems[0].Tag as ServiceItem;
                if (svc != null && !svc.IsOnline) Task.Run(() => StartService(svc));
            }
        }

        private void CtxKill_Click(object? sender, EventArgs e)
        {
            if (_lvServices.SelectedItems.Count > 0)
            {
                var svc = _lvServices.SelectedItems[0].Tag as ServiceItem;
                if (svc != null && svc.IsOnline) Task.Run(() => KillService(svc));
            }
        }

        private void CtxExplore_Click(object? sender, EventArgs e)
        {
            if (_lvServices.SelectedItems.Count > 0)
            {
                var svc = _lvServices.SelectedItems[0].Tag as ServiceItem;
                if (svc != null) OpenExplorer(svc);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  STATUS CHECKER LOOP
        // ══════════════════════════════════════════════════════════════

        private async Task StatusCheckerLoop()
        {
            AppendLog("Status checker started.");
            while (!_stopThreads)
            {
                try
                {
                    var snapshots = ProcessHelper.GetProcessSnapshots();
                    var activePids = new HashSet<int>(snapshots.Select(s => s.Pid));
                    _cpuTracker.CleanOrphans(activePids);
                    ProcessHelper.CleanOwnerCache(activePids);
                    var portsMap = ProcessHelper.GetListeningPortsPids();

                    int onlineCount = 0;
                    float totalCpu = 0f, totalMem = 0f;

                    if ((DateTime.UtcNow - _lastDockerRefresh).TotalSeconds >= 4)
                    {
                        RefreshDockerContainers();
                        _lastDockerRefresh = DateTime.UtcNow;
                    }

                    var viewServices = GetViewServices();

                    foreach (var svc in viewServices)
                    {
                        bool online = false;
                        int rootPid = 0;
                        float cpu = 0f, mem = 0f;
                        string user = "-";
                        bool isDocker = DockerHelper.IsDockerService(svc);

                        if (isDocker)
                        {
                            List<string>? names = null;
                            if (svc.IsDockerContainer && !string.IsNullOrEmpty(svc.DockerContainerName))
                                names = new List<string> { svc.DockerContainerName };
                            else if (_svcContainers.TryGetValue(svc.Id, out var dn))
                                names = dn;

                            if (names is { Count: > 0 })
                            {
                                online = names.Any(n => _dockerRunning.TryGetValue(n, out var r) && r);
                                if (!online && svc.Port.HasValue && svc.Id != "bedrock_smp")
                                    online = CheckPortAlive(svc.Port.Value);

                                foreach (var n in names)
                                    if (_dockerStats.TryGetValue(n, out var st)) { cpu += st.Cpu; mem += st.Mem; }

                                svc.ActivePids = new List<int>();
                                svc.PidsString = names.Count > 0 ? $"{names.Count} ct" : "-";

                                // Show the real Windows owner of the published port (as before),
                                // rather than a generic "docker" label.
                                user = "-";
                                if (svc.Port.HasValue)
                                {
                                    int ownerPid = 0;
                                    if (portsMap.TryGetValue(svc.Port.Value, out int pid)) ownerPid = pid;
                                    else ownerPid = FindPidByNetstat(svc.Port.Value) ?? 0;
                                    if (ownerPid > 0) user = ProcessHelper.GetProcessOwner(ownerPid);
                                }
                            }
                            else
                            {
                                if (svc.Port.HasValue && svc.Id != "bedrock_smp")
                                {
                                    if (portsMap.TryGetValue(svc.Port.Value, out int pid)) { online = true; rootPid = pid; }
                                    else { online = CheckPortAlive(svc.Port.Value); if (online) rootPid = FindPidByNetstat(svc.Port.Value) ?? 0; }
                                }
                                svc.ActivePids = new List<int> { rootPid };
                            }
                        }
                        else
                        {
                            if (svc.Port.HasValue && svc.Id != "bedrock_smp")
                            {
                                if (portsMap.TryGetValue(svc.Port.Value, out int pid)) { online = true; rootPid = pid; svc.ActivePids = new List<int>{pid}; }
                                else { online = CheckPortAlive(svc.Port.Value); if (online) rootPid = FindPidByNetstat(svc.Port.Value) ?? 0; svc.ActivePids = new List<int>{rootPid}; }
                            }
                            else
                            {
                                (online, rootPid) = CheckProcessAliveCached(svc.Id, snapshots);
                                svc.ActivePids = new List<int>{rootPid};
                            }
                        }

                        if (!online)
                        {
                            lock (_runningProcesses)
                            {
                                if (_runningProcesses.TryGetValue(svc.Id, out var p))
                                {
                                    try { if (!p.HasExited) { online = true; rootPid = p.Id; svc.ActivePids = new List<int>{p.Id}; } } catch { }
                                }
                            }
                        }

                        if (!online && _startingServices.TryGetValue(svc.Id, out var startTime)) {
                            if ((DateTime.UtcNow - startTime).TotalSeconds < 10) {
                                svc.IsStarting = true;
                            } else {
                                svc.IsStarting = false;
                                _startingServices.Remove(svc.Id);
                            }
                        } else if (online) {
                            svc.IsStarting = false;
                            _startingServices.Remove(svc.Id);
                        } else {
                            svc.IsStarting = false;
                        }

                        if (!isDocker)
                            svc.PidsString = svc.ActivePids.Count > 1 ? $"{svc.ActivePids[0]}..." : (svc.ActivePids.Count == 1 && svc.ActivePids[0] != 0 ? svc.ActivePids[0].ToString() : "-");

                        if (online)
                        {
                            onlineCount++;
                            if (!isDocker && rootPid > 0)
                            {
                                (cpu, mem) = ProcessHelper.GetProcessResources(rootPid, snapshots, _cpuTracker);
                                totalCpu += cpu; totalMem += mem;
                                user = ProcessHelper.GetProcessOwner(rootPid);
                            }
                            else if (isDocker)
                            {
                                totalCpu += cpu; totalMem += mem;
                                if (string.IsNullOrEmpty(user) || user == "-") user = "-";
                            }
                        }

                        svc.IsOnline = online;
                        svc.Cpu = cpu;
                        svc.Mem = mem;
                        svc.User = user;
                    }

                    if (!_stopThreads && !IsDisposed)
                    {
                        try
                        {
                            Invoke(() =>
                            {
                                if (_pendingDynamicRebuild)
                                {
                                    _pendingDynamicRebuild = false;
                                    PopulateListView();
                                    SyncListViewCheckboxes();
                                }

                                float vmmemMb = 0f;
                                try {
                                    foreach (var p in Process.GetProcessesByName("vmmem")) {
                                        vmmemMb += (float)(p.WorkingSet64 / (1024.0 * 1024.0));
                                    }
                                    foreach (var p in Process.GetProcessesByName("vmmemWSL")) {
                                        vmmemMb += (float)(p.WorkingSet64 / (1024.0 * 1024.0));
                                    }
                                } catch {}
                                
                                  _lblMetrics.Text = $"Services: {onlineCount}/{viewServices.Count}  |  CPU: {totalCpu:F1}%  |  RAM: {totalMem:F1} MB  |  Docker/WSL (Vmmem): {vmmemMb:F1} MB";

                                _lvServices.BeginUpdate();
                                  foreach (ListViewItem lvi in _lvServices.Items)
                                  {
                                      if (lvi.Tag as ServiceItem is ServiceItem svc)
                                          UpdateRowColors(lvi, svc);
                                  }
                                  _lvServices.EndUpdate();
                            });
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (InvalidOperationException) { break; }
                    }
                }
                catch (Exception ex) { AppendLog($"Checker error: {ex.Message}"); }

                await Task.Delay(_refreshIntervalMs);
            }
        }

        private bool CheckPortAlive(int port)
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect("127.0.0.1", port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(150);
                if (success) { client.EndConnect(result); return true; }
            }
            catch { }
            return false;
        }

        private int? FindPidByNetstat(int port)
        {
            try
            {
                var portsMap = ProcessHelper.GetListeningPortsPids();
                if (portsMap.TryGetValue(port, out int pid)) return pid;
            }
            catch { }
            return null;
        }

        private (bool online, int pid) CheckProcessAliveCached(string svcId, List<ProcessSnapshot> snapshots)
        {
            string cmdSub = "", procName = "";
            if (svcId == "cloudflare_tunnel") procName = "cloudflared.exe";
            else if (svcId == "playit_monitor") cmdSub = "Start-Playit-Monitor.ps1";
            else if (svcId == "playit" || svcId == "playit_gg") procName = "playit";
            else if (svcId == "mc_watchdog") cmdSub = "priority_watchdog.ps1";
            else if (svcId == "bitchat_peer") procName = "bitchat-tui";
            else if (svcId == "bedrock_smp") cmdSub = "Start-Server-Telegram.ps1";
            else if (svcId == "telegram_agent_tunnel" || svcId == "telegram_agent" || svcId == "telegram_antigravity_tunnel") cmdSub = "telegram_antigravity_agent_tunnel.py";
            else if (svcId == "hermes_journal" || svcId == "hermes" || svcId == "hermes_agent") cmdSub = "hermes";
            else if (svcId == "lmstudio_telegram_bot" || svcId == "lmstudio_bot") cmdSub = "lmstudio_telegram_bot.py";
            else if (svcId == "localsend") procName = "localsend_app.exe";
            else if (svcId == "antigravity_admin" || svcId == "antigravity_desktop_admin") procName = "Antigravity.exe";
            else if (svcId == "waha_agy_bridge" || svcId == "waha_bridge" || svcId == "whatsapp_antigravity_bridge") cmdSub = "agy_bridge.py";
            else return (false, 0);

            foreach (var snap in snapshots)
            {
                if (!string.IsNullOrEmpty(procName) && snap.Name.Contains(procName, StringComparison.OrdinalIgnoreCase)) return (true, snap.Pid);
                if (!string.IsNullOrEmpty(cmdSub) && snap.CmdLine.Contains(cmdSub, StringComparison.OrdinalIgnoreCase)) return (true, snap.Pid);
                
                // Debug log for Playit
                if (snap.Name.Contains("powershell", StringComparison.OrdinalIgnoreCase) && svcId == "playit_monitor")
                {
                    try { File.AppendAllText("cmdline_debug.txt", $"PID: {snap.Pid}, CmdLine: '{snap.CmdLine}'\n"); } catch { }
                }
            }
            return (false, 0);
        }

        // ══════════════════════════════════════════════════════════════
        //  SERVICE CONTROLS
        // ══════════════════════════════════════════════════════════════

        private void StartService(ServiceItem svc)
        {
            _startingServices[svc.Id] = DateTime.UtcNow;
            svc.IsStarting = true;

            if (DockerHelper.IsDockerService(svc))
            {
                AppendLog($"Starting {svc.Name} (docker)...");
                bool ok = DockerHelper.StartContainer(svc);
                AppendLog(ok ? $"Started {svc.Name}." : $"Failed to start {svc.Name}.");
                return;
            }

            if (string.IsNullOrEmpty(svc.Command)) return;
            AppendLog($"Starting {svc.Name}...");
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {svc.Command}",
                    WorkingDirectory = string.IsNullOrEmpty(svc.Directory) ? null : svc.Directory,
                    WindowStyle = svc.Id == "bitchat_peer" ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                var p = Process.Start(startInfo);
                if (p != null)
                {
                    lock (_runningProcesses) { _runningProcesses[svc.Id] = p; }
                    AppendLog($"Started {svc.Name} (PID {p.Id})");
                }
            }
            catch (Exception ex) { AppendLog($"Error starting {svc.Name}: {ex.Message}"); }
        }

        private void KillService(ServiceItem svc)
        {
            AppendLog($"Killing {svc.Name}...");

            if (DockerHelper.IsDockerService(svc))
            {
                bool ok = DockerHelper.StopContainer(svc);
                AppendLog(ok ? $"Killed {svc.Name}." : $"Failed to kill {svc.Name}.");
                return;
            }

            if (svc.Port.HasValue)
            {
                try
                {
                    var portsMap = ProcessHelper.GetListeningPortsPids();
                    if (portsMap.TryGetValue(svc.Port.Value, out int pid)) RunTaskKill(pid);
                }
                catch { }
            }

            string[] procNames = Array.Empty<string>();
            string[] cmdSubs = Array.Empty<string>();

            if (svc.Id == "cloudflare_tunnel") procNames = new[] { "cloudflared.exe" };
            else if (svc.Id == "playit_monitor") cmdSubs = new[] { "Start-Playit-Monitor.ps1" };
            else if (svc.Id == "playit" || svc.Id == "playit_gg") procNames = new[] { "playit-windows-x86_64-signed.exe", "playit.exe" };
            else if (svc.Id == "mc_watchdog") cmdSubs = new[] { "priority_watchdog.ps1" };
            else if (svc.Id == "bitchat_peer") procNames = new[] { "bitchat-tui" };
            else if (svc.Id == "bedrock_smp") { cmdSubs = new[] { "Start-Server-Telegram.ps1" }; procNames = new[] { "bedrock_server.exe" }; }
            else if (svc.Id == "telegram_agent_tunnel" || svc.Id == "telegram_agent" || svc.Id == "telegram_antigravity_tunnel") cmdSubs = new[] { "telegram_agent_bot.py", "telegram_antigravity_agent_tunnel.py" };
            else if (svc.Id == "hermes_journal" || svc.Id == "hermes" || svc.Id == "hermes_agent") cmdSubs = new[] { "hermes", "start-gateway.bat" };
            else if (svc.Id == "lmstudio_telegram_bot" || svc.Id == "lmstudio_bot") cmdSubs = new[] { "lmstudio_telegram_bot.py" };
            else if (svc.Id == "lm_studio") procNames = new[] { "LM Studio.exe", "lms.exe" };
            else if (svc.Id == "localsend") procNames = new[] { "localsend_app.exe" };
            else if (svc.Id == "antigravity_admin" || svc.Id == "antigravity_desktop_admin") procNames = new[] { "Antigravity.exe" };
            else if (svc.Id == "waha_agy_bridge" || svc.Id == "waha_bridge" || svc.Id == "whatsapp_antigravity_bridge") cmdSubs = new[] { "agy_bridge.py" };
            else if (svc.Id == "waha")
            {
                try
                {
                    var psi = new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c docker stop waha", CreateNoWindow = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(5000);
                }
                catch { }
            }

            var snapshots = ProcessHelper.GetProcessSnapshots();
            foreach (var snap in snapshots)
            {
                bool matches = procNames.Any(n => snap.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                            || cmdSubs.Any(s => snap.CmdLine.Contains(s, StringComparison.OrdinalIgnoreCase));
                if (matches) RunTaskKill(snap.Pid);
            }

            lock (_runningProcesses)
            {
                if (_runningProcesses.TryGetValue(svc.Id, out var p))
                {
                    try { p.Kill(true); } catch { }
                    _runningProcesses.Remove(svc.Id);
                }
            }

            AppendLog($"Killed {svc.Name}.");
        }

        private void RunTaskKill(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.ProcessName.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    proc.ProcessName.Contains("wsl", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch { }
            try
            {
                var psi = new ProcessStartInfo { FileName = "taskkill", Arguments = $"/F /PID {pid}", CreateNoWindow = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch { }
        }

        private void OpenExplorer(ServiceItem svc)
        {
            if (string.IsNullOrEmpty(svc.Directory)) return;
            string targetPath = svc.Directory;
            if (!string.IsNullOrEmpty(svc.Command))
            {
                var matches = Regex.Matches(svc.Command, @"(?:[A-Za-z]:\\[^""]+?\.(?:exe|ps1|js|py|bat|cmd|json))|(?:""[A-Za-z]:\\\\[^""]+?\.(?:exe|ps1|js|py|bat|cmd|json)"")");
                foreach (Match match in matches)
                {
                    string path = match.Value.Trim('"');
                    if (File.Exists(path))
                    {
                        if (path.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                            path.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
                        { targetPath = path; continue; }
                        targetPath = path; break;
                    }
                }
            }
            try
            {
                if (File.Exists(targetPath)) Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{targetPath}\"", UseShellExecute = true });
                else if (Directory.Exists(svc.Directory)) Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{svc.Directory}\"", UseShellExecute = true });
            }
            catch (Exception ex) { AppendLog($"Explorer error: {ex.Message}"); }
        }

        private void OpenPropertiesDialog()
        {
            if (_lvServices.SelectedItems.Count > 0 && _lvServices.SelectedItems[0].Tag is ServiceItem svc)
            {
                using var dlg = new PropertiesForm(svc);
                dlg.ShowDialog(this);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  BUTTON HANDLERS
        // ══════════════════════════════════════════════════════════════

        private void BtnStartAll_Click(object? sender, EventArgs e)
        {
            foreach (var svc in GetViewServices()) { if (!svc.IsOnline) Task.Run(() => StartService(svc)); }
        }

        private void BtnKillAll_Click(object? sender, EventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to kill all services?", "Confirm Kill All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (MessageBox.Show("Are you really really sure?", "Final Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Stop) != DialogResult.Yes) return;
            foreach (var svc in GetViewServices()) { if (svc.IsOnline) Task.Run(() => KillService(svc)); }
        }

        private void BtnSettings_Click(object? sender, EventArgs e)
        {
            using var dlg = new SettingsForm(_refreshIntervalMs, _autoListDocker);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _refreshIntervalMs = dlg.RefreshIntervalMs;
                _autoListDocker = dlg.AutoListDocker;
                SaveSettings();
                _pendingDynamicRebuild = true;
                AppendLog($"Refresh interval set to {_refreshIntervalMs}ms; Docker auto-list {( _autoListDocker ? "on" : "off")}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  STARTUP SHORTCUT
        // ══════════════════════════════════════════════════════════════

        private void StartStartupServices()
        {
            var snapshots = ProcessHelper.GetProcessSnapshots();
            var portsMap = ProcessHelper.GetListeningPortsPids();
            AppendLog($"StartStartupServices: Found {snapshots.Count} processes and {portsMap.Count} listening ports.");
            foreach (var svc in _services)
            {
                if (svc.RunOnStartup)
                {
                    bool already = false;
                    if (svc.Port.HasValue && svc.Id != "bedrock_smp") {
                        already = portsMap.ContainsKey(svc.Port.Value) || CheckPortAlive(svc.Port.Value);
                        AppendLog($"Startup check for {svc.Name} (Port {svc.Port.Value}): already={already}");
                    }
                    else {
                        (already, _) = CheckProcessAliveCached(svc.Id, snapshots);
                        AppendLog($"Startup check for {svc.Name} (Process {svc.Id}): already={already}");
                    }
                    if (!already)
                    {
                        lock (_runningProcesses)
                        {
                            if (_runningProcesses.TryGetValue(svc.Id, out var p))
                            {
                                try { if (!p.HasExited) already = true; } catch { }
                            }
                        }
                    }

                    if (!already && !svc.IsOnline) {
                        AppendLog($"Decided to start {svc.Name} because it was not detected.");
                        Task.Run(() => StartService(svc));
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  SYSTEM TRAY
        // ══════════════════════════════════════════════════════════════

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; MinimizeToTray(); return; }
            base.OnFormClosing(e);
        }

        private void MinimizeToTray() { Hide(); CreateTrayIcon(); }

        private void CreateTrayIcon()
        {
            if (_trayIcon != null) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Panel", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Start All", null, (s, e) => BtnStartAll_Click(this, EventArgs.Empty));
            menu.Items.Add("Kill All", null, (s, e) => BtnKillAll_Click(this, EventArgs.Empty));
            menu.Items.Add("-");
            menu.Items.Add("Exit Completely", null, (s, e) => ExitCompletely());

            _trayIcon = new NotifyIcon
            {
                Icon = CreateDynamicTrayIcon(),
                Text = "Raudeland Control Panel Lite",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private Icon CreateDynamicTrayIcon()
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(SystemColors.Control);
                using var pen = new Pen(Color.DarkBlue, 3);
                g.DrawRectangle(pen, 2, 2, 60, 60);
                using var font = new Font("Consolas", 32, FontStyle.Bold);
                g.DrawString("R", font, Brushes.Black, 13, 6);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public void RestoreFromTray()
        {
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            TopMost = true;
            BringToFront();
            Activate();
            TopMost = false;
        }

        private void ExitCompletely()
        {
            _stopThreads = true;
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            Application.Exit();
        }
    }
}
