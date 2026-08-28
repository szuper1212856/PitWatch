using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace PitWatch.Gui;

public partial class App : Application
{
    /// <summary>
    /// Custom entry point. Velopack needs to run before WPF starts up, because during
    /// install/update it launches this same exe with special arguments and expects a quick
    /// response - spinning up the whole UI first would be wasted work (and can hang the
    /// update). VelopackApp.Build().Run() handles those hooks and returns immediately in
    /// the normal case.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            // Never let the updater framework stop the app from starting.
            PitWatch.Logger.Error("Velopack startup hook failed - continuing without updates.", ex);
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ForceInvariantNumberFormatting();
        HookCrashHandlers();

        // Move any data from an older install (which kept things next to the .exe) into
        // %APPDATA% before anything tries to read it.
        PitWatch.UserDataPaths.MigrateFromLegacyIfNeeded();

        PitWatch.Logger.StartNewRun(PitWatch.AppInfo.Version);

        var config = PitWatch.Config.Load();

        ThemeManager.Apply(config.ThemeMode, config.ColorblindMode, config.AccentColor);

        if (!config.SetupCompleted)
        {
            var setup = new SetupWindow();
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        var main = new MainWindow();
        main.Closed += (_, _) => Shutdown();
        main.Show();
    }

    /// <summary>
    /// Forces invariant (dot) decimal formatting regardless of Windows region settings.
    ///
    /// Without this, everything formatted with e.g. {fuel:F1} follows the machine's locale -
    /// on a Hungarian/German/French Windows the engineer says "12,5 liters" and written
    /// values use commas too. Since all of PitWatch's spoken output is English, matching
    /// that with invariant number formatting keeps behaviour identical on every machine.
    /// </summary>
    private static void ForceInvariantNumberFormatting()
    {
        var culture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>
    /// Catches anything that would otherwise kill the app with no explanation. The user
    /// gets a readable message pointing at the log file rather than a silent disappearance.
    /// </summary>
    private void HookCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            PitWatch.Logger.Error("Unhandled UI exception.", args.Exception);
            ShowCrashMessage(args.Exception);
            args.Handled = true; // keep running where possible rather than dying outright
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            PitWatch.Logger.Error("Unhandled background exception.", args.ExceptionObject as Exception);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            PitWatch.Logger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private static void ShowCrashMessage(Exception ex)
    {
        MessageBox.Show(
            $"Something went wrong:\n\n{ex.Message}\n\n" +
            $"PitWatch will try to keep running. Details were written to:\n{PitWatch.Logger.LogPath}\n\n" +
            "If this keeps happening, sending that log file makes it much easier to fix.",
            "PitWatch", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
