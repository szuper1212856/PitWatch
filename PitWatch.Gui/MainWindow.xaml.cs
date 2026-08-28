using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PitWatch.AI;
using PitWatch.Commands;
using PitWatch.Models;
using PitWatch.Telemetry;
using PitWatch.Voice;

namespace PitWatch.Gui;

public partial class MainWindow : Window
{
    private readonly PitWatch.Config _config;
    private GeminiClient _gemini;
    private readonly SpeechOutput _speechOut;
    private readonly FuelTracker _fuelTracker = new();
    private readonly EventWatcher _eventWatcher = new();
    private readonly IdleChatter _idleChatter = new();
    private readonly LapAnalyzer _lapAnalyzer = new();
    private readonly RaceRules _raceRules = new();
    private readonly StintTracker _stintTracker = new();
    private readonly CustomCallouts _customCallouts = new();
    private readonly PitWatch.Voice.VoiceInput _voiceInput = new();
    private bool _voiceButtonWasHeld;
    private readonly ProximityWatcher _proximityWatcher = new();
    private readonly AccTelemetryReader _accReader = new();
    private readonly LmuTelemetryReader _lmuReader = new();
    private BroadcastingClient? _broadcasting;
    private readonly PitWatch.History.SessionRecorder _recorder = new();
    private TrackMapWindow? _popoutMap;
    private readonly UpdateService _updates = new();
    private UpdateCheckResult? _pendingUpdate;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Queue<float> _speedHistory = new();
    private readonly Queue<(float Throttle, float Brake, float Steer)> _pedalHistory = new();
    private readonly List<List<Point>> _trackMapSegments = new() { new List<Point>() };
    private const int HistoryCapacity = 300; // 15s at 20Hz

    private GameState _lastState = new();
    private bool _wasGameRunning = false;
    private bool _wasBroadcastingConnected = false;

    public MainWindow()
    {
        InitializeComponent();

        _config = PitWatch.Config.Load();
        _gemini = new GeminiClient(_config.GeminiApiKey, _config.GeminiModel, _config.Personality);
        _speechOut = new SpeechOutput(_config.SpeechVoiceRate, _config.SpeechVoiceVolume,
            _config.UseElevenLabs, _config.ElevenLabsApiKey, _config.ElevenLabsVoiceId, _config.RadioBeepEnabled);
        _speechOut.Spoken += OnSpoken;
        // Voice problems (quota exhausted, bad key) used to vanish into a nonexistent
        // console - now they appear in the radio transcript where they'll be noticed.
        _speechOut.Notice += msg => AddTranscriptLine($"[!] {msg}");

        if (_config.BroadcastingEnabled)
        {
            _broadcasting = new BroadcastingClient(_config.BroadcastingIp, _config.BroadcastingPort,
                "PitWatch", _config.BroadcastingPassword, _config.BroadcastingCommandPassword);
            _broadcasting.Start();
        }

        _customCallouts.Load();
        ApplyPanelVisibility();
        DiagnosticsTab.Visibility = _config.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;

        _timer.Tick += Timer_Tick;
        _timer.Start();

        // Fire and forget - a slow or failed update check must never delay the dashboard.
        _ = CheckForUpdatesAsync();
    }

