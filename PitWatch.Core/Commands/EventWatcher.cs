using PitWatch.Models;
using PitWatch.Voice;

namespace PitWatch.Commands;

/// <summary>
/// Compares each new GameState against the previous one and speaks up when something
/// event-worthy happens - overtakes, crashes, low fuel, new damage, race finish, and now
/// (v1.0) an automatic end-of-lap delta report.
///
/// HONEST LIMITATIONS (things you asked for that aren't in here yet):
/// - Penalties: ACC's basic shared memory doesn't clearly expose penalty state in the
///   fields this app currently reads.
/// - Rain incoming: not available via this shared memory page at all.
/// </summary>
public class EventWatcher
{
    private GameState? _previous;
    private bool _announcedFinish;
    private bool _hasAnnouncedStart;
    private readonly Random _rng = new();

    // Tune this if it fires too often/rarely on your rig - impact magnitude varies
    // by car and crash type, this is a starting estimate, not a calibrated value.
    private const float CrashGThreshold = 5.0f;
    private const int OvertakenRoastThreshold = 1;
    private static readonly TimeSpan OvertakenWindow = TimeSpan.FromSeconds(90);
    private readonly List<DateTime> _recentOvertakenTimes = new();
    private DateTime _lastTyreWarning = DateTime.MinValue;
    private static readonly TimeSpan TyreWarningCooldown = TimeSpan.FromSeconds(90);

