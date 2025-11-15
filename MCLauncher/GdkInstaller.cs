using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace MCLauncher {
    public static class GdkInstaller {
        private static readonly string[] RootCandidates = new[] {
            @"C:\Program Files (x86)\Microsoft GDK",
            @"C:\Program Files\Microsoft GDK"
        };
        private const string XboxGamesRoot = @"C:\XboxGames";
        private const string PreviewFolderName = "Minecraft Preview for Windows";
        private const string RetailFolderName = "Minecraft for Windows";

        private static bool TryFindInPath(out string wdappPath) {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries)) {
                try {
                    var candidate = Path.Combine(dir.Trim(), "wdapp.exe");
                    if (File.Exists(candidate)) {
                        wdappPath = candidate;
                        return true;
                    }
                } catch (Exception ex) {
                    Debug.WriteLine($"Error checking path {dir}: {ex.Message}");
                }
            }
            wdappPath = string.Empty;
            return false;
        }

        public static bool TryGetWdappPath(out string wdappPath) {
            if (TryFindInPath(out wdappPath)) return true;
            foreach (var root in RootCandidates) {
                try {
                    if (!Directory.Exists(root)) continue;
                    var subdirs = Directory.GetDirectories(root);
                    foreach (var sd in subdirs.OrderByDescending(s => s)) {
                        var p1 = Path.Combine(sd, "tools", "bin", "gaming", "wdapp.exe");
                        if (File.Exists(p1)) { wdappPath = p1; return true; }
                        var p2 = Path.Combine(sd, "Tools", "bin", "gaming", "wdapp.exe");
                        if (File.Exists(p2)) { wdappPath = p2; return true; }
                    }
                } catch (Exception ex) {
                    Debug.WriteLine($"Error scanning GDK directory {root}: {ex.Message}");
                }
            }
            wdappPath = string.Empty;
            return false;
        }

        public static Task InstallMsixvc(string msixvcPath) => InstallMsixvc(msixvcPath, false, CancellationToken.None);

        public static async Task InstallMsixvc(string msixvcPath, bool useBootstrapper, CancellationToken cancellationToken = default(CancellationToken)) {
            bool isHttp = msixvcPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || msixvcPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isHttp && !File.Exists(msixvcPath)) throw new FileNotFoundException(msixvcPath);
            if (!TryGetWdappPath(out var wdapp)) throw new InvalidOperationException("wdapp.exe not found. Install Microsoft GDK.");
            Debug.WriteLine($"Starting wdapp install. Source={msixvcPath}, useBootstrapper={useBootstrapper}, wdapp={wdapp}");
            var args = $"install /w {(useBootstrapper ? "/bootstrapper " : string.Empty)}\"{msixvcPath}\"";
            var psi = new ProcessStartInfo {
                FileName = wdapp,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = new Process { StartInfo = psi }) {
                using (cancellationToken.Register(() => {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                })) {
                    p.Start();
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    await Task.Run(() => p.WaitForExit(), cancellationToken);
                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    Debug.WriteLine($"wdapp install finished with code {p.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
                    if (p.ExitCode != 0 && useBootstrapper) {
                        try {
                            var psi2 = new ProcessStartInfo {
                                FileName = wdapp,
                                Arguments = $"install /w \"{msixvcPath}\"",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };
                            using (var p2 = new Process { StartInfo = psi2 }) {
                                using (cancellationToken.Register(() => { try { if (!p2.HasExited) p2.Kill(); } catch { } })) {
                                    p2.Start();
                                    var s1 = p2.StandardOutput.ReadToEndAsync();
                                    var s2 = p2.StandardError.ReadToEndAsync();
                                    await Task.Run(() => p2.WaitForExit(), cancellationToken);
                                    string so1 = await s1; string se1 = await s2;
                                    Debug.WriteLine($"wdapp fallback install finished with code {p2.ExitCode}\nSTDOUT:\n{so1}\nSTDERR:\n{se1}");
                                    if (p2.ExitCode != 0) {
                                        throw new Exception($"wdapp install failed (code {p.ExitCode})\n{stdout}\n{stderr}\nFallback without /bootstrapper failed (code {p2.ExitCode})\n{so1}\n{se1}");
                                    }
                                    return;
                                }
                            }
                        } catch {
                            throw;
                        }
                    }
                    if (p.ExitCode != 0) {
                        throw new Exception($"wdapp install failed (code {p.ExitCode})\n{stdout}\n{stderr}");
                    }
                }
            }
        }

        public static string GetGameFolder(bool preview)
        {
            string folder = Path.Combine(XboxGamesRoot, preview ? PreviewFolderName : RetailFolderName);
            return folder;
        }

        public static bool TryGetExePath(bool preview, out string exePath)
        {
            exePath = string.Empty;
            string folder = GetGameFolder(preview);
            try {
                string candidate = Path.Combine(folder, "Content", "Minecraft.Windows.exe");
                if (File.Exists(candidate)) { exePath = candidate; return true; }
            } catch { }
            return false;
        }

        public static async Task LaunchGdk(bool preview, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryGetWdappPath(out var wdapp)) throw new InvalidOperationException("wdapp.exe not found. Install Microsoft GDK.");
            if (!TryGetExePath(preview, out var exe)) throw new FileNotFoundException("Minecraft.Windows.exe not found in XboxGames.");
            Debug.WriteLine($"Launching GDK. Preview={preview}, wdapp={wdapp}, exe={exe}");
            var psi = new ProcessStartInfo {
                FileName = wdapp,
                Arguments = $"launch \"{exe}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = new Process { StartInfo = psi }) {
                using (cancellationToken.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } })) {
                    p.Start();
                    await Task.Run(() => p.WaitForExit(), cancellationToken);
                    Debug.WriteLine($"wdapp launch finished with code {p.ExitCode}");
                    if (p.ExitCode != 0) throw new Exception("wdapp launch failed");
                }
            }
        }

        public static async Task UninstallGdk(bool preview, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!TryGetWdappPath(out var wdapp)) throw new InvalidOperationException("wdapp.exe not found. Install Microsoft GDK.");
            string folder = GetGameFolder(preview);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            Debug.WriteLine($"Uninstalling GDK. Preview={preview}, folder={folder}, wdapp={wdapp}");
            // Try uninstall using wdapp uninstall with folder path
            var psi = new ProcessStartInfo {
                FileName = wdapp,
                Arguments = $"uninstall \"{folder}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = new Process { StartInfo = psi }) {
                using (cancellationToken.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } })) {
                    p.Start();
                    string stdout = await p.StandardOutput.ReadToEndAsync();
                    string stderr = await p.StandardError.ReadToEndAsync();
                    await Task.Run(() => p.WaitForExit(), cancellationToken);
                    Debug.WriteLine($"wdapp uninstall finished with code {p.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
                    if (p.ExitCode != 0) {
                        throw new Exception($"wdapp uninstall failed (code {p.ExitCode})\n{stdout}\n{stderr}");
                    }
                }
            }
        }

        public static bool IsInstalled(bool preview)
        {
            try {
                string exe;
                if (TryGetExePath(preview, out exe)) return true;
            } catch { }
            try {
                if (!TryGetWdappPath(out var wdapp)) return false;
                var psi = new ProcessStartInfo {
                    FileName = wdapp,
                    Arguments = "list",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = new Process { StartInfo = psi }) {
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (preview) {
                        if (stdout.IndexOf("Minecraft Preview for Windows", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    } else {
                        if (stdout.IndexOf("Minecraft for Windows", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            } catch { }
            return false;
        }

        public static string GetCreatorFolder(bool preview)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string baseName = preview ? "Minecraft Bedrock Preview" : "Minecraft Bedrock";
            return Path.Combine(appData, baseName, "users", "shared", "games", "com.mojang");
        }
    }
}
