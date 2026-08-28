using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using PitWatch.History;

namespace PitWatch.Gui;

public partial class SetupWindow : Window
{
    private StackPanel[] _pages = Array.Empty<StackPanel>();
    private RadioButton[] _navItems = Array.Empty<RadioButton>();
    private string _selectedAccent = "Green";

    // First run walks through every step in order so nothing important gets missed.
    // Settings (edit mode) lets you jump straight to whatever you came to change.
    private readonly bool _wizardMode;

    private readonly Dictionary<string, List<string>> _callouts;
    private string? _currentCalloutEvent;
    private bool _loadingCallout;   // suppresses TextChanged while populating the box
    private int _currentStep;
    private int _furthestStepReached;

    private static readonly (string Title, string Subtitle)[] SectionInfo =
    {
        ("AI & Voice", "Both keys are optional - everything else works without them."),
        ("Personality", "How your engineer talks to you."),
        ("Voice Input", "Talk to your engineer with a button on your wheel."),
        ("Track Map", "See every car on track, live."),
        ("Alerts", "Choose what he speaks up about."),
        ("Appearance", "Make it yours."),
    };

    public SetupWindow(bool isEditMode = false)
    {
        InitializeComponent();
        _pages = new[] { PageAi, PagePersonality, PageVoiceInput, PageTrackMap, PageAlerts, PageAppearance };
        _navItems = new[] { NavAi, NavPersonality, NavVoiceInput, NavTrackMap, NavAlerts, NavAppearance };

        var config = PitWatch.Config.Load();

        _wizardMode = !isEditMode;
        Title = isEditMode ? "PitWatch Settings" : "Welcome to PitWatch";

        if (!config.GeminiApiKey.Contains("PASTE_YOUR"))
            ApiKeyBox.Password = config.GeminiApiKey;

        UseElevenLabsCheckBox.IsChecked = config.UseElevenLabs;
        ElevenLabsPanel.Visibility = config.UseElevenLabs ? Visibility.Visible : Visibility.Collapsed;
        ElevenLabsKeyBox.Password = config.ElevenLabsApiKey;
        ElevenLabsVoiceBox.Text = config.ElevenLabsVoiceId;
        VoiceRateSlider.Value = config.SpeechVoiceRate;
        VoiceVolumeSlider.Value = config.SpeechVoiceVolume;
        RadioBeepCheckBox.IsChecked = config.RadioBeepEnabled;

        SetPersonalityRadio(config.Personality);
        SetChattinessRadio(config.Chattiness);

        DarkModeRadio.IsChecked = config.ThemeMode != "Light";
        LightModeRadio.IsChecked = config.ThemeMode == "Light";
        ColorblindCheckBox.IsChecked = config.ColorblindMode;
        _selectedAccent = config.AccentColor;
        BuildAccentSwatches();

        ShowSpeedTraceCheck.IsChecked = config.ShowSpeedTrace;
        ShowPedalTraceCheck.IsChecked = config.ShowPedalTrace;
        ShowGForceCheck.IsChecked = config.ShowGForce;

        AnnounceOvertakesCheck.IsChecked = config.AnnounceOvertakes;
        AnnounceLapAnalysisCheck.IsChecked = config.AnnounceLapAnalysis;
        AnnounceProximityCheck.IsChecked = config.AnnounceProximity;
        AnnounceTyreTempsCheck.IsChecked = config.AnnounceTyreTemps;
        AnnounceStintSummaryCheck.IsChecked = config.AnnounceStintSummary;

        VoiceInputEnabledCheck.IsChecked = config.VoiceInputEnabled;
        _voiceBinding = config.VoiceInputBinding;
        UpdateBindingText();

        UpdateBroadcastingStatus(config);

        _callouts = PitWatch.Commands.CustomCallouts.ReadAll();
        foreach (var ev in PitWatch.Commands.CustomCallouts.KnownEvents)
        {
            CalloutEventList.Items.Add(new ListBoxItem
            {
                Content = PitWatch.Commands.CustomCallouts.DescribeEvent(ev),
                Tag = ev
            });
        }
        CalloutEventList.SelectedIndex = 0;

        GoToStep(0);
    }

