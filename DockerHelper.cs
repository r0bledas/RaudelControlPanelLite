using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RaudelControlPanelLite
{
    /// <summary>
    /// Represents a single Docker container as reported by <c>docker ps</c>.
    /// </summary>
    public class DockerContainer
    {
        public string Name { get; set; } = "";
        public string Image { get; set; } = "";
        public bool Running { get; set; }
        public string State { get; set; } = "";
        public string Status { get; set; } = "";
        public string RestartPolicy { get; set; } = "no";
        public string Project { get; set; } = "";
        public string ComposeFile { get; set; } = "";
        public int? PublishedPort { get; set; }
    }

    /// <summary>
    /// Reads and updates Docker-backed services: restart policy, container listing,
    /// per-container statistics, and start/stop.
    /// </summary>
    public static class DockerHelper
    {
        private static readonly Regex DockerCommandRegex = new(
            @"\bdocker(?:-compose)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DockerContainerCommandRegex = new(
            @"\bdocker\s+(?:start|stop|restart|update)\s+(?:-[^\s]+\s+)*""?([A-Za-z0-9][A-Za-z0-9_.-]*)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ComposeFileArgRegex = new(
            @"(?:-f|--file)\s+""?([^""\s]+\.ya?ml)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] ComposeFileNames =
        {
            "compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"
        };

        public static bool IsDockerService(ServiceItem svc)
        {
            if (svc == null) return false;
            if (!string.IsNullOrEmpty(svc.DockerContainerName)) return true;
            return !string.IsNullOrWhiteSpace(svc.Command) && DockerCommandRegex.IsMatch(svc.Command);
        }

        /// <summary>
        /// Returns true when the container(s) behind this service are configured to start with Docker.
        /// Returns null when the service is not Docker-backed or its containers could not be resolved.
        /// </summary>
        public static bool? GetStartupEnabled(ServiceItem svc)
        {
            if (!IsDockerService(svc)) return null;

            var containers = ResolveContainers(svc);
            if (containers.Count > 0)
            {
                var policies = GetRestartPolicies(containers);
                if (policies.Count > 0)
                    return policies.Values.All(IsStartupPolicy);
            }

            return GetComposeConfiguredStartup(svc);
        }

        /// <summary>
        /// Applies a Docker restart policy to every container behind the service.
        /// </summary>
        public static bool SetStartupEnabled(ServiceItem svc, bool enabled)
        {
            if (!IsDockerService(svc)) return false;

            var containers = ResolveContainers(svc);
            if (containers.Count == 0) return false;

            string policy = enabled ? "unless-stopped" : "no";
            string args = "update --restart=" + policy + " " + string.Join(" ", containers.Select(Quote));
            var result = RunDocker(args);
            return result.Ok;
        }

        /// <summary>
        /// Starts the container(s) behind the service (compose-aware).
        /// </summary>
        public static bool StartContainer(ServiceItem svc)
        {
            string? composeFile = FindComposeFile(svc);
            if (composeFile != null)
            {
                var result = RunDocker("compose -f " + Quote(composeFile) + " up -d");
                return result.Ok;
            }

            var containers = ResolveContainers(svc);
            if (containers.Count == 0) return false;
            return RunDocker("start " + string.Join(" ", containers.Select(Quote))).Ok;
        }

        /// <summary>
        /// Stops the container(s) behind the service (graceful <c>docker stop</c>).
        /// </summary>
        public static bool StopContainer(ServiceItem svc)
        {
            var containers = ResolveContainers(svc);
            if (containers.Count == 0) return false;
            return RunDocker("stop " + string.Join(" ", containers.Select(Quote))).Ok;
        }

        /// <summary>
        /// Lists all Docker containers and their metadata (including restart policy).
        /// </summary>
        public static List<DockerContainer> ListContainers(bool includeStopped)
        {
            var containers = new List<DockerContainer>();

            string psArgs = "ps " + (includeStopped ? "-a " : "") + "--format " + Quote("{{json .}}");
            var psResult = RunDocker(psArgs);
            if (string.IsNullOrWhiteSpace(psResult.Stdout)) return containers;

            foreach (var line in psResult.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var c = new DockerContainer
                    {
                        Name = GetString(root, "Names"),
                        Image = GetString(root, "Image"),
                        State = GetString(root, "State"),
                        Status = GetString(root, "Status"),
                    };
                    c.Running = c.State.Equals("running", StringComparison.OrdinalIgnoreCase);

                    string labels = GetString(root, "Labels");
                    c.Project = ExtractLabel(labels, "com.docker.compose.project");
                    c.ComposeFile = ExtractLabel(labels, "com.docker.compose.project.config_files");
                    c.PublishedPort = ParsePublishedPort(GetString(root, "Ports"));

                    if (c.Name.Length == 0) continue;
                    containers.Add(c);
                }
                catch { }
            }

            // Attach restart policies in a single inspect call.
            var policies = GetRestartPolicies(containers.Select(c => c.Name).ToList());
            foreach (var c in containers)
            {
                if (policies.TryGetValue(c.Name, out var policy)) c.RestartPolicy = policy;
            }

            return containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Returns per-container CPU percentage and memory usage (MB) via <c>docker stats --no-stream</c>.
        /// </summary>
        public static List<(string Name, float Cpu, float Mem)> GetContainerStats()
        {
            var stats = new List<(string, float, float)>();
            string args = "stats --no-stream --format " + Quote("{{json .}}");
            var result = RunDocker(args);
            if (string.IsNullOrWhiteSpace(result.Stdout)) return stats;

            foreach (var line in result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string name = GetString(root, "Name");
                    if (name.Length == 0) continue;

                    float cpu = 0f;
                    string cpuPerc = GetString(root, "CPUPerc");
                    if (cpuPerc.EndsWith("%") && float.TryParse(cpuPerc.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float cpuVal))
                        cpu = cpuVal;

                    float mem = ParseMemUsageMb(GetString(root, "MemUsage"));
                    stats.Add((name, cpu, mem));
                }
                catch { }
            }

            return stats;
        }

        /// <summary>
        /// Container names that make up the service: either the name passed to
        /// <c>docker start/stop</c> or the containers belonging to the compose project.
        /// </summary>
        public static List<string> ResolveContainers(ServiceItem svc)
        {
            if (!string.IsNullOrEmpty(svc.DockerContainerName))
                return new List<string> { svc.DockerContainerName };

            var explicitMatch = DockerContainerCommandRegex.Match(svc.Command ?? "");
            if (explicitMatch.Success)
                return new List<string> { explicitMatch.Groups[1].Value };

            string? composeFile = FindComposeFile(svc);
            if (composeFile == null) return new List<string>();

            var compose = ReadComposeInfo(composeFile);

            var projectContainers = GetContainersByProject(compose.Project);
            if (projectContainers.Count > 0) return projectContainers;

            return compose.ContainerNames;
        }

        private static bool IsStartupPolicy(string policy)
        {
            return policy.Equals("always", StringComparison.OrdinalIgnoreCase)
                || policy.Equals("unless-stopped", StringComparison.OrdinalIgnoreCase);
        }

        private static string? FindComposeFile(ServiceItem svc)
        {
            string command = svc.Command ?? "";

            var fileArg = ComposeFileArgRegex.Match(command);
            if (fileArg.Success && File.Exists(fileArg.Groups[1].Value))
                return fileArg.Groups[1].Value;

            if (!string.IsNullOrWhiteSpace(svc.Directory) && Directory.Exists(svc.Directory))
            {
                foreach (var name in ComposeFileNames)
                {
                    string candidate = Path.Combine(svc.Directory, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        private sealed class ComposeInfo
        {
            public string Project = "";
            public List<string> ContainerNames = new();
            public bool HasServices;
            public bool AllStartupEnabled = true;
        }

        private static ComposeInfo ReadComposeInfo(string composeFile)
        {
            var info = new ComposeInfo();
            info.Project = Path.GetFileName(Path.GetDirectoryName(composeFile) ?? "").ToLowerInvariant();

            var result = RunDocker("compose -f " + Quote(composeFile) + " config --format json");
            if (!result.Ok || string.IsNullOrWhiteSpace(result.Stdout)) return info;

            try
            {
                using var doc = JsonDocument.Parse(result.Stdout);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    string? project = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(project)) info.Project = project!;
                }

                if (!root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
                    return info;

                foreach (var service in services.EnumerateObject())
                {
                    info.HasServices = true;

                    string containerName = "";
                    if (service.Value.TryGetProperty("container_name", out var cn) && cn.ValueKind == JsonValueKind.String)
                        containerName = cn.GetString() ?? "";

                    if (string.IsNullOrWhiteSpace(containerName))
                        containerName = $"{info.Project}-{service.Name}-1";

                    info.ContainerNames.Add(containerName);

                    string restart = "no";
                    if (service.Value.TryGetProperty("restart", out var r) && r.ValueKind == JsonValueKind.String)
                        restart = r.GetString() ?? "no";

                    if (!IsStartupPolicy(restart)) info.AllStartupEnabled = false;
                }
            }
            catch
            {
                // Unparseable compose output: fall back to container inspection only.
            }

            return info;
        }

        private static bool? GetComposeConfiguredStartup(ServiceItem svc)
        {
            string? composeFile = FindComposeFile(svc);
            if (composeFile == null) return null;

            var compose = ReadComposeInfo(composeFile);
            if (!compose.HasServices) return null;

            return compose.AllStartupEnabled;
        }

        private static List<string> GetContainersByProject(string project)
        {
            if (string.IsNullOrWhiteSpace(project)) return new List<string>();

            string args = "ps -a --filter " + Quote("label=com.docker.compose.project=" + project)
                        + " --format " + Quote("{{.Names}}");

            var result = RunDocker(args);
            if (!result.Ok) return new List<string>();

            return result.Stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, string> GetRestartPolicies(List<string> containers)
        {
            var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (containers.Count == 0) return policies;

            string args = "inspect --format " + Quote("{{.Name}}|{{.HostConfig.RestartPolicy.Name}}")
                        + " " + string.Join(" ", containers.Select(Quote));

            var result = RunDocker(args);
            if (string.IsNullOrWhiteSpace(result.Stdout)) return policies;

            foreach (var rawLine in result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int separator = line.IndexOf('|');
                if (separator <= 0) continue;

                string name = line.Substring(0, separator).Trim().TrimStart('/');
                string policy = line.Substring(separator + 1).Trim();
                if (name.Length == 0) continue;

                policies[name] = policy.Length == 0 ? "no" : policy;
            }

            return policies;
        }

        private static string GetString(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
            return "";
        }

        private static string ExtractLabel(string labels, string key)
        {
            if (string.IsNullOrWhiteSpace(labels)) return "";
            int idx = labels.IndexOf(key + "=", StringComparison.Ordinal);
            if (idx < 0) return "";
            int valueStart = idx + key.Length + 1;
            int valueEnd = labels.IndexOf(',', valueStart);
            return valueEnd < 0 ? labels.Substring(valueStart) : labels.Substring(valueStart, valueEnd - valueStart);
        }

        private static int? ParsePublishedPort(string ports)
        {
            if (string.IsNullOrWhiteSpace(ports)) return null;
            foreach (var part in ports.Split(','))
            {
                int arrow = part.IndexOf("->", StringComparison.Ordinal);
                if (arrow <= 0) continue;
                string host = part.Substring(0, arrow).Trim();
                int colon = host.LastIndexOf(':');
                string portStr = colon >= 0 ? host.Substring(colon + 1) : host;
                if (int.TryParse(portStr, out int port)) return port;
            }
            return null;
        }

        private static float ParseMemUsageMb(string memUsage)
        {
            if (string.IsNullOrWhiteSpace(memUsage)) return 0f;
            string used = memUsage.Split('/')[0].Trim();
            if (used.Length == 0) return 0f;

            // Extract numeric prefix and unit (B, KiB, MiB, GiB, TiB).
            var match = Regex.Match(used, @"^\s*([0-9.]+)\s*([A-Za-z]+)\s*$");
            if (!match.Success) return 0f;
            if (!float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                return 0f;

            string unit = match.Groups[2].Value.ToUpperInvariant();
            return unit switch
            {
                "B" => value / 1024f / 1024f,
                "KB" or "KIB" => value / 1024f,
                "MB" => value,
                "MIB" => value * 1.048576f,
                "GB" => value * 1024f,
                "GIB" => value * 1024f * 1.048576f,
                "TB" => value * 1024f * 1024f,
                "TIB" => value * 1024f * 1024f * 1.048576f,
                _ => 0f
            };
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "") + "\"";
        }

        private static (bool Ok, string Stdout, string Stderr) RunDocker(string arguments, int timeoutMs = 8000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return (false, "", "docker failed to start");

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return (false, "", "docker command timed out");
                }

                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                return (process.ExitCode == 0, stdout, stderr);
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }
    }
}
