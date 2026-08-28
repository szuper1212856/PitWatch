using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PitWatch.Gui;

/// <summary>
/// Injects theme (Dark/Light), accent palette, and control styles into Application.Resources.
/// Must run before any window is created, since App.xaml's Styles use DynamicResource to
/// defer lookup until this has run.
/// </summary>
public static class ThemeManager
{
    /// <summary>User-selectable accent colors. Key is what gets stored in config.</summary>
    public static readonly (string Name, string Hex)[] AccentChoices =
    {
        ("Green", "#3DDC84"),
        ("Red", "#FF3B4E"),
        ("Blue", "#4FA3FF"),
        ("Amber", "#FFB300"),
        ("Purple", "#B084FF"),
        ("Cyan", "#22D3EE"),
    };

    public static void Apply(string themeMode, bool colorblindMode, string accentName = "Green")
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Clear();

        string themeFile = themeMode == "Light" ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        string accentFile = colorblindMode ? "Themes/AccentsColorblind.xaml" : "Themes/AccentsNormal.xaml";

        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(accentFile, UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Controls.xaml", UriKind.Relative) });

        // Custom accent overrides the palette's primary color - skipped in colorblind mode,
        // where the whole point is a specific tested-safe palette that shouldn't be altered.
        if (!colorblindMode)
        {
            var match = AccentChoices.FirstOrDefault(a => a.Name == accentName);
            if (match.Hex != null)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(match.Hex));
                brush.Freeze();
                app.Resources["AccentGreen"] = brush; // key name kept for compatibility across all existing XAML
            }
        }
    }
}