    /// <summary>
    /// Shows a step and updates all the surrounding chrome. In wizard mode the footer
    /// becomes Back/Next and only turns into Finish on the last step, so a first-time user
    /// is walked through every section rather than landing on page one with a Save button
    /// and never discovering the rest.
    /// </summary>
    private void GoToStep(int index)
    {
        if (_pages.Length == 0) return;
        index = Math.Clamp(index, 0, _pages.Length - 1);
        _currentStep = index;
        _furthestStepReached = Math.Max(_furthestStepReached, index);

        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = SectionInfo[index].Title;
        SectionSubtitle.Text = SectionInfo[index].Subtitle;

        if (_navItems.Length == _pages.Length && !_navItems[index].IsChecked!.Value)
            _navItems[index].IsChecked = true;

        if (_wizardMode)
        {
            // Steps ahead of where you've reached stay locked, so the sidebar doubles as
            // a progress indicator instead of letting you skip past things unseen.
            for (int i = 0; i < _navItems.Length; i++)
                _navItems[i].IsEnabled = i <= _furthestStepReached;

            bool lastStep = index == _pages.Length - 1;
            BackButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
            ContinueButton.Content = lastStep ? "Finish" : "Next";
            StepCounterText.Text = $"Step {index + 1} of {_pages.Length}";
        }
        else
        {
            BackButton.Visibility = Visibility.Collapsed;
            ContinueButton.Content = "Save";
            StepCounterText.Text = "";
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoToStep(_currentStep - 1);

    private void BuildAccentSwatches()
    {
        AccentSwatches.Items.Clear();
        foreach (var (name, hex) in ThemeManager.AccentChoices)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var border = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(name == _selectedAccent ? 3 : 0),
                BorderBrush = (Brush)FindResource("TextPrimary"),
                ToolTip = name,
                Tag = name
            };
            border.MouseLeftButtonUp += (s, _) =>
            {
                _selectedAccent = (string)((Border)s).Tag;
                BuildAccentSwatches(); // rebuild to move the selection outline
            };
            AccentSwatches.Items.Add(border);
        }
    }

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (_pages.Length == 0) return; // fires during InitializeComponent before setup
        if (sender is not RadioButton rb || rb.Tag is not string tagStr) return;
        if (!int.TryParse(tagStr, out int index)) return;
        if (index == _currentStep) return; // already here, avoids recursing via GoToStep
        GoToStep(index);
    }

    private void SetPersonalityRadio(string personality)
    {
        PersonalityKind.IsChecked = personality == "Kind";
        PersonalityMean.IsChecked = personality == "Mean";
        PersonalityProfessional.IsChecked = personality == "Professional";
        PersonalityHelpful.IsChecked = personality is not ("Kind" or "Mean" or "Professional");
    }

    private void SetChattinessRadio(string chattiness)
    {
        ChattinessQuiet.IsChecked = chattiness == "Quiet";
        ChattinessChatty.IsChecked = chattiness == "Chatty";
        ChattinessNormal.IsChecked = chattiness is not ("Quiet" or "Chatty");
    }

    private void UpdateBroadcastingStatus(PitWatch.Config config)
    {
        BroadcastingStatusText.Text = config.BroadcastingEnabled ? "Currently ON." : "Currently OFF.";
        BroadcastingStatusText.Foreground = (Brush)FindResource(config.BroadcastingEnabled ? "AccentGreen" : "TextMuted");
    }

    private void UseElevenLabsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ElevenLabsPanel == null) return; // fires during InitializeComponent
        ElevenLabsPanel.Visibility = UseElevenLabsCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void BroadcastingButton_Click(object sender, RoutedEventArgs e)
    {
        var config = PitWatch.Config.Load();
        var result = BroadcastingAutoSetup.TryEnable(config);
        BroadcastingStatusText.Text = result.Message;
        BroadcastingStatusText.Foreground = (Brush)FindResource(result.Success ? "AccentGreen" : "AccentRed");
    }

    private string _voiceBinding = "";

    private void UpdateBindingText()
    {
        BindingText.Text = string.IsNullOrWhiteSpace(_voiceBinding) ? "Nothing bound" : _voiceBinding;
    }

    private async void BindButton_Click(object sender, RoutedEventArgs e)
    {
        BindButton.IsEnabled = false;
        BindingText.Text = "Press a button on your wheel...";

        // Poll for a few seconds waiting for any wheel button to go down.
        var deadline = DateTime.UtcNow.AddSeconds(6);
        string? detected = null;
        while (DateTime.UtcNow < deadline && detected == null)
        {
            detected = PitWatch.Voice.VoiceInput.DetectPressedButton();
            if (detected == null) await Task.Delay(60);
        }

        if (detected != null)
        {
            _voiceBinding = detected;
        }
        UpdateBindingText();
        if (detected == null) BindingText.Text = "Nothing detected - is the wheel plugged in?";
        BindButton.IsEnabled = true;
    }

    // ---------- Callout editor ----------
    // Same editor as Settings, so customising callouts never means hand-editing JSON.

    private void CalloutEvent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CalloutEventList.SelectedItem is not ListBoxItem item || item.Tag is not string key) return;

        _loadingCallout = true;
        _currentCalloutEvent = key;
        CalloutHintText.Text = $"{PitWatch.Commands.CustomCallouts.DescribeEvent(key)} - one line per variation.";
        CalloutTextBox.Text = _callouts.TryGetValue(key, out var lines)
            ? string.Join(Environment.NewLine, lines)
            : "";
        _loadingCallout = false;
    }

    private void CalloutText_Changed(object sender, TextChangedEventArgs e)
    {
        // Ignore the change fired while switching events, or selecting a different event
        // would immediately overwrite it with the previous one's text.
        if (_loadingCallout || _currentCalloutEvent == null) return;

        var lines = CalloutTextBox.Text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) _callouts.Remove(_currentCalloutEvent);
        else _callouts[_currentCalloutEvent] = lines;

        CalloutStatusText.Text = lines.Count == 0
            ? "Using the built-in lines for this event."
            : $"{lines.Count} custom line{(lines.Count == 1 ? "" : "s")} - saved when you finish setup.";
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        // Keys are optional - only validate if something was actually typed, so leaving
        // it blank just means "skip AI questions for now" rather than being an error.
        if (!string.IsNullOrWhiteSpace(key) && key.Length < 10)
        {
            ErrorText.Text = "That doesn't look like a full key - paste the whole thing, or clear the box to skip.";
            ErrorText.Visibility = Visibility.Visible;
            GoToStep(0); // jump back to the section with the problem
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;

        // In wizard mode this button is "Next" until the final step - advance rather than
        // finishing, so first-time users see every section before the app opens.
        if (_wizardMode && _currentStep < _pages.Length - 1)
        {
            GoToStep(_currentStep + 1);
            return;
        }

        var config = PitWatch.Config.Load();
        config.GeminiApiKey = key;
        config.UseElevenLabs = UseElevenLabsCheckBox.IsChecked == true;
        config.ElevenLabsApiKey = ElevenLabsKeyBox.Password.Trim();
        config.ElevenLabsVoiceId = string.IsNullOrWhiteSpace(ElevenLabsVoiceBox.Text)
            ? config.ElevenLabsVoiceId : ElevenLabsVoiceBox.Text.Trim();
        config.SpeechVoiceRate = (int)VoiceRateSlider.Value;
        config.SpeechVoiceVolume = (int)VoiceVolumeSlider.Value;
        config.RadioBeepEnabled = RadioBeepCheckBox.IsChecked == true;

        config.Personality = PersonalityKind.IsChecked == true ? "Kind"
            : PersonalityMean.IsChecked == true ? "Mean"
            : PersonalityProfessional.IsChecked == true ? "Professional"
            : "Helpful";

        config.Chattiness = ChattinessQuiet.IsChecked == true ? "Quiet"
            : ChattinessChatty.IsChecked == true ? "Chatty"
            : "Normal";

        config.ThemeMode = LightModeRadio.IsChecked == true ? "Light" : "Dark";
        config.ColorblindMode = ColorblindCheckBox.IsChecked == true;
        config.AccentColor = _selectedAccent;

        config.ShowSpeedTrace = ShowSpeedTraceCheck.IsChecked == true;
        config.ShowPedalTrace = ShowPedalTraceCheck.IsChecked == true;
        config.ShowGForce = ShowGForceCheck.IsChecked == true;

        config.AnnounceOvertakes = AnnounceOvertakesCheck.IsChecked == true;
        config.AnnounceLapAnalysis = AnnounceLapAnalysisCheck.IsChecked == true;
        config.AnnounceProximity = AnnounceProximityCheck.IsChecked == true;
        config.AnnounceTyreTemps = AnnounceTyreTempsCheck.IsChecked == true;
        config.AnnounceStintSummary = AnnounceStintSummaryCheck.IsChecked == true;
        config.VoiceInputEnabled = VoiceInputEnabledCheck.IsChecked == true;
        config.VoiceInputBinding = _voiceBinding;

        config.SetupCompleted = true;
        config.Save();
        PitWatch.Commands.CustomCallouts.WriteAll(_callouts);

        DialogResult = true;
        Close();
    }
}
