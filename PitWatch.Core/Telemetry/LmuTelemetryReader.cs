using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using PitWatch.Models;

namespace PitWatch.Telemetry;

/// <summary>
/// Reads Le Mans Ultimate telemetry via the rF2 Shared Memory Map Plugin.
///
/// PREVIOUS BUG: this used to read the vehicle struct starting at byte 0 of the mapped
/// file. But the rF2 buffers begin with a 16-byte header (two version counters, a size
/// hint, and the vehicle count) before the vehicle array starts - so every field was
/// offset by 16 bytes and read parts of neighbouring values. That's why lap number came
/// back as numbers like 18,383,291: it was reading an internal version counter.
///
/// TEAR-FREE READS: the plugin increments mVersionUpdateBegin before writing and
/// mVersionUpdateEnd after. If they don't match, the buffer was being written mid-read and
/// the data may be half-old, half-new - so we retry rather than report a mixture.
/// </summary>
public class LmuTelemetryReader : ITelemetryProvider
{
    public string Name => "LMU";

    private const string TelemetryMapName = "$rFactor2SMMP_Telemetry$";
    private const string ScoringMapName = "$rFactor2SMMP_Scoring$";

    /// <summary>Two version ints, a bytes-updated hint, and the vehicle count.</summary>
    private const int BufferHeaderSize = 16;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RF2Vec3
    {
        public double X, Y, Z;
    }

    /// <summary>
    /// Fields are in the exact order the plugin writes them - the order is what determines
    /// the byte offsets, so nothing here can be reordered or omitted even when unused.
    /// Truncated after mFuel, which is the last field PitWatch currently needs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct RF2VehicleTelemetry
    {
        public int mID;
        public double mDeltaTime;
        public double mElapsedTime;
        public int mLapNumber;
        public double mLapStartET;
        public fixed byte mVehicleName[64];
        public fixed byte mVehicleClass[64];

        public RF2Vec3 mPos;
        public RF2Vec3 mLocalVel;
        public RF2Vec3 mLocalAccel;

        public RF2Vec3 mOri0;
        public RF2Vec3 mOri1;
        public RF2Vec3 mOri2;
        public RF2Vec3 mLocalRot;
        public RF2Vec3 mLocalRotAccel;

        public int mGear;
        public double mEngineRPM;
        public double mEngineWaterTemp;
        public double mEngineOilTemp;
        public double mClutchRPM;

        public double mUnfilteredThrottle;
        public double mUnfilteredBrake;
        public double mUnfilteredSteering;
        public double mUnfilteredClutch;

        public double mFilteredThrottle;
        public double mFilteredBrake;
        public double mFilteredSteering;
        public double mFilteredClutch;

        public double mSteeringShaftTorque;
        public double mFront3rdDeflection;
        public double mRear3rdDeflection;

        public double mFrontWingHeight;
        public double mFrontRideHeight;
        public double mRearRideHeight;
        public double mDrag;
        public double mFrontDownforce;
        public double mRearDownforce;

        public double mFuel;
        public double mEngineMaxRPM;
    }

    private MemoryMappedFile? _telemetryFile;

