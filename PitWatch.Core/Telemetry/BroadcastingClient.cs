using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PitWatch.Telemetry;

/// <summary>
/// Client for ACC's official "Broadcasting SDK" - a separate UDP protocol (not shared
/// memory) that Kunos publishes specifically for spotter apps, TV overlays, and similar
/// tools. This is the only way to get other cars' positions - the shared memory used
/// elsewhere in this app genuinely does not include opponent data at all.
///
/// SETUP REQUIRED (one-time): open
///   Documents\Assetto Corsa Competizione\Config\broadcasting.json
/// and make sure "updListenerPort" matches BroadcastingPort in PitWatch's config.json
/// (default 9000), and note whatever "connectionPassword" is set there - it needs to
/// match BroadcastingPassword in config.json too (both can be empty strings).
///
/// CONFIDENCE NOTE: this is based on Kunos' officially published protocol spec/sample
/// code, so it's on firmer ground than the shared-memory struct guessing earlier in this
/// app - but it's still written from memory without a live ACC instance to test against.
/// Parse failures get logged with the message type and byte count so we can debug
/// together if it doesn't line up on the first try, same approach that fixed the
/// tyre/damage/flag issues.
/// </summary>
public class BroadcastingClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _serverEndpoint;
    private readonly string _displayName;
    private readonly string _connectionPassword;
    private readonly string _commandPassword;
    private int _connectionId = -1;

    private class CarInfo
    {
        public float X, Y, YawRad;
        public int RacePosition;
        public DateTime LastUpdatedUtc = DateTime.UtcNow;
    }

    private readonly Dictionary<int, CarInfo> _cars = new();
    private readonly object _lock = new();

    public bool IsConnected { get; private set; }
    private DateTime _lastPacketUtc = DateTime.UtcNow;
    private volatile bool _disposed;

    public BroadcastingClient(string ip, int port, string displayName, string connectionPassword, string commandPassword)
    {
        _serverEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        _udp = new UdpClient(0); // bind to any local port
        _displayName = displayName;
        _connectionPassword = connectionPassword;
        _commandPassword = commandPassword;
    }

    public void Start()
    {
        SendRegisterRequest();
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(RetryLoop);
    }

    /// <summary>
    /// Keeps the connection alive across ACC restarts and session changes.
    ///
    /// Two bugs this fixes: registration used to be attempted only once at Start(), so if
    /// ACC wasn't up yet it never connected; and the loop used to exit permanently once
    /// connected, so when ACC restarted or you loaded a new session, IsConnected stayed
    /// true forever while no data actually arrived - the track map would just freeze on
    /// whatever it had last drawn. Now it runs for the app's lifetime and treats "no
    /// packets for a while" as disconnected, which triggers re-registration.
    /// </summary>
    private async Task RetryLoop()
    {
        while (!_disposed)
        {
            await Task.Delay(4000);

            // Consider the connection dead if nothing has arrived recently - ACC stops
            // sending when a session ends or the game closes, but never tells us.
            if (IsConnected && DateTime.UtcNow - _lastPacketUtc > TimeSpan.FromSeconds(8))
            {
                PitWatch.Logger.Info("Broadcasting: no data for a while, assuming the session ended - re-registering.");
                IsConnected = false;
                lock (_lock) { _cars.Clear(); } // drop stale car positions from the old session
            }

            if (!IsConnected)
            {
                SendRegisterRequest();
            }
        }
    }

    private void SendRegisterRequest()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)1); // REGISTER_COMMAND_APPLICATION
        w.Write((byte)4); // broadcasting protocol version
        WriteString(w, _displayName);
        WriteString(w, _connectionPassword);
        w.Write(250); // requested update interval in ms
        WriteString(w, _commandPassword);
        Send(ms.ToArray());
    }

    private void Send(byte[] data) => _udp.Send(data, data.Length, _serverEndpoint);

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private async Task ReceiveLoop()
    {
        while (!_disposed)
        {
            try
            {
                var result = await _udp.ReceiveAsync();
                _lastPacketUtc = DateTime.UtcNow;
                ParseMessage(result.Buffer);
            }
            catch (ObjectDisposedException)
            {
                // The socket was closed while we were waiting on it - that's a normal
                // shutdown, not an error. Exit rather than spinning: previously this
                // caught the generic Exception below and looped instantly, writing
                // thousands of identical lines to the log in a fraction of a second.
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // On Windows a UDP send to a port with nothing listening comes back as
                // ConnectionReset on the *next* receive. It just means ACC isn't running
                // or isn't listening yet, which is entirely normal while we wait for it -
                // so this is expected, not an error, and must not be logged every retry.
                LogThrottled("Broadcasting: nothing listening on the ACC port yet (this is normal until ACC is running).");
            }
            catch (Exception ex)
            {
                LogThrottled($"Broadcasting receive error: {ex.Message}");

                // Back off briefly so an unexpected, immediately-repeating failure can't
                // spin the CPU or flood the log.
                await Task.Delay(1000);
            }
        }
    }

    private string? _lastThrottledMessage;
    private DateTime _lastThrottledLogUtc = DateTime.MinValue;
    private int _suppressedLogCount;

    /// <summary>
    /// Logs a repeating message at most once a minute, reporting how many times it was
    /// suppressed. Without this, a condition that repeats every few seconds (like ACC not
    /// being open) fills the log with thousands of identical lines and buries anything
    /// actually worth reading.
    /// </summary>
    private void LogThrottled(string message)
    {
        var now = DateTime.UtcNow;

        if (message == _lastThrottledMessage && now - _lastThrottledLogUtc < TimeSpan.FromMinutes(1))
        {
            _suppressedLogCount++;
            return;
        }

        if (_suppressedLogCount > 0 && message == _lastThrottledMessage)
        {
            PitWatch.Logger.Info($"(previous message repeated {_suppressedLogCount} more times)");
        }

        PitWatch.Logger.Warn(message);
        _lastThrottledMessage = message;
        _lastThrottledLogUtc = now;
        _suppressedLogCount = 0;
    }

    private void ParseMessage(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        byte messageType = r.ReadByte();

        try
        {
            switch (messageType)
            {
                case 1: // REGISTRATION_RESULT
                    _connectionId = r.ReadInt32();
                    bool success = r.ReadByte() != 0;
                    r.ReadByte(); // isReadOnly
                    string errMsg = ReadString(r);
                    IsConnected = success;
                    PitWatch.Logger.Info(success
                        ? "[Broadcasting] connected to ACC."
                        : $"[Broadcasting] registration failed: {errMsg}. Check port/password in config.json against broadcasting.json.");
                    break;

                case 3: // REALTIME_CAR_UPDATE - the one we actually need
                    ParseRealtimeCarUpdate(r);
                    break;

                default:
                    // Not parsed in this MVP (entry list, track data, events) - not needed
                    // for pure proximity detection.
                    break;
            }
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Warn($"Broadcasting: couldn't parse message type {messageType} ({data.Length} bytes): {ex.Message}");
        }
    }

    public int _rawDumpsRemaining = 0;

    /// <summary>Call this to capture the next few raw messages, ideally while actually
    /// driving so the data is real telemetry rather than empty pre-spawn zeros.</summary>
    public void RequestRawDump(int count = 3) => _rawDumpsRemaining = count;

    private void ParseRealtimeCarUpdate(BinaryReader r)
    {
        if (_rawDumpsRemaining > 0)
        {
            // Diagnostic: dump the raw bytes of this message so we can count the exact
            // byte layout by hand - the position values showing as exact multiples of 256
            // point to an off-by-one-byte error somewhere in the assumed field layout, and
            // guessing a 4th time isn't reliable - real bytes will settle it precisely.
            var baseStream = (MemoryStream)r.BaseStream;
            var allBytes = baseStream.ToArray();
            string hex = string.Join(" ", allBytes.Select(b => b.ToString("X2")));
            string filePath = System.IO.Path.Combine(AppContext.BaseDirectory, $"debug_broadcast_raw_{_rawDumpsRemaining}.txt");
            System.IO.File.WriteAllText(filePath, $"Message type 3 (REALTIME_CAR_UPDATE), {allBytes.Length} bytes total:\n{hex}");
            _rawDumpsRemaining--;
        }

        int carId = r.ReadUInt16();
        r.ReadUInt16();      // driverIndex - not used
        r.ReadByte();        // unknown field (present in real data, purpose unclear, just skip)
        r.ReadSByte();       // gear - not used
        float worldPosX = r.ReadSingle();
        float worldPosY = r.ReadSingle();
        float yaw = r.ReadSingle();
        r.ReadByte();        // carLocation enum - not used
        r.ReadUInt16();      // speed kmh - not used (we already have our own from shared memory)
        byte racePosition = r.ReadByte(); // confirmed via raw data: 1 byte, not 2 like originally assumed
        // Message continues with spline position, lap timing structs etc. - not read,
        // remaining bytes are simply discarded when this reader is disposed.

        // Reject implausible coordinates before storing. Cars sitting in the garage (or
        // mid-respawn) report 0,0, and occasional corrupt packets report astronomically
        // large floats - either one recorded as a real track point produces the huge
        // stray line running across the map.
        bool validPosition =
            float.IsFinite(worldPosX) && float.IsFinite(worldPosY)
            && Math.Abs(worldPosX) < 100000f && Math.Abs(worldPosY) < 100000f
            && !(Math.Abs(worldPosX) < 0.01f && Math.Abs(worldPosY) < 0.01f);

        if (!validPosition) return;

        lock (_lock)
        {
            _cars[carId] = new CarInfo { X = worldPosX, Y = worldPosY, YawRad = yaw, RacePosition = racePosition };
        }
    }

    private static string ReadString(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        var bytes = r.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Finds nearby cars relative to the player, identifying "which car is me" by matching
    /// the known race position from shared memory (reliable, already-confirmed data)
    /// against the race position each car reports over broadcasting - avoids needing to
    /// guess at a separate "focused car" field.
    /// </summary>
    /// <summary>
    /// Gets the player's own world position, identified the same way as FindNearbyCars -
    /// by matching race position against the known value from shared memory.
    /// </summary>
    public bool TryGetSelfPosition(int myRacePosition, out float x, out float y)
    {
        lock (_lock)
        {
            var self = _cars.Values.FirstOrDefault(c => c.RacePosition == myRacePosition);
            if (self != null)
            {
                x = self.X;
                y = self.Y;
                return true;
            }
        }
        x = 0; y = 0;
        return false;
    }

    /// <summary>All currently tracked cars' positions (excluding whichever one is the
    /// player, identified the same way as TryGetSelfPosition) - used to draw everyone
    /// on the track map, not just the player's own path.</summary>
    public List<(float X, float Y, int RacePosition)> GetAllCarPositions(int myRacePosition)
    {
        lock (_lock)
        {
            return _cars.Values
                .Where(c => c.RacePosition != myRacePosition)
                .Select(c => (c.X, c.Y, c.RacePosition))
                .ToList();
        }
    }

    /// <summary>
    /// Dumps every car currently tracked from broadcast data (id, position, world coords,
    /// yaw) so we can see whether the parsed RacePosition values look sane and compare them
    /// against the known real position from shared memory - diagnostic for the self-car
    /// matching not finding anything despite being connected and receiving data.
    /// </summary>
    public string DumpTrackedCars()
    {
        lock (_lock)
        {
            if (_cars.Count == 0) return "No cars tracked yet from broadcast data.";
            return string.Join(" | ", _cars.Select(kv =>
                $"carId={kv.Key} pos={kv.Value.RacePosition} x={kv.Value.X:F1} y={kv.Value.Y:F1} yaw={kv.Value.YawRad:F2}"));
        }
    }

    public List<(string Side, float DistanceMeters)> FindNearbyCars(int myRacePosition, float radiusMeters)
    {
        var results = new List<(string, float)>();

        lock (_lock)
        {
            CarInfo? self = _cars.Values.FirstOrDefault(c => c.RacePosition == myRacePosition);
            if (self == null) return results;

            foreach (var car in _cars.Values)
            {
                if (car == self) continue;

                float dx = car.X - self.X;
                float dy = car.Y - self.Y;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > radiusMeters) continue;

                float angleToCar = MathF.Atan2(dy, dx);
                float relativeBearing = NormalizeAngle(angleToCar - self.YawRad);

                // Four-way classification instead of just left/right/close. Also note:
                // left/right are SWAPPED here relative to the raw math - confirmed backward
                // via real testing (car reported on the right when actually on the left),
                // so rather than re-derive the exact coordinate handedness that caused it,
                // this directly flips the two labels to match reality.
                const float aheadBehindBand = 0.6f; // ~35 degrees either side of straight ahead/behind
                string side = relativeBearing switch
                {
                    > -aheadBehindBand and < aheadBehindBand => "ahead",
                    > MathF.PI - aheadBehindBand or < -(MathF.PI - aheadBehindBand) => "behind",
                    > 0 => "left",   // swapped from "right" - see note above
                    _ => "right",    // swapped from "left"
                };

                results.Add((side, distance));
            }
        }

        return results;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= 2 * MathF.PI;
        while (angle < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }

    public void Dispose()
    {
        _disposed = true;
        _udp.Dispose();
    }
}
