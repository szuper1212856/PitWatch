using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using PitWatch.Models;

namespace PitWatch.Telemetry;

/// <summary>
/// Reads ACC's official shared memory (acpmf_physics / acpmf_graphics / acpmf_static).
/// These structs match Kunos' published ACC Shared Memory SDK layout.
///
/// NOTE ON ACCURACY: ACC's basic shared memory does NOT expose gaps to other cars
/// or your grid position among opponents reliably — that data lives in the separate
/// "Broadcasting SDK" (a UDP protocol), which is a bigger integration than this MVP covers.
/// Gap/position fields below are left at 0 / best-effort until that's added.
/// If any field reads garbage on your machine, ACC has changed its SDK slightly between
/// versions — cross-check against the official "ACC Shared Memory" PDF from Kunos/community
/// docs and adjust the struct below; the layout is stable but has grown over updates.
/// </summary>
public class AccTelemetryReader : ITelemetryProvider
{
    public string Name => "ACC";

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct SPageFilePhysics
    {
        public int packetId;
        public float gas;
        public float brake;
        public float fuel;
        public int gear;
        public int rpms;
        public float steerAngle;
        public float speedKmh;
        public fixed float velocity[3];
        public fixed float accG[3];
        public fixed float wheelSlip[4];
        public fixed float wheelLoad[4];
        public fixed float wheelsPressure[4];
        public fixed float wheelAngularSpeed[4];
        public fixed float tyreWear[4];
        public fixed float tyreDirtyLevel[4];
        public fixed float tyreCoreTemperature[4];
        public fixed float camberRAD[4];
        public fixed float suspensionTravel[4];
        public float drs;
        public float tc;
        public float heading;
        public float pitch;
        public float roll;
        public float cgHeight;
        public fixed float carDamage[5];
        public int numberOfTyresOut;
        public int pitLimiterOn;
        public float abs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct SPageFileGraphics
    {
        public int packetId;
        public int status;
        public int session;
        public fixed char currentTime[15];
        public fixed char lastTime[15];
        public fixed char bestTime[15];
        public fixed char split[15];
        public int completedLaps;
        public int position;
        public int iCurrentTime;
        public int iLastTime;
        public int iBestTime;
        public float sessionTimeLeft;
        public float distanceTraveled;
        public int isInPit;
        public int currentSectorIndex;
        public int lastSectorTime;
        public int numberOfLaps;
        public fixed char tyreCompound[33];
        // Missing this field shifted everything below it by 4 bytes: activeCars was
        // reading normalizedCarPosition, so "of 24 cars" showed up as numbers like
        // 1044357427 (that value read as a float is ~0.18 - a lap position, not a car
        // count). Lap progress was reading this replay field for the same reason.
        public float replayTimeMultiplier;
        public float normalizedCarPosition;
        public int activeCars;
        // These two large arrays exist in the real struct purely as padding to reach
        // the fields we actually want below (flag, penaltyTime) - we don't use the
        // per-car data itself, just need the correct byte offset past it.
        public fixed float carCoordinates[180]; // 60 cars * (x,y,z)
        public fixed int carIDs[60];
        public int playerCarID;
        public float penaltyTime;
        public int flag;        // best-effort enum, see FlagNames in GameState - needs your verification
        public int penalty;
        public int idealLineOn;
        public int isInPitLane;
    }

    /// <summary>
    /// ACC's static page - written once when a session loads. Holds the track name, car
    /// model and real tank capacity, none of which appear in the physics or graphics pages.
    /// Uses wide (UTF-16) strings, hence char rather than byte arrays.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private unsafe struct SPageFileStatic
    {
        public fixed char smVersion[15];
        public fixed char acVersion[15];
        public int numberOfSessions;
        public int numCars;
        public fixed char carModel[33];
        public fixed char track[33];
        public fixed char playerName[33];
        public fixed char playerSurname[33];
        public fixed char playerNick[33];
        public int sectorCount;
        public float maxTorque;
        public float maxPower;
        public int maxRpm;
        public float maxFuel;
    }

    private MemoryMappedFile? _physicsFile;
    private MemoryMappedFile? _staticFile;

    // The static page only changes when a session loads, so it's read once and cached
    // rather than re-read 20 times a second along with everything else.
    private string _cachedTrack = "";
    private string _cachedCar = "";
    private float _cachedMaxFuel;
    private DateTime _staticReadAtUtc = DateTime.MinValue;
    private MemoryMappedFile? _graphicsFile;

    public bool IsAvailable()
    {
        try
        {
            using var test = MemoryMappedFile.OpenExisting("Local\\acpmf_physics");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public unsafe GameState ReadState()
    {
        var state = new GameState { GameName = "ACC" };

        try
        {
            _physicsFile ??= MemoryMappedFile.OpenExisting("Local\\acpmf_physics");
            _graphicsFile ??= MemoryMappedFile.OpenExisting("Local\\acpmf_graphics");

            using var physicsAccessor = _physicsFile.CreateViewAccessor();
            using var graphicsAccessor = _graphicsFile.CreateViewAccessor();

            physicsAccessor.Read(0, out SPageFilePhysics physics);
            graphicsAccessor.Read(0, out SPageFileGraphics graphics);

            state.IsGameRunning = true;
            ReadStaticInfo(state);
            state.FuelLiters = physics.fuel;
            state.SpeedKmh = physics.speedKmh;
            state.Throttle = physics.gas;
            state.Brake = physics.brake;
            state.SteerAngle = physics.steerAngle;
            state.Gear = physics.gear;
            state.LapProgress = Math.Clamp(graphics.normalizedCarPosition, 0f, 1f);
            state.CurrentSectorIndex = graphics.currentSectorIndex;
            state.Rpm = physics.rpms;
            state.HeadingRad = physics.heading;
            state.WheelsOffTrack = physics.numberOfTyresOut;
            state.IsInPit = graphics.isInPit != 0;
            state.SessionFlagRaw = graphics.flag;
            state.SessionStatusRaw = graphics.status;
            state.SessionTypeRaw = graphics.session;
            state.SessionTimeLeftSeconds = graphics.sessionTimeLeft / 1000f;
            state.CurrentLap = graphics.completedLaps + 1;
            state.TotalLaps = graphics.numberOfLaps;
            state.Position = graphics.position;
            // Sanity-check rather than trusting the field blindly. ACC grids top out well
            // below this, so anything larger means we're reading the wrong bytes - better
            // to report nothing than to tell the driver they're "P1 of 1044357427".
            state.TotalCars = graphics.activeCars is > 0 and <= 100 ? graphics.activeCars : 0;
            state.LastLapTimeSeconds = graphics.iLastTime / 1000f;
            state.BestLapTimeSeconds = graphics.iBestTime / 1000f;
            state.CurrentLapTimeSeconds = graphics.iCurrentTime / 1000f;
            state.ImpactG = MathF.Sqrt(physics.accG[0] * physics.accG[0]
                                      + physics.accG[1] * physics.accG[1]
                                      + physics.accG[2] * physics.accG[2]);
            state.GForceLateral = physics.accG[0];
            state.GForceLongitudinal = physics.accG[2];

            for (int i = 0; i < 5; i++)
                state.CarDamageRaw[i] = physics.carDamage[i];

            for (int i = 0; i < 4; i++)
            {
                state.TyreWearRaw[i] = physics.tyreWear[i];
                // NOTE: formula under investigation - was reading a constant 100% for the user
                // regardless of driving, which means either this offset is misreading a static
                // value, or ACC's tyreWear field genuinely barely moves in short stints and needs
                // a different scale. Showing the raw value too (via the "debug tyres" command)
                // so we can calibrate this against real observed wear instead of guessing again.
                state.TyreWearPercent[i] = physics.tyreWear[i] * 100f;
                state.TyreTempCelsius[i] = physics.tyreCoreTemperature[i];
                state.TyrePressurePsi[i] = physics.wheelsPressure[i];
            }

            // Rough fuel-per-lap estimate: needs a couple of laps of history to be meaningful.
            // FuelTracker (see Commands/FuelTracker.cs) maintains that history across reads.
        }
        catch
        {
            state.IsGameRunning = false;
        }

        return state;
    }

    /// <summary>
    /// Loads track, car and tank capacity from the static page, refreshing periodically so
    /// switching track or car mid-run is picked up without restarting PitWatch.
    /// </summary>
    private unsafe void ReadStaticInfo(GameState state)
    {
        if (DateTime.UtcNow - _staticReadAtUtc > TimeSpan.FromSeconds(10))
        {
            try
            {
                _staticFile ??= MemoryMappedFile.OpenExisting("Local\\acpmf_static");
                using var accessor = _staticFile.CreateViewAccessor();
                accessor.Read(0, out SPageFileStatic st);

                // st is a local, so its fixed buffers already live at a stable stack
                // address - they can be passed straight to a pointer parameter. (A `fixed`
                // statement here is actually rejected: you can't pin something already
                // pinned.) Letting the compiler resolve the field offsets from the struct
                // definition is still far safer than hand-computing byte positions.
                _cachedTrack = FixedToString(st.track, 33);
                _cachedCar = FixedToString(st.carModel, 33);

                _cachedMaxFuel = st.maxFuel;
                _staticReadAtUtc = DateTime.UtcNow;
            }
            catch
            {
                // Static page unavailable - not fatal, everything else still works.
                _staticReadAtUtc = DateTime.UtcNow;
            }
        }

        state.TrackName = _cachedTrack;
        state.CarModel = _cachedCar;
        state.MaxFuelLiters = _cachedMaxFuel;
    }

    /// <summary>Converts a fixed-size wide char buffer to a string, stopping at the first
    /// null - the buffer is padded and would otherwise carry trailing junk.</summary>
    private static unsafe string FixedToString(char* buffer, int maxLength)
    {
        int length = 0;
        while (length < maxLength && buffer[length] != '\0') length++;
        return new string(buffer, 0, length).Trim();
    }

    /// <summary>
    /// Reads raw floats directly from the physics shared memory at an arbitrary byte offset,
    /// bypassing the struct entirely. Used to empirically locate fields (like tyre wear) when
    /// the struct-based read is suspected to be misaligned - rather than guessing at C# field
    /// layout again, this lets us look at the actual bytes and match patterns against real
    /// observed driving (e.g. "which of these floats drops after I lock a tyre").
    /// </summary>
    public float[] DumpRawFloats(int startByteOffset, int count)
    {
        _physicsFile ??= MemoryMappedFile.OpenExisting("Local\\acpmf_physics");
        using var accessor = _physicsFile.CreateViewAccessor();
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = accessor.ReadSingle(startByteOffset + i * 4);
        }
        return result;
    }

    /// <summary>
    /// Same idea as DumpRawFloats but for the GRAPHICS page as int32s, since that's where
    /// flag/session data lives (not physics). Used to empirically locate the real flag
    /// offset - our struct-based read returned 0 during an actual double-yellow, which
    /// means either the offset is wrong or ACC doesn't set this field for local flags
    /// the way assumed. Real data beats another blind guess.
    /// </summary>
    public int[] DumpRawGraphicsInts(int startByteOffset, int count)
    {
        _graphicsFile ??= MemoryMappedFile.OpenExisting("Local\\acpmf_graphics");
        using var accessor = _graphicsFile.CreateViewAccessor();
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = accessor.ReadInt32(startByteOffset + i * 4);
        }
        return result;
    }
}