    // The shared memory mapping can outlive the game process, so its mere existence isn't
    // proof LMU is running. The plugin's elapsed-time value advances continuously while a
    // session is live, so if it stops changing we treat the game as gone rather than
    // reporting "Connected" over frozen data.
    private double _lastElapsedTime = -1;
    private DateTime _lastElapsedChangeUtc = DateTime.UtcNow;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    public bool IsAvailable()
    {
        try
        {
            using var test = MemoryMappedFile.OpenExisting(TelemetryMapName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public GameState ReadState()
    {
        // These come from rF2's separate Scoring buffer, which PitWatch doesn't read yet -
        // flagging them here lets the dashboard hide those panels rather than show blanks.
        var state = new GameState
        {
            GameName = "LMU",
            HasSectorData = false,
            HasTyreData = false,
            HasDamageData = false,
            HasPositionData = false,
            HasSessionTimeData = false,
            HasGForceData = false,
            SupportsTrackMap = false,
        };

        try
        {
            _telemetryFile ??= MemoryMappedFile.OpenExisting(TelemetryMapName);
            using var accessor = _telemetryFile.CreateViewAccessor();

            // Retry a couple of times if we catch the buffer mid-write.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int versionBegin = accessor.ReadInt32(0);
                int numVehicles = accessor.ReadInt32(12);

                if (numVehicles <= 0 || numVehicles > 128)
                {
                    // No cars in the session yet - connected but nothing to report.
                    state.IsGameRunning = false;
                    return state;
                }

                // The player is vehicle 0 in the telemetry buffer. (Scoring orders cars by
                // race position instead, which is why that buffer isn't used here.)
                accessor.Read(BufferHeaderSize, out RF2VehicleTelemetry v);

                int versionEnd = accessor.ReadInt32(4);
                if (versionBegin != versionEnd) continue; // torn read, try again

                if (Math.Abs(v.mElapsedTime - _lastElapsedTime) > 0.0001)
                {
                    _lastElapsedTime = v.mElapsedTime;
                    _lastElapsedChangeUtc = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - _lastElapsedChangeUtc > StaleAfter)
                {
                    // Data frozen - LMU has closed or the session ended.
                    state.IsGameRunning = false;
                    return state;
                }

                state.IsGameRunning = true;
                state.CurrentLap = Math.Max(v.mLapNumber, 0);
                state.FuelLiters = (float)v.mFuel;
                // rF2 uses -1 = reverse, 0 = neutral, 1 = first gear. ACC uses
                // 0 = reverse, 1 = neutral, 2 = first. Normalising to the ACC convention
                // here keeps a single gear-display path instead of the UI having to know
                // which game it's talking to - without this, first gear showed as "N".
                state.Gear = v.mGear + 1;
                state.Rpm = (int)v.mEngineRPM;

                // Speed is the magnitude of the local velocity vector, in m/s.
                double speedMs = Math.Sqrt(
                    v.mLocalVel.X * v.mLocalVel.X +
                    v.mLocalVel.Y * v.mLocalVel.Y +
                    v.mLocalVel.Z * v.mLocalVel.Z);
                state.SpeedKmh = (float)(speedMs * 3.6);

                state.Throttle = (float)v.mFilteredThrottle;
                state.Brake = (float)v.mFilteredBrake;
                state.SteerAngle = (float)v.mFilteredSteering;

                return state;
            }

            // Three torn reads in a row is unusual but not fatal - report not-running for
            // this tick rather than passing on data that may be inconsistent.
            state.IsGameRunning = false;
        }
        catch (FileNotFoundException)
        {
            // LMU closed, or the plugin isn't loaded. Normal, not worth logging each tick.
            _telemetryFile = null;
            state.IsGameRunning = false;
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Warn($"LMU telemetry read failed: {ex.Message}");
            _telemetryFile = null;
            state.IsGameRunning = false;
        }

        return state;
    }

    /// <summary>
    /// Diagnostic dump for verifying the struct layout against real data - the same
    /// approach that pinned down the ACC field offsets. Reports the raw header plus the
    /// key vehicle fields so wrong values are immediately obvious.
    /// </summary>
    public string DumpRaw()
    {
        try
        {
            _telemetryFile ??= MemoryMappedFile.OpenExisting(TelemetryMapName);
            using var accessor = _telemetryFile.CreateViewAccessor();

            int versionBegin = accessor.ReadInt32(0);
            int versionEnd = accessor.ReadInt32(4);
            int bytesHint = accessor.ReadInt32(8);
            int numVehicles = accessor.ReadInt32(12);

            accessor.Read(BufferHeaderSize, out RF2VehicleTelemetry v);

            double speedMs = Math.Sqrt(
                v.mLocalVel.X * v.mLocalVel.X +
                v.mLocalVel.Y * v.mLocalVel.Y +
                v.mLocalVel.Z * v.mLocalVel.Z);

            return $"header: versionBegin={versionBegin} versionEnd={versionEnd} bytesHint={bytesHint} numVehicles={numVehicles}\n"
                 + $"vehicle0: id={v.mID} lap={v.mLapNumber} elapsed={v.mElapsedTime:F1}s lapStart={v.mLapStartET:F1}\n"
                 + $"  fuel={v.mFuel:F2}L gear={v.mGear} rpm={v.mEngineRPM:F0} maxRpm={v.mEngineMaxRPM:F0}\n"
                 + $"  speed={speedMs * 3.6:F1}km/h pos=({v.mPos.X:F1},{v.mPos.Y:F1},{v.mPos.Z:F1})\n"
                 + $"  throttle={v.mFilteredThrottle:F2} brake={v.mFilteredBrake:F2} steer={v.mFilteredSteering:F2}\n"
                 + $"  waterTemp={v.mEngineWaterTemp:F1} oilTemp={v.mEngineOilTemp:F1}\n"
                 + $"(struct size: {Marshal.SizeOf<RF2VehicleTelemetry>()} bytes)";
        }
        catch (Exception ex)
        {
            return $"Couldn't read LMU telemetry: {ex.Message}";
        }
    }
}
