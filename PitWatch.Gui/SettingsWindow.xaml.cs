using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using PitWatch.Commands;
using PitWatch.History;

namespace PitWatch.Gui;

public partial class SettingsWindow : Window
{
    private string _selectedAccent = "Green";
    private readonly Dictionary<string, List<string>> _callouts;
    private string? _currentCalloutEvent;
    private bool _loadingCallout;   // suppresses TextChanged while populating the box
    private bool _bindingKey;

    // Appearance values as they were when the window opened - compared on save so the
    // restart hint only appears when a restart is genuinely needed.
    private readonly string _initialTheme;
    private readonly string _initialAccent;
    private readonly bool _initialColorblind;

    public SettingsWindow()
    {
        InitializeComponent();

        var config = PitWatch.Config.Load();
        VersionLabel.Text = $"v{PitWatch.AppInfo.Version}";
        DataPathText.Text = PitWatch.UserDataPaths.Root;

        // Engineer
        PersonalityKind.IsChecked = config.Personality == "Kind";
        PersonalityMean.IsChecked = config.Personality == "Mean";
        PersonalityProfessional.IsChecked = config.Personality == "Professional";
        PersonalityHelpful.IsChecked = config.Personality is not ("Kind" or "Mean" or "Professional");

        ChattinessQuiet.IsChecked = config.Chattiness == "Quiet";
        ChattinessChatty.IsChecked = config.Chattiness == "Chatty";
        ChattinessNormal.IsChecked = config.Chattiness is not ("Quiet" or "Chatty");

        AnnounceOvertakesCheck.IsChecked = config.AnnounceOvertakes;
        AnnounceLapAnalysisCheck.IsChecked = config.AnnounceLapAnalysis;
        AnnounceProximityCheck.IsChecked = config.AnnounceProximity;
        AnnounceTyreTempsCheck.IsChecked = config.AnnounceTyreTemps;
        AnnounceStintSummaryCheck.IsChecked = config.AnnounceStintSummary;

        // Voice & keys
        if (!config.GeminiApiKey.Contains("PASTE_YOUR")) ApiKeyBox.Password = config.GeminiApiKey;
        UseElevenLabsCheckBox.IsChecked = config.UseElevenLabs;
        ElevenLabsPanel.Visibility = config.UseElevenLabs ? Visibility.Visible : Visibility.Collapsed;
        ElevenLabsKeyBox.Password = config.ElevenLabsApiKey;
        ElevenLabsVoiceBox.Text = config.ElevenLabsVoiceId;
        VoiceRateSlider.Value = config.SpeechVoiceRate;
        VoiceVolumeSlider.Value = config.SpeechVoiceVolume;
        RadioBeepCheckBox.IsChecked = config.RadioBeepEnabled;
        VoiceInputCheck.IsChecked = config.VoiceInputEnabled;
        BindStatusText.Text = string.IsNullOrWhiteSpace(config.VoiceInputBinding)
            ? "No button bound yet."
            : $"Bound to: {config.VoiceInputBinding}";

        // Appearance
        DarkModeRadio.IsChecked = config.ThemeMode != "Light";
        LightModeRadio.IsChecked = config.ThemeMode == "Light";
        ColorblindCheckBox.IsChecked = config.ColorblindMode;
        _selectedAccent = config.AccentColor;
        _initialTheme = config.ThemeMode;
        _initialAccent = config.AccentColor;
        _initialColorblind = config.ColorblindMode;
        BuildAccentSwatches();

        ShowSpeedTraceCheck.IsChecked = config.ShowSpeedTrace;
        ShowPedalTraceCheck.IsChecked = config.ShowPedalTrace;
        ShowGForceCheck.IsChecked = config.ShowGForce;

        DeveloperModeCheck.IsChecked = config.DeveloperMode;

        UpdateBroadcastingStatus(config);
        LmuStatusText.Text = LmuAutoSetup.DescribeStatus();

        // Appearance controls trigger the restart hint, since those only take effect at
        // startup - everything else applies immediately on save.
        DarkModeRadio.Checked += (_, _) => UpdateRestartHint();
        LightModeRadio.Checked += (_, _) => UpdateRestartHint();
        ColorblindCheckBox.Checked += (_, _) => UpdateRestartHint();
        ColorblindCheckBox.Unchecked += (_, _) => UpdateRestartHint();

        // Callouts
        _callouts = CustomCallouts.ReadAll();
        foreach (var ev in CustomCallouts.KnownEvents)
        {
            CalloutEventList.Items.Add(new ListBoxItem
            {
                Content = CustomCallouts.DescribeEvent(ev),
                Tag = ev
            });
        }
        CalloutEventList.SelectedIndex = 0;
    }