    public void Update(GameState state, SpeechOutput speech, PitWatch.Config config, LapAnalyzer lapAnalyzer,
        StintTracker stints, CustomCallouts callouts)
    {
        if (!state.IsGameRunning)
        {
            _previous = null;
            _announcedFinish = false;
            _hasAnnouncedStart = false;
            _recentOvertakenTimes.Clear();
            return;
        }

        bool quiet = config.Chattiness == "Quiet";

        if (_previous is { } prev)
        {
            // Position change - discretionary (skipped in Quiet mode), and ignored while
            // in the pit lane since strategy shuffles aren't real on-track passes.
            //
            // This used to also suppress anything under 70 km/h as a pit-lane proxy, but
            // that backfired: you drop below 70 in slow corners and hairpins, so genuine
            // overtakes there were silently swallowed and only reported at the next
            // position change - which is what made callouts feel delayed. IsInPit is the
            // real signal, so the speed floor is now low enough to only catch a stationary
            // car (spin, reset, garage) rather than normal slow-corner driving.
            bool nearPits = state.IsInPit || prev.IsInPit || state.SpeedKmh < 15 || prev.SpeedKmh < 15;

            // Position changes only mean something in a race. In practice and qualifying
            // everyone is on their own programme - running different fuel loads, on out
            // laps, in the pits - so "position" churns constantly and reacting to it is
            // just noise.
            if (!quiet && config.AnnounceOvertakes && state.IsRace && !nearPits
                && state.Position > 0 && prev.Position > 0 && state.Position != prev.Position)
            {
                if (state.Position < prev.Position)
                {
                    speech.Speak(callouts.Get("overtake") ?? Pick(PersonalityProfile.OvertakeLines(config.Personality)));
                    stints.RecordOvertake(madeByPlayer: true);
                }
                else
                {
                    var now = DateTime.UtcNow;
                    _recentOvertakenTimes.Add(now);
                    _recentOvertakenTimes.RemoveAll(t => now - t > OvertakenWindow);

                    if (_recentOvertakenTimes.Count >= OvertakenRoastThreshold)
                    {
                        speech.Speak(callouts.Get("overtaken") ?? Pick(PersonalityProfile.OvertakenLines(config.Personality)));
                        stints.RecordOvertake(madeByPlayer: false);
                        _recentOvertakenTimes.Clear();
                    }
                }
            }

            // Crash reaction - always on regardless of chattiness, this is safety-relevant.
            bool hadImpactSpike = state.ImpactG >= CrashGThreshold && prev.ImpactG < CrashGThreshold;
            bool pickedUpDamage = state.HasDamage && !prev.HasDamage;
            if (pickedUpDamage)
            {
                string damageLine = callouts.Get("damage") ?? DamageFormatter.FormatAutomaticCallout(state.CarDamageRaw);
                speech.Speak(hadImpactSpike ? $"That was a hit! {damageLine}" : damageLine);
            }

            // Low fuel - always on, this is strategy-critical.
            if (state.EstimatedLapsOfFuelLeft is >= 0 and <= 1
                && (prev.EstimatedLapsOfFuelLeft > 1 || prev.EstimatedLapsOfFuelLeft < 0))
            {
                speech.Speak(callouts.Get("low_fuel") ?? "Fuel's critical - box this lap.");
            }

            // Green light - always on, once per session.
            // "GREEN LIGHT, GO GO GO" belongs to a race start, not rolling out of the
            // garage for a practice run.
            if (!_hasAnnouncedStart && state.IsRace && state.CurrentLap <= 1
                && prev.SpeedKmh < 5 && state.SpeedKmh > 30)
            {
                speech.Speak(callouts.Get("green_light") ?? "GREEN LIGHT, GREEN LIGHT, GO GO GO!");
                _hasAnnouncedStart = true;
            }

            // End-of-lap delta report (v1.0) - discretionary, skipped in Quiet mode.
            if (!quiet && config.AnnounceLapAnalysis && state.CurrentLap != prev.CurrentLap)
            {
                var report = lapAnalyzer.TakeLastLapReport();
                if (report != null) speech.Speak(report);
            }

            // Personal best alerts (new best lap / best sector) - these come from the
            // analyzer as they happen rather than waiting for the end of the lap.
            if (!quiet && config.AnnounceLapAnalysis)
            {
                var alert = lapAnalyzer.TakeNextAlert();
                if (alert != null)
                {
                    string key = alert.Contains("sector", StringComparison.OrdinalIgnoreCase) ? "best_sector" : "best_lap";
                    speech.Speak(callouts.Get(key) ?? alert);
                }
            }

            // Stint summary after each pit stop
            if (config.AnnounceStintSummary)
            {
                var stintSummary = stints.TakeStintSummary();
                if (stintSummary != null) speech.Speak(stintSummary);
            }

            // Tyre temperature warnings - only speaks when something's actually wrong,
            // and rate-limited so it can't nag every few seconds during a hot stint.
            if (!quiet && config.AnnounceTyreTemps && DateTime.UtcNow - _lastTyreWarning > TyreWarningCooldown)
            {
                var tyreWarning = TyreAdvisor.CheckForWarning(state);
                if (tyreWarning != null)
                {
                    speech.Speak(tyreWarning);
                    _lastTyreWarning = DateTime.UtcNow;
                }
            }

            // Race finish - always on.
            // Only announce a "race result" for an actual race - finishing a practice
            // session with "P12, do better next time" makes no sense.
            bool sessionEnded = state.IsRace
                                && prev.SessionStatusRaw == 2 && state.SessionStatusRaw != 2
                                && prev.CurrentLap >= 1;
            bool lapsComplete = state.TotalLaps > 0 && state.CurrentLap > state.TotalLaps;
            if (!_announcedFinish && (sessionEnded || lapsComplete))
            {
                _announcedFinish = true;

                string headline = state.Position switch
                {
                    1 => callouts.Get("race_win") ?? "You won! Great race.",
                    2 or 3 => callouts.Get("race_podium") ?? $"P{state.Position} finish, nice podium.",
                    <= 0 => callouts.Get("race_finish") ?? "Race finished.",
                    _ => callouts.Get("race_finish") ?? $"Race finished, P{state.Position}."
                };
                speech.Speak(headline);

                // Full race director debrief - the detailed post-race breakdown.
                speech.Speak(stints.BuildRaceDirectorSummary(state, lapAnalyzer));
            }
        }

        _previous = state;
    }

    private string Pick(string[] options) => options[_rng.Next(options.Length)];
}
