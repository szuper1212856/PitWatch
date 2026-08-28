using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PitWatch.DevLauncher;

public partial class LauncherWindow : Window
{
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private long _lastLogLength = -1;
    private Process? _pitWatchProcess;

    public LauncherWindow()
    {
        InitializeComponent();
        RefreshInfo();
        SetStatus("Ready", isIdle: true);

        _logTimer.Tick += (_, _) => TailLog();
        _logTimer.Start();
    }

    // ---------- Build info ----------

    private void RefreshInfo()
    {
        BuildInfoStack.Children.Clear();

        var exe = FindPitWatchExe();

        AddInfoRow("Version", PitWatch.AppInfo.Version);
        bool stale = exe != null && IsInstalledCopy(exe);
        AddInfoRow("Build", exe != null ? DescribeBuild(exe) : "not found",
                   isWarning: exe == null || stale);

        if (stale)
        {
            // This is the trap: with no local build present, the launcher silently falls
            // back to the installed release - so you test old code and conclude your fix
            // didn't work. Worth being loud about.
            AddWarningBanner("No local build found - this would launch your INSTALLED copy, "
                           + "not the source in this folder. Press Build to compile it first.");
        }

        string configState;
        bool configWarn = false;
        try
        {
            if (File.Exists(PitWatch.UserDataPaths.ConfigFile))
            {
                var cfg = PitWatch.Config.Load();
                configState = cfg.SetupCompleted ? "set up" : "setup pending";
                DevModeCheck.IsChecked = cfg.DeveloperMode;

                AddInfoRow("Config", configState);
                AddInfoRow("Gemini key", cfg.HasGeminiKey ? "set" : "not set", isWarning: !cfg.HasGeminiKey);
                AddInfoRow("ElevenLabs",
                    cfg.UseElevenLabs ? (cfg.HasElevenLabsKey ? "on" : "on, NO KEY") : "off",
                    isWarning: cfg.UseElevenLabs && !cfg.HasElevenLabsKey);
                AddInfoRow("Dev mode", cfg.DeveloperMode ? "on" : "off");
            }
            else
            {
                AddInfoRow("Config", "none - wizard will run");
            }
        }
        catch (Exception ex)
        {
            AddInfoRow("Config", $"unreadable: {ex.Message}", isWarning: true);
        }

        try
        {
            int count = Directory.Exists(PitWatch.UserDataPaths.SessionsFolder)
                ? Directory.GetFiles(PitWatch.UserDataPaths.SessionsFolder, "session_*.json").Length
                : 0;
            AddInfoRow("Sessions", count.ToString());
        }
        catch
        {
            AddInfoRow("Sessions", "?");
        }

        FooterText.Text = $"{PitWatch.UserDataPaths.Root}"
                        + (exe != null ? $"    ·    {exe}" : "");
    }

    /// <summary>A prominent warning inside the build panel - more visible than an
    /// amber value, for things that will actively mislead you if missed.</summary>
    private void AddWarningBanner(string message)
    {
        BuildInfoStack.Children.Add(new Border
        {
            Background = (Brush)FindResource("Control"),
            BorderBrush = (Brush)FindResource("Warn"),
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 2),
            Child = new TextBlock
            {
                Text = message,
                Foreground = (Brush)FindResource("Warn"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            }
        });
    }

    /// <summary>Adds a label/value line to the build panel.</summary>
    private void AddInfoRow(string label, string value, bool isWarning = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Muted"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = (Brush)FindResource(isWarning ? "Warn" : "Text"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        BuildInfoStack.Children.Add(grid);
    }

    /// <summary>
    /// Describes which build will launch - whether it's your local Debug/Release output or
    /// the installed copy, plus how recently it was built. Knowing you're about to test a
    /// three-day-old binary saves a lot of confusion.
    /// </summary>
    private static string DescribeBuild(string exePath)
    {
        try
        {
            string kind = exePath.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) ? "Debug"
                        : exePath.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase) ? "Release"
                        : "Installed";

            var age = DateTime.Now - File.GetLastWriteTime(exePath);
            string ageText = age.TotalMinutes < 1 ? "just now"
                           : age.TotalHours < 1 ? $"{age.TotalMinutes:F0}m ago"
                           : age.TotalDays < 1 ? $"{age.TotalHours:F0}h ago"
                           : $"{age.TotalDays:F0}d ago";

