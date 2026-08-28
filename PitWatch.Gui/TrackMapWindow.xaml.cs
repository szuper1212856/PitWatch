using System.Windows;
using System.Windows.Controls;

namespace PitWatch.Gui;

/// <summary>
/// Pop-out track map. Deliberately does no drawing of its own - MainWindow's existing
/// DrawTrackMap routine renders into this window's canvas too, so both views always show
/// identical data and there's only one drawing implementation to maintain.
/// </summary>
public partial class TrackMapWindow : Window
{
    public Canvas MapCanvas => PopoutMapCanvas;
    public TextBlock StatusText => PopoutStatusText;

    public TrackMapWindow()
    {
        InitializeComponent();
    }

    private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopCheck.IsChecked == true;
    }
}
