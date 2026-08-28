using System.Linq;
using System.IO;
using System.Text.Json;
using PitWatch.Models;

namespace PitWatch.History;

public class SessionSnapshot
{
    public double TimestampSeconds { get; set; }
    public float SpeedKmh { get; set; }
    public float FuelLiters { get; set; }
    public int Lap { get; set; }
    public int Position { get; set; }
    public float LastLapTimeSeconds { get; set; }
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public bool HasWorldPos { get; set; }
}

/// <summary>Headline facts about a session, for lists and comparisons.</summary>
public class SessionSummary
{
    public string Path { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string GameName { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string CarModel { get; set; } = "";
    public string SessionKindName { get; set; } = "";
    public float BestLap { get; set; }
    public float AverageLap { get; set; }
    public int LapCount { get; set; }
    public float Consistency { get; set; }
    public float TheoreticalBest { get; set; }
    public int FinalPosition { get; set; }
}

public class SessionRecord
{
    public DateTime StartedAt { get; set; }
    public string GameName { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string CarModel { get; set; } = "";
    public string SessionKindName { get; set; } = "";
    public List<SessionSnapshot> Snapshots { get; set; } = new();
    public float BestLapTimeSeconds { get; set; }
    public int FinalPosition { get; set; }

    // v1.1 additions - richer detail so History is actually useful rather than two graphs
    public List<float> LapTimes { get; set; } = new();
    public float TheoreticalBestSeconds { get; set; }
    public float[] BestSectors { get; set; } = new float[3];
    public int StartPosition { get; set; }
    public string RaceDirectorSummary { get; set; } = "";

    public float? AverageLap => LapTimes.Count > 0 ? LapTimes.Average() : null;
    public float? Consistency
    {
        get
        {
            if (LapTimes.Count < 2) return null;
            float avg = LapTimes.Average();
            return MathF.Sqrt(LapTimes.Sum(t => (t - avg) * (t - avg)) / LapTimes.Count);
        }
    }
}

/// <summary>
/// Records a lightweight snapshot roughly once a second during a live session (not every
/// tick - that would make files huge and graphs noisy) and saves it as JSON when the
/// session ends, so it can be reloaded and viewed later in the History window.
/// </summary>
public class SessionRecorder
{
    private const double SnapshotIntervalSeconds = 1.0;
    private static readonly string SessionsFolder = UserDataPaths.SessionsFolder;

    private SessionRecord? _current;
    private DateTime _sessionStartUtc;
    private double _lastSnapshotAt = -999;

    public void Update(GameState state, Func<int, (float X, float Y)?>? worldPositionLookup = null)
    {
        if (!state.IsGameRunning)
        {
            if (_current != null) FinishAndSave();
            return;
        }

        if (_current == null)
        {
            _current = new SessionRecord
            {
                StartedAt = DateTime.Now,
                GameName = state.GameName,
                TrackName = state.TrackName,
                CarModel = state.CarModel,
                SessionKindName = state.KindName,
            };
            _sessionStartUtc = DateTime.UtcNow;
            _lastSnapshotAt = -999;
        }

        double elapsed = (DateTime.UtcNow - _sessionStartUtc).TotalSeconds;
        if (elapsed - _lastSnapshotAt < SnapshotIntervalSeconds) return;
        _lastSnapshotAt = elapsed;

        var worldPos = worldPositionLookup?.Invoke(state.Position);

        _current.Snapshots.Add(new SessionSnapshot
        {
            TimestampSeconds = elapsed,
            SpeedKmh = state.SpeedKmh,
            FuelLiters = state.FuelLiters,
            Lap = state.CurrentLap,
            Position = state.Position,
            LastLapTimeSeconds = state.LastLapTimeSeconds,
            HasWorldPos = worldPos.HasValue,
            WorldX = worldPos?.X ?? 0,
            WorldY = worldPos?.Y ?? 0,
        });

        if (state.BestLapTimeSeconds > 0 && state.BestLapTimeSeconds < 3600)
            _current.BestLapTimeSeconds = state.BestLapTimeSeconds;
        _current.FinalPosition = state.Position;
    }

    /// <summary>Call on app close too, in case a session was still live.</summary>
    /// <summary>Attach analysis results before saving, so History has the full picture
    /// rather than just raw snapshots.</summary>
    public void AttachAnalysis(IEnumerable<float> lapTimes, float theoreticalBest, float[] bestSectors,
        int startPosition, string directorSummary)
    {
        if (_current == null) return;
        _current.LapTimes = lapTimes.ToList();
        _current.TheoreticalBestSeconds = theoreticalBest;
        _current.BestSectors = bestSectors;
        _current.StartPosition = startPosition;
        _current.RaceDirectorSummary = directorSummary;
    }

    public void FinishAndSave()
    {
        if (_current == null || _current.Snapshots.Count < 3) { _current = null; return; }

        Directory.CreateDirectory(SessionsFolder);
        // Game goes in the filename so the history list can be grouped and filtered without
        // opening and parsing every saved session just to find out which sim it was.
        string safeGame = string.Concat((_current.GameName ?? "Unknown").Where(char.IsLetterOrDigit));
        if (safeGame.Length == 0) safeGame = "Unknown";
        string fileName = $"session_{safeGame}_{_current.StartedAt:yyyy-MM-dd_HH-mm-ss}.json";
        string path = Path.Combine(SessionsFolder, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));

        _current = null;
    }

    public static List<string> ListSavedSessions()
    {
        if (!Directory.Exists(SessionsFolder)) return new List<string>();
        return Directory.GetFiles(SessionsFolder, "session_*.json").OrderByDescending(f => f).ToList();
    }

    /// <summary>
    /// Loads just enough of every session to build a list - track, date, best lap - so
    /// the history page can group and compare without parsing every snapshot of every
    /// session, which gets slow once there are dozens of them.
    /// </summary>
    public static List<SessionSummary> LoadAllSummaries()
    {
        var results = new List<SessionSummary>();

        foreach (var path in ListSavedSessions())
        {
            try
            {
                var rec = Load(path);
                if (rec == null) continue;

                var valid = rec.LapTimes.Where(t => t > 1 && t < 3600).ToList();

                results.Add(new SessionSummary
                {
                    Path = path,
                    StartedAt = rec.StartedAt,
                    GameName = rec.GameName,
                    TrackName = string.IsNullOrWhiteSpace(rec.TrackName) ? "Unknown track" : rec.TrackName,
                    CarModel = rec.CarModel,
                    SessionKindName = rec.SessionKindName,
                    BestLap = valid.Count > 0 ? valid.Min() : 0,
                    AverageLap = valid.Count > 0 ? valid.Average() : 0,
                    LapCount = valid.Count,
                    Consistency = StdDev(valid),
                    TheoreticalBest = rec.TheoreticalBestSeconds,
                    FinalPosition = rec.FinalPosition,
                });
            }
            catch (Exception ex)
            {
                PitWatch.Logger.Warn($"Couldn't summarise {path}: {ex.Message}");
            }
        }

        return results.OrderByDescending(r => r.StartedAt).ToList();
    }

    private static float StdDev(List<float> values)
    {
        if (values.Count < 2) return 0;
        float mean = values.Average();
        return MathF.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    public static SessionRecord? Load(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path));
    }
}