    private void ApplyPanelVisibility()
    {
        SpeedTracePanel.Visibility = _config.ShowSpeedTrace ? Visibility.Visible : Visibility.Collapsed;
        PedalTracePanel.Visibility = _config.ShowPedalTrace ? Visibility.Visible : Visibility.Collapsed;
        GForcePanel.Visibility = _config.ShowGForce ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSpoken(string text)
    {
        // Spoken fires from a background context in some cases (e.g. broadcasting receive
        // loop indirectly) - Dispatcher.Invoke keeps UI updates safely on the UI thread.
        Dispatcher.Invoke(() =>
        {
            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (TranscriptList.Items.Count > 200) TranscriptList.Items.RemoveAt(0);
            if (TranscriptList.Items.Count > 0)
                TranscriptList.ScrollIntoView(TranscriptList.Items[^1]);
        });
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        ITelemetryProvider? active = null;
        if (_accReader.IsAvailable()) active = _accReader;
        else if (_lmuReader.IsAvailable()) active = _lmuReader;

        GameState state = active?.ReadState() ?? new GameState { IsGameRunning = false };
        _lastState = state;

        // A new session starting is the reliable signal for "possibly a different track now"
        // (Spa -> Nurburgring means the old session ended and a new one began) - clearing
        // here avoids the previous track's layout getting drawn over by the new one.
        // Clear the map on a new session OR when the broadcasting connection drops and
        // comes back (which is what happens when you restart ACC or load a new session) -
        // otherwise the old track's shape stays on screen and the new one draws over it.
        bool broadcastingNow = _broadcasting?.IsConnected ?? false;
        bool newSession = state.IsGameRunning && !_wasGameRunning;
        bool reconnected = broadcastingNow && !_wasBroadcastingConnected;

        if (newSession)
        {
            // Race rules are per-race: carrying them into the next session would silently
            // produce wrong strategy calls.
            _raceRules.Clear();
        }

        if (newSession || reconnected)
        {
            _trackMapSegments.Clear();
            _trackMapSegments.Add(new List<Point>());
        }
        if (newSession)
        {
            // Fresh session means fresh reference data - otherwise last race's best lap
            // and stint history bleed into the new one.
            _lapAnalyzer.Reset();
            _stintTracker.Reset();
        }
        _wasGameRunning = state.IsGameRunning;
        _wasBroadcastingConnected = broadcastingNow;

        if (state.IsGameRunning)
        {
            _fuelTracker.Update(state);
        }

        _lapAnalyzer.Update(state);
        _stintTracker.Update(state);
        _eventWatcher.Update(state, _speechOut, _config, _lapAnalyzer, _stintTracker, _customCallouts);
        PollVoiceButton();
        _idleChatter.MaybeChat(state, _speechOut, _config);
        if (_broadcasting != null && _config.AnnounceProximity)
        {
            _proximityWatcher.Update(_broadcasting, state.Position, _speechOut);
        }

        UpdateStats(state);
        UpdateStrategyTab(state);
        if (_config.DeveloperMode) UpdateDiagnostics(state);
        UpdateHistoryBuffers(state);
        if (_config.ShowSpeedTrace) DrawSpeedTrace();
        if (_config.ShowPedalTrace) DrawPedalTrace();
        if (_config.ShowGForce) DrawGForce(state);
        DrawTrackMap(state);

        _recorder.AttachAnalysis(
            _lapAnalyzer.Laps.Where(l => l.WasValid).Select(l => l.LapTime),
            _lapAnalyzer.TheoreticalBest ?? 0f,
            _lapAnalyzer.BestSectors.Select(x => x ?? 0f).ToArray(),
            0,
            _stintTracker.BuildRaceDirectorSummary(state, _lapAnalyzer));

        _recorder.Update(state, myPos => _broadcasting != null && _broadcasting.TryGetSelfPosition(myPos, out float x, out float y)
            ? (x, y)
            : null);
    }

    private string _lastCapabilityGame = "";

    /// <summary>
    /// Shows or hides panels based on what the running game actually provides, so LMU
    /// doesn't display a wall of empty ACC-shaped panels. Only runs when the game changes,
    /// since toggling visibility every tick would be wasteful.
    /// </summary>
    private void ApplyGameCapabilities(GameState state)
    {
        string key = state.IsGameRunning ? state.GameName : "";
        if (key == _lastCapabilityGame) return;
        _lastCapabilityGame = key;

        // With no game running, show everything - otherwise the dashboard looks broken
        // before you've even started.
        bool running = state.IsGameRunning;
        Visibility Show(bool available) => (!running || available) ? Visibility.Visible : Visibility.Collapsed;

        TyrePanel.Visibility = Show(state.HasTyreData);
        DamagePanel.Visibility = Show(state.HasDamageData);
        TimeLeftPanel.Visibility = Show(state.HasSessionTimeData);
        PositionHeaderText.Visibility = Show(state.HasPositionData);
        GForcePanel.Visibility = (running && !state.HasGForceData) ? Visibility.Collapsed
            : (_config.ShowGForce ? Visibility.Visible : Visibility.Collapsed);
        TrackMapTab.Visibility = Show(state.SupportsTrackMap);
        StrategyLapPanel.Visibility = Show(state.HasSectorData);
        PacePanel.Visibility = Show(state.HasSectorData);

        if (running && !state.SupportsTrackMap)
        {
            LimitedGameNote.Text = $"Running {state.GameName}. It provides less telemetry than ACC, so panels that need the missing data are hidden - see Settings, Games for what's supported.";
            LimitedGamePanel.Visibility = Visibility.Visible;
        }
        else
        {
            LimitedGamePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateStats(GameState state)
    {
        ApplyGameCapabilities(state);

        ConnectionText.Text = state.IsGameRunning ? $"Connected - {state.GameName}" : "Waiting for ACC or LMU...";
        var statusBrush = state.IsGameRunning
            ? (Brush)FindResource("AccentGreen")
            : (Brush)FindResource("TextMuted");
        ConnectionText.Foreground = statusBrush;
        ConnectionDot.Fill = statusBrush;

        // Showing the session type makes it obvious PitWatch knows this is practice, not
        // a race - so the absence of position callouts reads as intentional.
        SessionText.Text = state.IsGameRunning
            ? $"{state.KindName.ToUpperInvariant()}  ·  LAP {state.CurrentLap}"
            : "--";
        PositionHeaderText.Text = state.IsGameRunning && state.Position > 0 ? GameState.SpokenPosition(state.Position) : "--";

        SpeedText.Text = state.IsGameRunning ? $"{state.SpeedKmh:F0}" : "0";
        GearText.Text = $"Gear: {GearLabel(state)}";
        RpmBar.Value = state.Rpm;

        FuelText.Text = state.IsGameRunning ? $"{state.FuelLiters:F1} L" : "-- L";
        FuelLapsText.Text = state.EstimatedLapsOfFuelLeft >= 0 ? $"~{state.EstimatedLapsOfFuelLeft} laps left" : "calculating...";

        TyreFLText.Text = $"FL {state.TyreTempCelsius[0]:F0}C / {state.TyrePressurePsi[0]:F1}";
        TyreFRText.Text = $"FR {state.TyreTempCelsius[1]:F0}C / {state.TyrePressurePsi[1]:F1}";
        TyreRLText.Text = $"RL {state.TyreTempCelsius[2]:F0}C / {state.TyrePressurePsi[2]:F1}";
        TyreRRText.Text = $"RR {state.TyreTempCelsius[3]:F0}C / {state.TyrePressurePsi[3]:F1}";

        CurrentLapTimeText.Text = $"Current: {GameState.FormatLapTime(state.CurrentLapTimeSeconds)}";
        LastLapTimeText.Text = $"Last: {GameState.FormatLapTime(state.LastLapTimeSeconds)}";
        BestLapTimeText.Text = $"Best: {GameState.FormatLapTime(state.BestLapTimeSeconds)}";

        DamageText.Text = DamageFormatter.FormatQuery(state.CarDamageRaw);
        TimeLeftText.Text = state.IsGameRunning && state.SessionTimeLeftSeconds > 0
            ? $"{(int)(state.SessionTimeLeftSeconds / 60):00}:{(int)(state.SessionTimeLeftSeconds % 60):00}"
            : "-- : --";
    }

    private void UpdateStrategyTab(GameState state)
    {
        if (!state.IsGameRunning) return;

        StrategyRulesText.Text = _raceRules.Describe();
        UpdateRacePlan(state);

        var plan = StrategyEngine.Build(state, _raceRules);
        if (plan != null)
        {
            StrategyFuelText.Text = plan.Summary;
            QuickStrategyText.Text = StrategyEngine.QuickSummary(state, _raceRules);
            StrategyPitText.Text = plan.StopsRequired == 0
                ? "No fuel stop required."
                : $"{plan.StopsRequired} stop{(plan.StopsRequired == 1 ? "" : "s")} required, first by lap {plan.BoxByLap}."
                  + (plan.SaveNeededPerLap is > 0 and < 0.3f
                      ? $"  Or save {plan.SaveNeededPerLap:F2} L/lap to avoid stopping."
                      : "");
        }
        else
        {
            StrategyFuelText.Text = "Building fuel data - complete a lap.";
            StrategyPitText.Text = "Building fuel data - complete a lap.";
            QuickStrategyText.Text = "Building data...";
        }

        StrategyLapText.Text = _lapAnalyzer.WhereAmISlow();

        // Pace panel: best, theoretical best, and live delta vs your best lap
        var best = _lapAnalyzer.BestLapTime;
        var theoretical = _lapAnalyzer.TheoreticalBest;
        PaceBestText.Text = best.HasValue ? GameState.FormatLapTime(best.Value) : "--:--";
        PaceTheoreticalText.Text = theoretical.HasValue ? GameState.FormatLapTime(theoretical.Value) : "--:--";
        PaceOnTableText.Text = (best.HasValue && theoretical.HasValue)
            ? $"{best.Value - theoretical.Value:F2}s on the table"
            : "";

        var sectors = _lapAnalyzer.BestSectors;
        PaceSectorsText.Text = string.Join("   ", sectors.Select((sec, i) =>
            $"S{i + 1} {(sec.HasValue ? sec.Value.ToString("F1") : "--")}"));

        var delta = _lapAnalyzer.LiveGhostDelta;
        if (delta.HasValue)
        {
            GhostDeltaText.Text = (delta.Value >= 0 ? "+" : "") + delta.Value.ToString("F2");
            GhostDeltaText.Foreground = (Brush)FindResource(delta.Value <= 0 ? "AccentGreen" : "AccentRed");
        }
        else
        {
            GhostDeltaText.Text = "--";
            GhostDeltaText.Foreground = (Brush)FindResource("TextMuted");
        }

        // Tyre advice
        TyreAdviceText.Text = TyreAdvisor.Analyze(state);

        // Stints
        StintList.Items.Clear();
        foreach (var st in _stintTracker.Stints)
        {
            string line = $"Stint {st.StintNumber}: {st.LapCount} laps";
            if (st.AverageLap.HasValue) line += $" · avg {GameState.FormatLapTime(st.AverageLap.Value)}";
            if (st.BestLap.HasValue) line += $" · best {GameState.FormatLapTime(st.BestLap.Value)}";
            if (st.FuelUsed > 0) line += $" · {st.FuelUsed:F1}L";
            if (st.Consistency.HasValue) line += $" · ±{st.Consistency.Value:F2}s";
            StintList.Items.Add(line);
        }
        if (_stintTracker.Stints.Count == 0)
            StintList.Items.Add("No completed stints yet.");
    }

    private static string GearLabel(GameState state) => state.Gear switch
    {
        0 => "R",
        1 => "N",
        >= 2 => (state.Gear - 1).ToString(),
        _ => "N"
    };

    private void UpdateHistoryBuffers(GameState state)
    {
        _speedHistory.Enqueue(state.SpeedKmh);
        if (_speedHistory.Count > HistoryCapacity) _speedHistory.Dequeue();

        _pedalHistory.Enqueue((state.Throttle, state.Brake, state.SteerAngle));
        if (_pedalHistory.Count > HistoryCapacity) _pedalHistory.Dequeue();
    }

    private void DrawSpeedTrace()
    {
        SpeedTraceCanvas.Children.Clear();
        double w = SpeedTraceCanvas.ActualWidth, h = SpeedTraceCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _speedHistory.Count < 2) return;

        var points = new PointCollection();
        var samples = _speedHistory.ToArray();
        // Fixed ceiling instead of scaling to the local window's max - that was the cause
        // of the "breathing" effect, since the vertical scale silently changed every
        // frame as the max speed within the trailing window rose and fell naturally.
        const float maxScale = 300f; // generous ceiling for GT3-class cars
        for (int i = 0; i < samples.Length; i++)
        {
            double x = w * i / (HistoryCapacity - 1);
            double y = h - (samples[i] / maxScale) * h;
            points.Add(new Point(x, y));
        }
        SpeedTraceCanvas.Children.Add(new Polyline { Points = points, Stroke = (Brush)FindResource("AccentGreen"), StrokeThickness = 2 });
    }

    private void DrawPedalTrace()
    {
        PedalTraceCanvas.Children.Clear();
        double w = PedalTraceCanvas.ActualWidth, h = PedalTraceCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _pedalHistory.Count < 2) return;

        var samples = _pedalHistory.ToArray();
        var throttlePts = new PointCollection();
        var brakePts = new PointCollection();
        var steerPts = new PointCollection();

        // Auto-scale steering to whatever range is actually observed, rather than a
        // fixed guessed divisor - that was crushing real steering input down to a
        // nearly-flat line since ACC's steerAngle range wasn't what was assumed.
        float maxAbsSteer = 0.01f; // avoid divide-by-zero
        foreach (var s in samples) maxAbsSteer = Math.Max(maxAbsSteer, Math.Abs(s.Steer));

        for (int i = 0; i < samples.Length; i++)
        {
            double x = w * i / (HistoryCapacity - 1);
            throttlePts.Add(new Point(x, h - samples[i].Throttle * h));
            brakePts.Add(new Point(x, h - samples[i].Brake * h));
            double steerNorm = samples[i].Steer / maxAbsSteer;
            steerPts.Add(new Point(x, h / 2 - steerNorm * (h / 2)));
        }

        PedalTraceCanvas.Children.Add(new Polyline { Points = throttlePts, Stroke = (Brush)FindResource("AccentGreen"), StrokeThickness = 2 });
        PedalTraceCanvas.Children.Add(new Polyline { Points = brakePts, Stroke = (Brush)FindResource("AccentRed"), StrokeThickness = 2 });
        PedalTraceCanvas.Children.Add(new Polyline { Points = steerPts, Stroke = (Brush)FindResource("AccentPurple"), StrokeThickness = 1.5 });
    }

    private void DrawGForce(GameState state)
    {
        GForceCanvas.Children.Clear();
        double w = GForceCanvas.ActualWidth, h = GForceCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Crosshair
        GForceCanvas.Children.Add(new Line { X1 = w / 2, Y1 = 0, X2 = w / 2, Y2 = h, Stroke = (Brush)FindResource("TextMuted"), StrokeThickness = 0.5 });
        GForceCanvas.Children.Add(new Line { X1 = 0, Y1 = h / 2, X2 = w, Y2 = h / 2, Stroke = (Brush)FindResource("TextMuted"), StrokeThickness = 0.5 });

        // Scale: +-3G fills the canvas
        double px = w / 2 + (state.GForceLateral / 3.0) * (w / 2);
        double py = h / 2 - (state.GForceLongitudinal / 3.0) * (h / 2);
        px = Math.Clamp(px, 4, w - 4);
        py = Math.Clamp(py, 4, h - 4);

        var dot = new Ellipse { Width = 10, Height = 10, Fill = (Brush)FindResource("AccentRed") };
        Canvas.SetLeft(dot, px - 5);
        Canvas.SetTop(dot, py - 5);
        GForceCanvas.Children.Add(dot);
    }

    private void DrawTrackMap(GameState state)
    {
        // Collect/update the data once, then render it to every open view (the tab and
        // the pop-out window) using the same routine - so both always agree and there's
        // only one drawing implementation to maintain.
        string? status = UpdateTrackMapData(state);

        RenderTrackMap(TrackMapCanvas, TrackMapStatusText, state, status);
        if (_popoutMap != null)
        {
            RenderTrackMap(_popoutMap.MapCanvas, _popoutMap.StatusText, state, status);
        }
    }

    /// <summary>Records new track points. Returns a status message if the map can't be
    /// drawn yet, or null when there's real data ready to render.</summary>
    private string? UpdateTrackMapData(GameState state)
    {
        if (_broadcasting == null)
            return "Track map is off.\nTurn it on in Settings \u2192 Track Map (one click).";
        if (!_broadcasting.IsConnected)
            return "Turned on, but not connected to ACC yet.\nMake sure ACC is running - it connects automatically.";
        if (state.Position <= 0)
            return "Connected. Waiting for you to get on track...";

        // Skip recording while off track or in the pits - otherwise a track cut or a pit
        // stop draws an ugly stray line across the map instead of just the racing line.
        bool shouldRecord = state.WheelsOffTrack == 0 && !state.IsInPit;

        if (shouldRecord && _broadcasting.TryGetSelfPosition(state.Position, out float x, out float y))
        {
            var p = new Point(x, y);
            var currentSegment = _trackMapSegments[^1];

            if (currentSegment.Count == 0 || (Math.Abs(currentSegment[^1].X - p.X) > 0.5 || Math.Abs(currentSegment[^1].Y - p.Y) > 0.5))
            {
                // A very large jump means a teleport (pit reset, session restart, respawn)
                // rather than actual driving - start a new segment so it doesn't get drawn
                // as a straight line shooting across the whole map.
                if (currentSegment.Count > 0)
                {
                    double jump = Math.Sqrt(Math.Pow(currentSegment[^1].X - p.X, 2) + Math.Pow(currentSegment[^1].Y - p.Y, 2));
                    if (jump > 120)
                    {
                        _trackMapSegments.Add(new List<Point>());
                        currentSegment = _trackMapSegments[^1];
                    }
                }

                currentSegment.Add(p);

                int totalPoints = _trackMapSegments.Sum(s => s.Count);
                if (totalPoints > 3000 && _trackMapSegments[0].Count > 0)
                {
                    _trackMapSegments[0].RemoveAt(0); // cap memory for very long sessions
                    if (_trackMapSegments[0].Count == 0 && _trackMapSegments.Count > 1)
                        _trackMapSegments.RemoveAt(0);
                }
            }
        }
        else if (_trackMapSegments[^1].Count > 0)
        {
            // Recording just paused (went off track / into the pits) - start a fresh
            // segment so the next good point doesn't reconnect with a straight line
            // across whatever was skipped.
            _trackMapSegments.Add(new List<Point>());
        }

        return _trackMapSegments.Sum(s => s.Count) < 2
            ? "Building the map - drive a bit more of the lap..."
            : null;
    }

    private void RenderTrackMap(Canvas canvas, TextBlock statusText, GameState state, string? status)
    {
        canvas.Children.Clear();

        if (status != null)
        {
            statusText.Text = status;
            statusText.Visibility = Visibility.Visible;
            return;
        }
        statusText.Visibility = Visibility.Collapsed;

        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        var allPoints = _trackMapSegments.SelectMany(s => s).ToList();
        if (w <= 0 || h <= 0 || allPoints.Count < 2) return;

        double minX = allPoints.Min(p => p.X), maxX = allPoints.Max(p => p.X);
        double minY = allPoints.Min(p => p.Y), maxY = allPoints.Max(p => p.Y);
        double rangeX = Math.Max(maxX - minX, 1), rangeY = Math.Max(maxY - minY, 1);
        double margin = 16;

        // Preserve aspect ratio so the circuit isn't stretched to fill the panel - uses
        // the tighter of the two axes so the shape stays true regardless of window size.
        double scale = Math.Min((w - margin * 2) / rangeX, (h - margin * 2) / rangeY);
        double offsetX = (w - rangeX * scale) / 2;
        double offsetY = (h - rangeY * scale) / 2;

        Point ToCanvasPoint(Point p) => new(
            offsetX + (p.X - minX) * scale,
            offsetY + (p.Y - minY) * scale);

        foreach (var segment in _trackMapSegments)
        {
            if (segment.Count < 2) continue;
            var segPoints = new PointCollection(segment.Select(ToCanvasPoint));
            canvas.Children.Add(new Polyline
            {
                Points = segPoints,
                Stroke = (Brush)FindResource("AccentBlue"),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round
            });
        }

        // Everyone else on track - small dots at their current position.
        if (_broadcasting != null)
        {
            foreach (var (ox, oy, _) in _broadcasting.GetAllCarPositions(state.Position))
            {
                var op = ToCanvasPoint(new Point(ox, oy));
                var otherDot = new Ellipse
                {
                    Width = 7, Height = 7,
                    Fill = (Brush)FindResource("AccentPurple"),
                    Opacity = 0.85
                };
                Canvas.SetLeft(otherDot, op.X - 3.5);
                Canvas.SetTop(otherDot, op.Y - 3.5);
                canvas.Children.Add(otherDot);
            }
        }

        // Player marker with a soft halo so it stands out against the other cars.
        var last = ToCanvasPoint(allPoints[^1]);
        var halo = new Ellipse
        {
            Width = 18, Height = 18,
            Fill = (Brush)FindResource("AccentGreen"),
            Opacity = 0.25
        };
        Canvas.SetLeft(halo, last.X - 9);
        Canvas.SetTop(halo, last.Y - 9);
        canvas.Children.Add(halo);

        var dot = new Ellipse { Width = 10, Height = 10, Fill = (Brush)FindResource("AccentGreen") };
        Canvas.SetLeft(dot, last.X - 5);
        Canvas.SetTop(dot, last.Y - 5);
        canvas.Children.Add(dot);
    }

    /// <summary>
    /// Push-to-talk: records while the bound wheel button (or key) is held, then sends the
    /// audio to Gemini for transcription and treats it exactly like a typed question.
    /// </summary>
    private async void PollVoiceButton()
    {
        if (!_config.VoiceInputEnabled || string.IsNullOrWhiteSpace(_config.VoiceInputBinding)) return;

        bool held = _voiceInput.IsButtonHeld(_config.VoiceInputBinding);

        if (held && !_voiceButtonWasHeld)
        {
            _voiceInput.StartRecording();
            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Listening...");
        }
        else if (!held && _voiceButtonWasHeld)
        {
            var wavPath = _voiceInput.StopRecording();
            _voiceButtonWasHeld = held;

            if (wavPath == null)
            {
                TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Didn't catch that - hold the button while speaking.");
                return;
            }

            var transcript = await _gemini.TranscribeAsync(wavPath);
            try { System.IO.File.Delete(wavPath); } catch { }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                // Distinguish "didn't understand the audio" from an actual API failure -
                // a quota running out used to look identical to a bad recording, which
                // made it impossible to tell why voice input had stopped working.
                var why = _gemini.LastTranscribeError;
                TranscriptList.Items.Add(why != null
                    ? $"[{DateTime.Now:HH:mm:ss}] [!] Voice input failed: {why}."
                    : $"[{DateTime.Now:HH:mm:ss}] Couldn't make out what you said - try again.");
                return;
            }

            QuestionInput.Text = transcript;
            await AskQuestion();
            return;
        }

        _voiceButtonWasHeld = held;
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e) => await AskQuestion();