    /// <summary>
    /// Shows the restart note only when something that needs a restart has actually
    /// changed, so it isn't permanently nagging about a restart nobody needs.
    /// </summary>
    private void UpdateRestartHint()
    {
        if (RestartHintText == null) return;

        string theme = LightModeRadio.IsChecked == true ? "Light" : "Dark";
        bool changed = theme != _initialTheme
                    || _selectedAccent != _initialAccent
                    || (ColorblindCheckBox.IsChecked == true) != _initialColorblind;

        RestartHintText.Text = changed
            ? "Appearance changes need a restart - use Save & restart to apply them now."
            : "";
    }

    // ---------- Callout editor ----------

    private void CalloutEvent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CalloutEventList.SelectedItem is not ListBoxItem item || item.Tag is not string key) return;

        _loadingCallout = true;
        _currentCalloutEvent = key;
        CalloutHintText.Text = $"{CustomCallouts.DescribeEvent(key)} - one line per variation.";
        CalloutTextBox.Text = _callouts.TryGetValue(key, out var lines)
            ? string.Join(Environment.NewLine, lines)
            : "";
        _loadingCallout = false;
    }

    private void CalloutText_Changed(object sender, TextChangedEventArgs e)
    {
        // Ignore the change that happens while switching events, otherwise selecting a
        // different event would immediately overwrite it with the previous one's text.
        if (_loadingCallout || _currentCalloutEvent == null) return;

        var lines = CalloutTextBox.Text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) _callouts.Remove(_currentCalloutEvent);
        else _callouts[_currentCalloutEvent] = lines;

        CalloutStatusText.Text = $"{lines.Count} line{(lines.Count == 1 ? "" : "s")} for this event - saved when you press Save.";
    }

    // ---------- Appearance ----------

    private void BuildAccentSwatches()
    {
        AccentSwatches.Items.Clear();
        foreach (var (name, hex) in ThemeManager.AccentChoices)
        {
            var colour = (Color)ColorConverter.ConvertFromString(hex);
            var border = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(colour),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(name == _selectedAccent ? 3 : 0),
                BorderBrush = (Brush)FindResource("TextPrimary"),
                ToolTip = name,
                Tag = name
            };
            border.MouseLeftButtonUp += (s, _) =>
            {
                _selectedAccent = (string)((Border)s).Tag;
                BuildAccentSwatches();
                UpdateRestartHint();
            };
            AccentSwatches.Items.Add(border);
        }
    }

    // ---------- Other handlers ----------

    private void UseElevenLabsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ElevenLabsPanel == null) return;
        ElevenLabsPanel.Visibility = UseElevenLabsCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        _bindingKey = true;
        BindStatusText.Text = "Press the key or wheel button you want to use...";
        BindButton.IsEnabled = false;
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_bindingKey)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var config = PitWatch.Config.Load();
            config.VoiceInputBinding = key.ToString();
            config.Save();

            BindStatusText.Text = $"Bound to: {key}";
            _bindingKey = false;
            BindButton.IsEnabled = true;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void BroadcastingButton_Click(object sender, RoutedEventArgs e)
    {
        var config = PitWatch.Config.Load();
        var result = BroadcastingAutoSetup.TryEnable(config);
        BroadcastingStatusText.Text = result.Message;
        BroadcastingStatusText.Foreground = (Brush)FindResource(result.Success ? "AccentGreen" : "AccentRed");
    }

    private void UpdateBroadcastingStatus(PitWatch.Config config)
    {
        BroadcastingStatusText.Text = config.BroadcastingEnabled ? "Currently ON." : "Currently OFF.";
        BroadcastingStatusText.Foreground = (Brush)FindResource(config.BroadcastingEnabled ? "AccentGreen" : "TextMuted");
    }

    private async void TestGemini_Click(object sender, RoutedEventArgs e)
    {
        TestGeminiButton.IsEnabled = false;
        GeminiTestResult.Text = "Checking...";
        GeminiTestResult.Foreground = (Brush)FindResource("TextMuted");

        var config = PitWatch.Config.Load();
        var error = await PitWatch.AI.GeminiClient.TestKeyAsync(ApiKeyBox.Password.Trim(), config.GeminiModel);

        GeminiTestResult.Text = error ?? "Key works.";
        GeminiTestResult.Foreground = (Brush)FindResource(error == null ? "AccentGreen" : "AccentRed");
        TestGeminiButton.IsEnabled = true;
    }

    private async void TestElevenLabs_Click(object sender, RoutedEventArgs e)
    {
        TestElevenLabsButton.IsEnabled = false;
        ElevenLabsTestResult.Text = "Checking...";
        ElevenLabsTestResult.Foreground = (Brush)FindResource("TextMuted");

        var error = await PitWatch.Voice.SpeechOutput.TestElevenLabsKeyAsync(ElevenLabsKeyBox.Password.Trim());

        ElevenLabsTestResult.Text = error ?? "Key works.";
        ElevenLabsTestResult.Foreground = (Brush)FindResource(error == null ? "AccentGreen" : "AccentRed");
        TestElevenLabsButton.IsEnabled = true;
    }

    private void OpenLmuFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = LmuAutoSetup.FindLmuFolder();
        if (folder == null)
        {
            LmuStatusText.Text = "Couldn't find Le Mans Ultimate on this PC. In Steam: right-click the game, Manage, Browse local files - then create a Plugins folder there if there isn't one.";
            LmuStatusText.Foreground = (Brush)FindResource("AccentRed");
            return;
        }

        try
        {
            var plugins = System.IO.Path.Combine(folder, "Plugins");
            System.IO.Directory.CreateDirectory(plugins);
            Process.Start(new ProcessStartInfo(plugins) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't open the LMU plugins folder.", ex);
            LmuStatusText.Text = $"Couldn't open it: {ex.Message}";
        }
    }

    private void LmuEnable_Click(object sender, RoutedEventArgs e)
    {
        var result = LmuAutoSetup.TryEnable();
        LmuStatusText.Text = result.Message;
        LmuStatusText.Foreground = (Brush)FindResource(result.Success ? "AccentGreen" : "AccentRed");

        // If the DLL is what's missing, open the folder so the next step is obvious.
        if (!result.Success && result.PluginFolder != null && System.IO.Directory.Exists(result.PluginFolder))
        {
            try { Process.Start(new ProcessStartInfo(result.PluginFolder) { UseShellExecute = true }); }
            catch { /* not important enough to report */ }
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.File.Exists(PitWatch.Logger.LogPath))
            {
                MessageBox.Show("No log file yet - nothing has been logged.", "PitWatch");
                return;
            }
            Process.Start(new ProcessStartInfo(PitWatch.Logger.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't open the log file.", ex);
            MessageBox.Show($"Couldn't open it. The log is at:\n{PitWatch.Logger.LogPath}", "PitWatch");
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PitWatch.UserDataPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(PitWatch.UserDataPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't open the data folder.", ex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        DialogResult = true;
        Close();
    }

    private void SaveRestart_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;

        // Theme, accent and colourblind mode are applied once at startup when the resource
        // dictionaries are loaded, so they genuinely need a restart - this just saves the
        // user doing it manually.
        try
        {
            string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exe))
            {
                MessageBox.Show("Couldn't work out how to restart - please close and reopen PitWatch.", "PitWatch");
                DialogResult = true;
                Close();
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't restart automatically.", ex);
            MessageBox.Show("Settings were saved, but PitWatch couldn't restart itself - please close and reopen it.", "PitWatch");
            DialogResult = true;
            Close();
        }
    }

    /// <summary>Writes every setting. Returns false if validation failed.</summary>
    private bool SaveSettings()
    {
        var key = ApiKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(key) && key.Length < 10)
        {
            ErrorText.Text = "That doesn't look like a full key - paste the whole thing, or clear the box.";
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        ErrorText.Visibility = Visibility.Collapsed;

        var config = PitWatch.Config.Load();

        config.GeminiApiKey = key;
        config.UseElevenLabs = UseElevenLabsCheckBox.IsChecked == true;
        config.ElevenLabsApiKey = ElevenLabsKeyBox.Password.Trim();
        config.ElevenLabsVoiceId = string.IsNullOrWhiteSpace(ElevenLabsVoiceBox.Text)
            ? config.ElevenLabsVoiceId : ElevenLabsVoiceBox.Text.Trim();
        config.SpeechVoiceRate = (int)VoiceRateSlider.Value;
        config.SpeechVoiceVolume = (int)VoiceVolumeSlider.Value;
        config.RadioBeepEnabled = RadioBeepCheckBox.IsChecked == true;
        config.VoiceInputEnabled = VoiceInputCheck.IsChecked == true;

        config.Personality = PersonalityKind.IsChecked == true ? "Kind"
            : PersonalityMean.IsChecked == true ? "Mean"
            : PersonalityProfessional.IsChecked == true ? "Professional"
            : "Helpful";

        config.Chattiness = ChattinessQuiet.IsChecked == true ? "Quiet"
            : ChattinessChatty.IsChecked == true ? "Chatty"
            : "Normal";

        config.AnnounceOvertakes = AnnounceOvertakesCheck.IsChecked == true;
        config.AnnounceLapAnalysis = AnnounceLapAnalysisCheck.IsChecked == true;
        config.AnnounceProximity = AnnounceProximityCheck.IsChecked == true;
        config.AnnounceTyreTemps = AnnounceTyreTempsCheck.IsChecked == true;
        config.AnnounceStintSummary = AnnounceStintSummaryCheck.IsChecked == true;

        config.ThemeMode = LightModeRadio.IsChecked == true ? "Light" : "Dark";
        config.ColorblindMode = ColorblindCheckBox.IsChecked == true;
        config.AccentColor = _selectedAccent;
        config.ShowSpeedTrace = ShowSpeedTraceCheck.IsChecked == true;
        config.ShowPedalTrace = ShowPedalTraceCheck.IsChecked == true;
        config.ShowGForce = ShowGForceCheck.IsChecked == true;

        config.DeveloperMode = DeveloperModeCheck.IsChecked == true;

        config.Save();
        CustomCallouts.WriteAll(_callouts);
        return true;
    }
}