            return $"{kind}  ·  built {ageText}";
        }
        catch
        {
            return exePath;
        }
    }

    /// <summary>True when the exe we'd launch is an installed release rather than a
    /// build of the source sitting next to this launcher.</summary>
    private static bool IsInstalledCopy(string exePath) =>
        exePath.Contains(@"\PitWatch\current\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds a PitWatch executable to launch, preferring the local build (what you're
    /// actually working on) over the installed copy, so the launcher tests your changes
    /// rather than the last released version.
    /// </summary>
    private static string? FindPitWatchExe()
    {
        var here = AppContext.BaseDirectory;

        // Walk up to the solution folder, then look for the Gui build output.
        var dir = new DirectoryInfo(here);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "PitWatch.Gui", "bin");
            if (Directory.Exists(candidate))
            {
                var found = Directory.GetFiles(candidate, "PitWatch.Gui.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            dir = dir.Parent;
        }

        // Fall back to an installed copy.
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PitWatch", "current", "PitWatch.Gui.exe");
        return File.Exists(installed) ? installed : null;
    }

    // ---------- Launching ----------

    /// <summary>
    /// Compiles the solution so the launch runs current code. Without this the launcher
    /// happily starts whatever binary happens to exist, which is how you end up testing
    /// last week's build and concluding a fix didn't work.
    /// </summary>
    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildAsync();

    private async Task<bool> BuildAsync()
    {
        BuildButton.IsEnabled = false;
        LaunchButton.IsEnabled = false;
        SetStatus("Building...");
        AppendConsole("Building solution...");

        bool ok = await Task.Run(() =>
        {
            try
            {
                var solutionDir = FindSolutionDir();
                if (solutionDir == null) return false;

                var psi = new ProcessStartInfo("dotnet",
                    "build PitWatch.sln -c Debug -v quiet --nologo")
                {
                    WorkingDirectory = solutionDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    PitWatch.Logger.Warn($"[launcher] build failed:\n{output}");
                }
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                PitWatch.Logger.Error("[launcher] build threw.", ex);
                return false;
            }
        });

        BuildButton.IsEnabled = true;
        LaunchButton.IsEnabled = true;

        if (ok)
        {
            SetStatus("Build OK", isIdle: true);
            AppendConsole("Build succeeded.");
        }
        else
        {
            SetStatus("Build failed", isError: true);
            AppendConsole("Build failed - details above.");
        }

        RefreshInfo();
        return ok;
    }

    /// <summary>Walks up from the launcher to find the folder holding PitWatch.sln.</summary>
    private static string? FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PitWatch.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        // Always compile before launching. The whole point of this launcher is testing
        // current code, and silently starting a stale binary defeats that entirely.
        if (!await BuildAsync()) return;

        var exe = FindPitWatchExe();
        if (exe == null)
        {
            SetStatus("No PitWatch build found", isError: true);
            AppendConsole("Couldn't find PitWatch.Gui.exe. Build the solution first.");
            return;
        }

        try
        {
            if (FreshConfigCheck.IsChecked == true) BackupAndClearConfig();
            if (ClearLogCheck.IsChecked == true) ClearLogFile();

            // Applied after a fresh-config reset so it isn't immediately wiped.
            if (DevModeCheck.IsChecked == true) ForceDeveloperMode();

            AppendConsole($"Launching {exe}");
            _pitWatchProcess = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            SetStatus("Running");
            LaunchButton.Content = "RELAUNCH";

            RefreshInfo();
        }
        catch (Exception ex)
        {
            SetStatus("Launch failed", isError: true);
            AppendConsole($"Launch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves the config aside rather than deleting it - re-testing the first-run wizard is
    /// a common thing to want, but losing your real API keys to do it is not.
    /// </summary>
    private void BackupAndClearConfig()
    {
        try
        {
            var path = PitWatch.UserDataPaths.ConfigFile;
            if (!File.Exists(path))
            {
                AppendConsole("No config to reset - first run will show the wizard anyway.");
                return;
            }

            var backup = path + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup);
            AppendConsole($"Config moved aside to {Path.GetFileName(backup)} - wizard will run.");
        }
        catch (Exception ex)
        {
            AppendConsole($"Couldn't reset config: {ex.Message}");
        }
    }

    private void ForceDeveloperMode()
    {
        try
        {
            var cfg = PitWatch.Config.Load();
            cfg.DeveloperMode = true;
            cfg.Save();
            AppendConsole("Developer mode forced on.");
        }
        catch (Exception ex)
        {
            AppendConsole($"Couldn't set developer mode: {ex.Message}");
        }
    }

    // ---------- Log console ----------

    private void TailLog()
    {
        // Notice when PitWatch exits, so the status doesn't sit on "Running" forever.
        if (_pitWatchProcess is { HasExited: true })
        {
            SetStatus($"Exited (code {_pitWatchProcess.ExitCode})",
                isError: _pitWatchProcess.ExitCode != 0,
                isIdle: _pitWatchProcess.ExitCode == 0);
            _pitWatchProcess = null;
            LaunchButton.Content = "LAUNCH PITWATCH";
        }

        try
        {
            var path = PitWatch.Logger.LogPath;
            if (!File.Exists(path))
            {
                if (_lastLogLength != 0)
                {
                    RenderConsole(new[] { "(no log file yet)" });
                    _lastLogLength = 0;
                }
                return;
            }

            var info = new FileInfo(path);
            if (info.Length == _lastLogLength) return; // unchanged, nothing to redraw
            _lastLogLength = info.Length;

            // Opened with full sharing because PitWatch is writing to this file at the
            // same time - without ReadWrite sharing the read would fail with a lock error.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');

            var lines = ErrorsOnlyCheck.IsChecked == true
                ? all.Where(l => l.Contains("[ERROR]") || l.Contains("[WARN]")).ToArray()
                : all;

            // Only the tail matters, and rendering thousands of lines makes the UI crawl.
            RenderConsole(lines.TakeLast(400));

            if (AutoScrollCheck.IsChecked == true) ConsoleScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            RenderConsole(new[] { $"(couldn't read log: {ex.Message})" });
        }
    }

    /// <summary>
    /// Draws log lines with errors and warnings coloured, so problems stand out instead of
    /// being buried in a wall of identical grey text - which is the whole point of having
    /// a console rather than just opening the file in Notepad.
    /// </summary>
    private void RenderConsole(IEnumerable<string> lines)
    {
        ConsoleList.Items.Clear();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string key = line.Contains("[ERROR]") ? "Bad"
                       : line.Contains("[WARN]") ? "Warn"
                       : line.Contains("[launcher]") ? "Accent"
                       : "Muted";

            ConsoleList.Items.Add(new TextBlock
            {
                Text = line.TrimEnd(),
                Foreground = (Brush)FindResource(key),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 0, 1),
            });
        }

        if (AutoScrollCheck.IsChecked == true) ConsoleScroll.ScrollToEnd();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        _lastLogLength = -1; // force a redraw with the new filter
        TailLog();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ClearLogFile();

    private void ClearLogFile()
    {
        try
        {
            if (File.Exists(PitWatch.Logger.LogPath))
            {
                File.WriteAllText(PitWatch.Logger.LogPath, "");
                _lastLogLength = -1;
                ConsoleList.Items.Clear();
                AppendConsole("Log cleared.");
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"Couldn't clear the log: {ex.Message}");
        }
    }

    /// <summary>Writes a launcher message into the shared log, so launcher actions and
    /// PitWatch's own output appear together in one timeline.</summary>
    private void AppendConsole(string message)
    {
        PitWatch.Logger.Info($"[launcher] {message}");
        _lastLogLength = -1;
        TailLog();
    }

    // ---------- Buttons ----------

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshInfo();

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PitWatch.UserDataPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(PitWatch.UserDataPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendConsole($"Couldn't open data folder: {ex.Message}");
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(PitWatch.UserDataPaths.ConfigFile))
            {
                AppendConsole("No config file yet.");
                return;
            }
            Process.Start(new ProcessStartInfo(PitWatch.UserDataPaths.ConfigFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendConsole($"Couldn't open config: {ex.Message}");
        }
    }

    private void SetStatus(string text, bool isError = false, bool isIdle = false)
    {
        string key = isError ? "Bad" : isIdle ? "Muted" : "Accent";
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(key);
        StatusDot.Fill = (Brush)FindResource(key);
    }
}
