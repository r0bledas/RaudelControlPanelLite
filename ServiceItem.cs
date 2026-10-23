using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RaudelControlPanelLite
{
    public class ServiceItem : INotifyPropertyChanged
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "";

        [JsonPropertyName("directory")]
        public string Directory { get; set; } = "";

        [JsonPropertyName("command")]
        public string Command { get; set; } = "";

        [JsonPropertyName("port")]
        public int? Port { get; set; }

        private bool _runOnStartup;
        [JsonPropertyName("run_on_startup")]
        public bool RunOnStartup
        {
            get => _runOnStartup;
            set
            {
                if (_runOnStartup != value)
                {
                    _runOnStartup = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isOnline;
        [JsonIgnore]
        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (_isOnline != value)
                {
                    _isOnline = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isStarting;
        [JsonIgnore]
        public bool IsStarting
        {
            get => _isStarting;
            set
            {
                if (_isStarting != value)
                {
                    _isStarting = value;
                    OnPropertyChanged();
                }
            }
        }

        private float _cpu;
        [JsonIgnore]
        public float Cpu
        {
            get => _cpu;
            set
            {
                if (_cpu != value)
                {
                    _cpu = value;
                    OnPropertyChanged();
                }
            }
        }

        private float _mem;
        [JsonIgnore]
        public float Mem
        {
            get => _mem;
            set
            {
                if (_mem != value)
                {
                    _mem = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _pidsString = "";
        [JsonIgnore]
        public string PidsString
        {
            get => _pidsString;
            set
            {
                if (_pidsString != value)
                {
                    _pidsString = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _user = "-";
        [JsonIgnore]
        public string User
        {
            get => _user;
            set
            {
                if (_user != value)
                {
                    _user = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonIgnore]
        public List<int> ActivePids { get; set; } = new List<int>();

        [JsonIgnore]
        public string DockerContainerName { get; set; } = "";

        [JsonIgnore]
        public bool IsDockerContainer { get; set; }

        [JsonIgnore]
        public string PortDisplayString => Id == "geyser_mc" ? "25565/19132" : (Port.HasValue ? $"PORT {Port.Value}" : (Id == "bedrock_smp" ? "UDP 19132" : "SYS PROC"));

        [JsonIgnore]
        public string TypeDisplay
        {
            get
            {
                string cmd = Command ?? "";
                if (!string.IsNullOrEmpty(DockerContainerName)) return "docker";
                if (Regex.IsMatch(cmd, @"\bdocker(?:-compose)?\b", RegexOptions.IgnoreCase)) return "docker";
                if (Regex.IsMatch(cmd, @"\.py\b", RegexOptions.IgnoreCase) || Regex.IsMatch(cmd, @"\bpython(?:\.exe)?\b", RegexOptions.IgnoreCase)) return ".py";
                if (Regex.IsMatch(cmd, @"\.js\b", RegexOptions.IgnoreCase) || Regex.IsMatch(cmd, @"\bnode(?:\.exe)?\b", RegexOptions.IgnoreCase)) return ".js";
                if (Regex.IsMatch(cmd, @"\.ps1\b", RegexOptions.IgnoreCase) || Regex.IsMatch(cmd, @"\bpowershell(?:\.exe)?\b", RegexOptions.IgnoreCase)) return ".ps1";
                if (Regex.IsMatch(cmd, @"\.bat\b|\.cmd\b", RegexOptions.IgnoreCase) || Regex.IsMatch(cmd, @"\bcmd(?:\.exe)?\b", RegexOptions.IgnoreCase)) return ".bat";
                if (Regex.IsMatch(cmd, @"\.exe\b", RegexOptions.IgnoreCase)) return ".exe";
                return "proc";
            }
        }

        [JsonIgnore]
        public string DisplayDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(Directory) || Directory == "N/A")
                    return "Dir: N/A";
                if (Directory.Length > 30)
                    return $"Dir: ...{Directory.Substring(Directory.Length - 27)}";
                return $"Dir: {Directory}";
            }
        }

        [JsonIgnore]
        public string DisplayId => $"ID: {Id}";

        [JsonIgnore]
        public string ResourcesString => IsOnline ? $"{Cpu:F1}% CPU | {Mem:F1} MB RAM" : (IsStarting ? "Starting..." : "Offline");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