    private async void QuestionInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await AskQuestion();
    }

    private async Task AskQuestion()
    {
        var question = QuestionInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(question)) return;
        QuestionInput.Text = "";

        TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] You: {question}");

        if (question.Equals("debug lmu", StringComparison.OrdinalIgnoreCase))
        {
            string dump = _lmuReader.DumpRaw();
            string filePath = System.IO.Path.Combine(PitWatch.UserDataPaths.Root, "debug_lmu.txt");
            try { System.IO.File.WriteAllText(filePath, dump); } catch { }
            foreach (var line in dump.Split('\n'))
                AddTranscriptLine(line);
            AddTranscriptLine($"(also written to {filePath})");
            return;
        }

        if (question.Equals("debug broadcast raw", StringComparison.OrdinalIgnoreCase))
        {
            if (_broadcasting == null)
            {
                TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Broadcasting isn't turned on.");
            }
            else
            {
                _broadcasting.RequestRawDump(3);
                TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Capturing next 3 messages - check debug_broadcast_raw_*.txt in a few seconds.");
            }
            return;
        }

        if (question.Equals("debug broadcast", StringComparison.OrdinalIgnoreCase))
        {
            string dump = _broadcasting == null
                ? "Broadcasting isn't turned on."
                : $"Connected={_broadcasting.IsConnected}. My known race position (from shared memory)={_lastState.Position}. "
                  + _broadcasting.DumpTrackedCars();

            string filePath = System.IO.Path.Combine(AppContext.BaseDirectory, "debug_broadcast.txt");
            System.IO.File.WriteAllText(filePath, dump);

            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Debug info written to debug_broadcast.txt next to the exe - open it in Notepad to copy.");
            return;
        }

        if (question.Equals("talk less", StringComparison.OrdinalIgnoreCase))
        {
            _idleChatter.SetRuntimeVerbosity(2.5f);
            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Got it, talking less from now on.");
            return;
        }

        if (question.Equals("talk more", StringComparison.OrdinalIgnoreCase))
        {
            _idleChatter.SetRuntimeVerbosity(0.3f);
            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Got it, talking more from now on.");
            return;
        }

        string answer = PresetCommands.TryHandle(question, _lastState, _lapAnalyzer, _raceRules)
                         ?? await _gemini.AskAsync(question, _lastState.ToPromptContext());

        _speechOut.Speak(answer); // this also adds the answer to the transcript via OnSpoken
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        new HistoryWindow().Show();
    }

    private void PopoutMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_popoutMap != null)
        {
            _popoutMap.Activate();
            return;
        }

        _popoutMap = new TrackMapWindow();
        _popoutMap.Closed += (_, _) => _popoutMap = null;
        _popoutMap.Show();
    }

    private async Task CheckForUpdatesAsync()
    {
        var result = await _updates.CheckAsync();
        if (!result.UpdateAvailable) return;

        // Download in the background first, so "Restart & update" is instant rather than
        // leaving the user staring at a progress bar when they click it.
        bool ready = await _updates.DownloadAsync();
        if (!ready) return;

        _pendingUpdate = result;

        Dispatcher.Invoke(() =>
        {
            UpdateBannerText.Text = $"PitWatch {result.NewVersion} is ready to install.";
            // Only offer the notes link when there's something to show.
            UpdateInfoButton.Visibility = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateBanner.Visibility = Visibility.Visible;
        });
    }

    private void UpdateInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate?.NewVersion == null) return;

        var window = new UpdateNotesWindow(_pendingUpdate.NewVersion, _pendingUpdate.ReleaseNotes) { Owner = this };
        window.ShowDialog();

        // The notes window has its own update button, so honour that choice here.
        if (window.UpdateRequested)
        {
            _recorder.FinishAndSave();
            _updates.ApplyAndRestart();
        }
    }

    private void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        _recorder.FinishAndSave(); // don't lose the current session to the restart
        _updates.ApplyAndRestart();
    }

    private void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        // Stays downloaded - it'll be applied next time PitWatch restarts anyway.
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the pre-race plan - what to start with and when to stop. Runs in practice
    /// too, which is the point: you work out the race there, before it matters.
    /// </summary>
    private void UpdateRacePlan(GameState state)
    {
        var plan = RacePlanner.Build(state, _raceRules, state.FuelPerLap, _lapAnalyzer.BestLapTime);

        if (plan == null)
        {
            RacePlanSummary.Text = state.FuelPerLap <= 0
                ? "Run a few laps so I can measure your fuel use."
                : "Set the race length above and I'll plan it.";
            RacePlanDetails.Text = "";
            return;
        }

        RacePlanSummary.Text = plan.Summary;
        RacePlanDetails.Text = string.Join("\n", plan.Details);
    }

    private void UpdateDiagnostics(GameState state)
    {
        DiagStateText.Text =
            $"running={state.IsGameRunning}  game={state.GameName}  lap={state.CurrentLap}  sector={state.CurrentSectorIndex + 1}\n"
          + $"pos={state.Position}/{state.TotalCars}  speed={state.SpeedKmh:F0}  gear={state.Gear}  rpm={state.Rpm}\n"
          + $"fuel={state.FuelLiters:F1}L  perLap={state.FuelPerLap:F2}  lapsLeft={state.EstimatedLapsOfFuelLeft}\n"
          + $"inPit={state.IsInPit}  wheelsOff={state.WheelsOffTrack}  lapProgress={state.LapProgress:F3}\n"
          + $"sessionStatus={state.SessionStatusRaw}  type={state.SessionTypeRaw}  flag={state.SessionFlagRaw}  timeLeft={state.SessionTimeLeftSeconds:F0}s";

        string broadcast = _broadcasting == null
            ? "broadcasting: off"
            : $"broadcasting: {(_broadcasting.IsConnected ? "connected" : "not connected")}";

        DiagConnText.Text =
            $"{broadcast}\n"
          + $"gemini key: {(_config.HasGeminiKey ? "set" : "not set")}\n"
          + $"elevenlabs: {(_config.UseElevenLabs ? (_config.HasElevenLabsKey ? "on, key set" : "on, NO KEY") : "off")}\n"
          + $"voice input: {(_config.VoiceInputEnabled ? $"on ({_config.VoiceInputBinding})" : "off")}\n"
          + $"map points: {_trackMapSegments.Sum(seg => seg.Count)}  segments: {_trackMapSegments.Count}";
    }

    private void DiagOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PitWatch.Logger.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't open the log file.", ex);
        }
    }

    private void DiagOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PitWatch.UserDataPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PitWatch.UserDataPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't open the data folder.", ex);
        }
    }

    private void ApplyRules_Click(object sender, RoutedEventArgs e)
    {
        // Parse leniently - a blank or unparseable box just means "not set" rather than
        // an error dialog, since this gets filled in quickly between sessions.
        if (int.TryParse(RuleLengthBox.Text, out int length) && length > 0)
        {
            bool inLaps = RuleLengthUnit.SelectedIndex == 1;
            _raceRules.RaceLengthLaps = inLaps ? length : null;
            _raceRules.RaceLengthMinutes = inLaps ? null : length;
        }
        else
        {
            _raceRules.RaceLengthLaps = null;
            _raceRules.RaceLengthMinutes = null;
        }

        _raceRules.MinimumPitWindowLap = int.TryParse(RuleWindowFrom.Text, out int from) && from > 0 ? from : null;
        _raceRules.MaximumPitWindowLap = int.TryParse(RuleWindowTo.Text, out int to) && to > 0 ? to : null;
        _raceRules.PitStopTimeLossSeconds = float.TryParse(RuleStopSeconds.Text, out float secs) && secs > 0 ? secs : null;
        _raceRules.TankCapacityLiters = float.TryParse(RuleTankCapacity.Text, out float tank) && tank > 0 ? tank : null;

        _raceRules.MandatoryPitStop = RuleMandatoryStop.IsChecked == true;
        _raceRules.MandatoryTyreChange = RuleMandatoryTyres.IsChecked == true;
        _raceRules.MandatoryRefuelling = RuleMandatoryRefuel.IsChecked == true;
        _raceRules.MandatoryDriverChange = RuleMandatoryDriver.IsChecked == true;

        StrategyRulesText.Text = _raceRules.Describe();
        AddTranscriptLine(_raceRules.Describe());
    }

    private void ClearRules_Click(object sender, RoutedEventArgs e)
    {
        _raceRules.Clear();
        RuleLengthBox.Text = "";
        RuleWindowFrom.Text = "";
        RuleWindowTo.Text = "";
        RuleStopSeconds.Text = "";
        RuleTankCapacity.Text = "";
        RuleMandatoryStop.IsChecked = false;
        RuleMandatoryTyres.IsChecked = false;
        RuleMandatoryRefuel.IsChecked = false;
        RuleMandatoryDriver.IsChecked = false;
        StrategyRulesText.Text = _raceRules.Describe();
    }

    /// <summary>Adds a line to the radio transcript from any thread.</summary>
    private void AddTranscriptLine(string text)
    {
        Dispatcher.Invoke(() =>
        {
            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (TranscriptList.Items.Count > 200) TranscriptList.Items.RemoveAt(0);
            if (TranscriptList.Items.Count > 0) TranscriptList.ScrollIntoView(TranscriptList.Items[^1]);
        });
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        if (settings.ShowDialog() == true)
        {
            // Reload so a changed API key/personality/voice actually takes effect
            // immediately, instead of silently keeping old settings until restart.
            var updated = PitWatch.Config.Load();
            _gemini = new GeminiClient(updated.GeminiApiKey, updated.GeminiModel, updated.Personality);

            // Start Broadcasting live if it was just turned on - previously this needed
            // a full app restart to take effect, which is almost certainly why the track
            // map didn't show up even after driving multiple laps.
            if (updated.BroadcastingEnabled && _broadcasting == null)
            {
                _broadcasting = new BroadcastingClient(updated.BroadcastingIp, updated.BroadcastingPort,
                    "PitWatch", updated.BroadcastingPassword, updated.BroadcastingCommandPassword);
                _broadcasting.Start();
                TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Car proximity & track map turned on.");
            }

            TranscriptList.Items.Add($"[{DateTime.Now:HH:mm:ss}] Settings updated.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _popoutMap?.Close();
        _voiceInput.Dispose();
        _recorder.FinishAndSave();
        _speechOut.Dispose();
        _broadcasting?.Dispose();
        base.OnClosed(e);
    }
}
