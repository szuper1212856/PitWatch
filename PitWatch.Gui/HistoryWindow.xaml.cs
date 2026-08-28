using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PitWatch.History;
using PitWatch.Models;

namespace PitWatch.Gui;

public partial class HistoryWindow : Window
{
    private SessionRecord? _primary;
    private SessionRecord? _comparison;

    public HistoryWindow()
    {
        InitializeComponent();
        LoadSessionList();
    }

    private void LoadSessionList()
    {
        var files = SessionRecorder.ListSavedSessions();

        // Build the game filter from what's actually been recorded, so it only offers
        // games the user has really played rather than a hardcoded list.
        GameFilterCombo.Items.Clear();
        GameFilterCombo.Items.Add(new ComboBoxItem { Content = "All games", Tag = null });
        foreach (var game in files.Select(GameOf).Where(g => g != null).Distinct().OrderBy(g => g))
        {
            GameFilterCombo.Items.Add(new ComboBoxItem { Content = game, Tag = game });
        }
        GameFilterCombo.SelectedIndex = 0; // triggers GameFilter_Changed, which fills the list

        if (files.Count == 0)
            SummaryText.Text = "No saved sessions yet - they save automatically when a session ends.";
    }

    private void GameFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SessionListBox == null || CompareCombo == null) return; // fires during setup

        string? filter = (GameFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var files = SessionRecorder.ListSavedSessions()
            .Where(f => filter == null || GameOf(f) == filter)
            .ToList();

        SessionListBox.Items.Clear();
        foreach (var f in files)
        {
            SessionListBox.Items.Add(new ListBoxItem { Content = ListLabel(f), Tag = f });
        }

        // Comparison only makes sense within the same game, so it follows the filter too.
        CompareCombo.Items.Clear();
        CompareCombo.Items.Add(new ComboBoxItem { Content = "(none)", Tag = null });
        foreach (var f in files)
        {
            CompareCombo.Items.Add(new ComboBoxItem { Content = FriendlyName(f), Tag = f });
        }
        CompareCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Turns a session filename into something readable. Handles both the current
    /// "session_ACC_2026-08-05_14-30-00" format and the older "session_2026-08-05_14-30-00"
    /// one, so sessions recorded before the game name was added still display properly.
    /// </summary>
    private static string FriendlyName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path).Replace("session_", "");

        string game = "";
        int firstUnderscore = name.IndexOf('_');
        if (firstUnderscore > 0 && !char.IsDigit(name[0]))
        {
            game = name[..firstUnderscore];
            name = name[(firstUnderscore + 1)..];
        }

        string when = DateTime.TryParseExact(name, "yyyy-MM-dd_HH-mm-ss", null,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToString("MMM d, HH:mm")
            : name;

        return game.Length > 0 ? $"{game}  ·  {when}" : when;
    }

    /// <summary>Extracts the game from a session filename, or null for older files.</summary>
    /// <summary>
    /// Label for the session list. Loads the track name from the file, falling back to the
    /// filename-derived label for older sessions recorded before tracks were captured.
    /// </summary>
    private string ListLabel(string path)
    {
        if (_summaryCache.TryGetValue(path, out var cached)) return cached;

        string label = FriendlyName(path);
        try
        {
            var rec = SessionRecorder.Load(path);
            if (rec != null && !string.IsNullOrWhiteSpace(rec.TrackName))
            {
                string best = rec.BestLapTimeSeconds > 0
                    ? "  ·  " + GameState.FormatLapTime(rec.BestLapTimeSeconds)
                    : "";
                label = $"{PrettyTrackName(rec.TrackName)}{best}\n{rec.StartedAt:d MMM, HH:mm}";
            }
        }
        catch { /* fall back to the filename label */ }

        _summaryCache[path] = label;
        return label;
    }

    private readonly Dictionary<string, string> _summaryCache = new();

    private static string? GameOf(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path).Replace("session_", "");
        int firstUnderscore = name.IndexOf('_');
        if (firstUnderscore > 0 && !char.IsDigit(name[0])) return name[..firstUnderscore];
        return null;
    }

    private void SessionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionListBox.SelectedItem is not ListBoxItem item || item.Tag is not string path) return;

        _primary = SessionRecorder.Load(path);
        if (_primary == null || _primary.Snapshots.Count == 0)
        {
            SummaryText.Text = "Couldn't load this session (empty or corrupted file).";
            return;
        }

        LegendPrimary.Text = FriendlyName(path);
        RefreshAll();
    }

    private void CompareCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompareCombo.SelectedItem is not ComboBoxItem item) return;
        _comparison = item.Tag is string path ? SessionRecorder.Load(path) : null;
        LegendCompare.Text = item.Tag is string p ? FriendlyName(p) : "No comparison";
        RefreshAll();
    }

    private void RefreshAll()
    {
        if (_primary == null) return;

        int laps = _primary.LapTimes.Count > 0 ? _primary.LapTimes.Count : _primary.Snapshots[^1].Lap;
        var duration = TimeSpan.FromSeconds(_primary.Snapshots[^1].TimestampSeconds);

        // Track first and largest - without it a list of lap times means nothing, since
        // you can't compare a 2:18 at Spa with a 1:33 at Monza.
        TrackHeaderText.Text = string.IsNullOrWhiteSpace(_primary.TrackName)
            ? "Unknown track"
            : PrettyTrackName(_primary.TrackName);

        var bits = new List<string>
        {
            _primary.StartedAt.ToString("dddd d MMMM, HH:mm"),
            $"{duration.TotalMinutes:F0} min",
        };
        if (!string.IsNullOrWhiteSpace(_primary.SessionKindName)) bits.Insert(0, _primary.SessionKindName);
        if (!string.IsNullOrWhiteSpace(_primary.CarModel)) bits.Add(PrettyTrackName(_primary.CarModel));
        if (_primary.FinalPosition > 0) bits.Add($"Finished P{_primary.FinalPosition}");
        SummaryText.Text = string.Join("  ·  ", bits);

        ShowProgressAtTrack();
        DirectorText.Text = _primary.RaceDirectorSummary;

        StatBestLap.Text = _primary.BestLapTimeSeconds > 0 ? GameState.FormatLapTime(_primary.BestLapTimeSeconds) : "--";
        StatTheoretical.Text = _primary.TheoreticalBestSeconds > 0 ? GameState.FormatLapTime(_primary.TheoreticalBestSeconds) : "--";
        StatAverage.Text = _primary.AverageLap.HasValue ? GameState.FormatLapTime(_primary.AverageLap.Value) : "--";
        StatConsistency.Text = _primary.Consistency.HasValue ? $"±{_primary.Consistency.Value:F2}s" : "--";
        StatLaps.Text = laps.ToString();

        DrawLapChart();
        DrawSpeed();
        BuildLapTable();
        DrawMap();
    }

    /// <summary>Lap-time chart, with the comparison session overlaid so improvements
    /// between two runs are visible at a glance.</summary>
    /// <summary>
    /// Answers the question a history page actually exists for: am I getting faster here?
    /// Compares this session's best against previous sessions at the same track.
    /// </summary>
    private void ShowProgressAtTrack()
    {
        if (_primary == null || _primary.BestLapTimeSeconds <= 0)
        {
            ProgressText.Text = "";
            return;
        }

        var sameTrack = SessionRecorder.LoadAllSummaries()
            .Where(x => string.Equals(x.TrackName, _primary.TrackName, StringComparison.OrdinalIgnoreCase)
                        && x.BestLap > 0
                        && x.StartedAt < _primary.StartedAt)
            .OrderByDescending(x => x.StartedAt)
            .ToList();

        if (sameTrack.Count == 0)
        {
            ProgressText.Text = "First recorded session at this track - future ones will compare against it.";
            ProgressText.Foreground = (Brush)FindResource("TextMuted");
            return;
        }

        float previousBest = sameTrack.Min(x => x.BestLap);
        float delta = _primary.BestLapTimeSeconds - previousBest;

        if (delta < -0.01f)
        {
            ProgressText.Text = $"Personal best at this track - {-delta:F2}s faster than your previous best of "
                              + $"{GameState.FormatLapTime(previousBest)} across {sameTrack.Count} earlier session"
                              + (sameTrack.Count == 1 ? "." : "s.");
            ProgressText.Foreground = (Brush)FindResource("AccentGreen");
        }
        else
        {
            ProgressText.Text = $"{delta:F2}s off your best here ({GameState.FormatLapTime(previousBest)}), "
                              + $"set across {sameTrack.Count} earlier session" + (sameTrack.Count == 1 ? "." : "s.");
            ProgressText.Foreground = (Brush)FindResource("TextMuted");
        }
    }

    /// <summary>
    /// ACC reports internal names like "monza" or "porsche_991ii_gt3_r". Tidying them up
    /// makes the history page readable instead of looking like a config file.
    /// </summary>
    private static string PrettyTrackName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var words = raw.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w =>
            w.Length <= 3 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private void DrawLapChart()
    {
        LapChartCanvas.Children.Clear();
        double w = LapChartCanvas.ActualWidth, h = LapChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _primary == null) return;

        var series = new List<(List<float> Times, Brush Colour)>();
        if (_primary.LapTimes.Count > 0) series.Add((_primary.LapTimes, (Brush)FindResource("AccentGreen")));
        if (_comparison?.LapTimes.Count > 0) series.Add((_comparison.LapTimes, (Brush)FindResource("AccentPurple")));
        if (series.Count == 0) return;

        // Shared scale across both series so the comparison is meaningful
        var all = series.SelectMany(s => s.Times).ToList();
        float min = all.Min(), max = all.Max();
        float range = Math.Max(max - min, 0.5f);
        int maxLaps = series.Max(s => s.Times.Count);
        double margin = 10;

        foreach (var (times, colour) in series)
        {
            var pts = new PointCollection();
            for (int i = 0; i < times.Count; i++)
            {
                double x = margin + (maxLaps <= 1 ? 0 : (double)i / (maxLaps - 1) * (w - margin * 2));
                double y = margin + (1 - (times[i] - min) / range) * (h - margin * 2);
                pts.Add(new Point(x, y));
            }
            LapChartCanvas.Children.Add(new Polyline { Points = pts, Stroke = colour, StrokeThickness = 2 });

            foreach (var p in pts)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = colour };
                Canvas.SetLeft(dot, p.X - 2.5);
                Canvas.SetTop(dot, p.Y - 2.5);
                LapChartCanvas.Children.Add(dot);
            }
        }

        // Fastest/slowest reference labels so the axis means something
        LapChartCanvas.Children.Add(new TextBlock
        {
            Text = GameState.FormatLapTime(min), FontSize = 10,
            Foreground = (Brush)FindResource("TextMuted")
        });
        var slow = new TextBlock
        {
            Text = GameState.FormatLapTime(max), FontSize = 10,
            Foreground = (Brush)FindResource("TextMuted")
        };
        Canvas.SetTop(slow, h - 16);
        LapChartCanvas.Children.Add(slow);
    }

    private void DrawSpeed()
    {
        SpeedCanvas.Children.Clear();
        double w = SpeedCanvas.ActualWidth, h = SpeedCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _primary == null) return;

        DrawSeries(SpeedCanvas, _primary.Snapshots.Select(s => (s.TimestampSeconds, s.SpeedKmh)).ToList(),
            0, 300, (Brush)FindResource("AccentGreen"), w, h);

        if (_comparison != null)
            DrawSeries(SpeedCanvas, _comparison.Snapshots.Select(s => (s.TimestampSeconds, s.SpeedKmh)).ToList(),
                0, 300, (Brush)FindResource("AccentPurple"), w, h, opacity: 0.6);
    }

    /// <summary>
    /// Builds the lap-by-lap table. This replaced the fuel graph, which looked tidy but
    /// told you almost nothing - a line sloping down. A table of every lap with its delta
    /// to your best is what you actually study after a session.
    /// </summary>
    private void BuildLapTable()
    {
        LapTable.Items.Clear();
        if (_primary == null || _primary.LapTimes.Count == 0)
        {
            LapTable.Items.Add(new TextBlock
            {
                Text = "No completed laps recorded.",
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0)
            });
            return;
        }

        var valid = _primary.LapTimes.Where(t => t > 1 && t < 3600).ToList();
        if (valid.Count == 0) return;
        float best = valid.Min();

        // Position per lap comes from the snapshots - take the last known position
        // recorded during each lap number.
        var posByLap = _primary.Snapshots
            .Where(sn => sn.Position > 0)
            .GroupBy(sn => sn.Lap)
            .ToDictionary(g => g.Key, g => g.Last().Position);

        for (int i = 0; i < _primary.LapTimes.Count; i++)
        {
            float t = _primary.LapTimes[i];
            if (t <= 1 || t >= 3600) continue;

            int lapNo = i + 1;
            float delta = t - best;
            bool isBest = Math.Abs(t - best) < 0.001f;

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            row.Children.Add(Cell($"{lapNo}", 0, (Brush)FindResource("TextMuted")));
            row.Children.Add(Cell(GameState.FormatLapTime(t), 1,
                isBest ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextPrimary"),
                bold: isBest));
            row.Children.Add(Cell(isBest ? "best" : $"+{delta:F2}", 2,
                isBest ? (Brush)FindResource("AccentGreen") : (Brush)FindResource("TextMuted")));
            row.Children.Add(Cell(posByLap.TryGetValue(lapNo, out int p) ? $"P{p}" : "-", 3,
                (Brush)FindResource("TextMuted")));

            LapTable.Items.Add(row);
        }
    }

    private static TextBlock Cell(string text, int column, Brush colour, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = colour,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };
        Grid.SetColumn(tb, column);
        return tb;
    }

    private static void DrawSeries(Canvas canvas, List<(double T, float V)> data,
        float minV, float maxV, Brush colour, double w, double h, double opacity = 1.0)
    {
        if (data.Count < 2) return;
        double maxT = Math.Max(data[^1].T, 1);
        float range = Math.Max(maxV - minV, 0.001f);

        var pts = new PointCollection();
        foreach (var (t, v) in data)
        {
            double x = t / maxT * w;
            double y = h - (v - minV) / range * h;
            pts.Add(new Point(x, y));
        }
        canvas.Children.Add(new Polyline { Points = pts, Stroke = colour, StrokeThickness = 2, Opacity = opacity });
    }

    private void DrawMap()
    {
        MapCanvas.Children.Clear();
        if (_primary == null) return;

        var withPos = _primary.Snapshots.Where(s => s.HasWorldPos).ToList();
        if (withPos.Count < 2)
        {
            MapStatusText.Text = "No track data - car proximity/track map wasn't on for this session.";
            MapStatusText.Visibility = Visibility.Visible;
            return;
        }
        MapStatusText.Visibility = Visibility.Collapsed;

        double w = MapCanvas.ActualWidth, h = MapCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double minX = withPos.Min(s => s.WorldX), maxX = withPos.Max(s => s.WorldX);
        double minY = withPos.Min(s => s.WorldY), maxY = withPos.Max(s => s.WorldY);
        double rangeX = Math.Max(maxX - minX, 1), rangeY = Math.Max(maxY - minY, 1);
        double margin = 12;

        // Preserve aspect ratio so the circuit shape stays true
        double scale = Math.Min((w - margin * 2) / rangeX, (h - margin * 2) / rangeY);
        double offX = (w - rangeX * scale) / 2, offY = (h - rangeY * scale) / 2;

        var pts = new PointCollection(withPos.Select(s =>
            new Point(offX + (s.WorldX - minX) * scale, offY + (s.WorldY - minY) * scale)));

        MapCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = (Brush)FindResource("AccentBlue"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });
    }
}
