using System.Windows;
using System.Windows.Controls;

namespace PitWatch.Gui;

/// <summary>
/// An API key input that hides its contents by default, with a Show/Hide toggle.
///
/// Uses a real PasswordBox when hidden (so you can still type into it normally) and a
/// TextBox when revealed. Only one is ever visible, and the value is copied across at the
/// moment of toggling, so whichever box the user is looking at is always the live one -
/// reading Password always pulls from the currently visible control rather than a stale copy.
/// </summary>
public class ApiKeyBox : UserControl
{
    private readonly PasswordBox _hidden = new();
    private readonly TextBox _shown = new();
    private readonly Button _toggle = new();
    private bool _revealed;

    public string Password
    {
        get => _revealed ? _shown.Text : _hidden.Password;
        set
        {
            _hidden.Password = value ?? "";
            _shown.Text = value ?? "";
        }
    }

    public ApiKeyBox()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // PasswordBox has no themed style of its own in this app, so match the TextBox look.
        _hidden.Padding = new Thickness(8, 6, 8, 6);
        _hidden.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _hidden.SetResourceReference(ForegroundProperty, "TextPrimary");
        _hidden.SetResourceReference(BackgroundProperty, "ControlBg");
        _hidden.BorderThickness = new Thickness(0);

        _shown.Visibility = Visibility.Collapsed;

        Grid.SetColumn(_hidden, 0);
        Grid.SetColumn(_shown, 0);
        grid.Children.Add(_hidden);
        grid.Children.Add(_shown);

        _toggle.Content = "Show";
        _toggle.Padding = new Thickness(14, 6, 14, 6);
        _toggle.Margin = new Thickness(6, 0, 0, 0);
        _toggle.SetResourceReference(StyleProperty, "GhostButton");
        _toggle.Click += (_, _) => Toggle();
        Grid.SetColumn(_toggle, 1);
        grid.Children.Add(_toggle);

        Content = grid;
    }

    private void Toggle()
    {
        if (_revealed)
        {
            _hidden.Password = _shown.Text;   // carry the edited value back
            _shown.Visibility = Visibility.Collapsed;
            _hidden.Visibility = Visibility.Visible;
            _toggle.Content = "Show";
        }
        else
        {
            _shown.Text = _hidden.Password;   // carry the edited value forward
            _hidden.Visibility = Visibility.Collapsed;
            _shown.Visibility = Visibility.Visible;
            _toggle.Content = "Hide";
        }
        _revealed = !_revealed;
    }
}
