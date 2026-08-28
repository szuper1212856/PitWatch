using System.Linq;
using PitWatch.Telemetry;
using PitWatch.Voice;

namespace PitWatch.Commands;

/// <summary>
/// Announces "car on your left/right" style callouts from Broadcasting SDK proximity
/// data - only on a NEW car entering close range, not continuously while one sits there
/// (which would be constant noise during a tight battle).
/// </summary>
public class ProximityWatcher
{
    // Tightened from 8m to 5m - was firing for cars that were nearby but not actually
    // close enough to matter, which felt like constant noise.
    private const float ProximityRadiusMeters = 5f;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(4);
    private DateTime _lastAnnouncement = DateTime.MinValue;
    private bool _wasClearLastCheck = true;

    public void Update(BroadcastingClient broadcasting, int myRacePosition, SpeechOutput speech)
    {
        if (!broadcasting.IsConnected) return;

        var nearby = broadcasting.FindNearbyCars(myRacePosition, ProximityRadiusMeters);

        if (nearby.Count == 0)
        {
            _wasClearLastCheck = true;
            return;
        }

        // Only announce when transitioning from clear to occupied, not every tick
        // while a car is still sitting there.
        if (!_wasClearLastCheck) return;
        if (DateTime.UtcNow - _lastAnnouncement < Cooldown) return;

        var closest = nearby.OrderBy(c => c.DistanceMeters).First();
        speech.Speak(closest.Side switch
        {
            "left" => "Car on your left.",
            "right" => "Car on your right.",
            "ahead" => "Car close ahead.",
            "behind" => "Car right behind you.",
            _ => "Car close by."
        });

        _wasClearLastCheck = false;
        _lastAnnouncement = DateTime.UtcNow;
    }
}
