using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PitWatch.Voice;

/// <summary>
/// Records microphone audio while a bound button is held, then transcribes it.
///
/// WHY NOT THE OLD APPROACH: the original version used Windows' built-in System.Speech
/// recognizer, which was so inaccurate it was unusable ("tyres" came out as "Thai air").
/// This records raw audio instead and sends it to Gemini for transcription, which is
/// dramatically more accurate - the same reason Windows' own modern dictation works well
/// while the legacy API doesn't.
///
/// WHEEL BUTTON SUPPORT: joystick/wheel buttons are read through the legacy winmm
/// joystick API. It's old but has no dependencies and works with essentially every wheel
/// Windows recognises. Keyboard keys still work too - whichever is configured.
///
/// NOTE: this needs a Gemini API key to transcribe. Without one, voice input is
/// unavailable and PitWatch falls back to typed questions.
/// </summary>
public class VoiceInput : IDisposable
{
    // --- winmm joystick API for wheel buttons ---
    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos, dwYpos, dwZpos;
        public int dwRpos, dwUpos, dwVpos;
        public int dwButtons;
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1, dwReserved2;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int JOY_RETURNBUTTONS = 0x00000080;

    // --- audio capture via winmm waveIn ---
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    private bool _isRecording;
    private string? _currentRecordingPath;

    /// <summary>True while the configured button/key is currently held down.</summary>
    public bool IsButtonHeld(string binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return false;

        if (binding.StartsWith("joy", StringComparison.OrdinalIgnoreCase))
        {
            // Format: "joy0:3" = joystick 0, button 3
            var parts = binding.Substring(3).Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int joyId) || !int.TryParse(parts[1], out int button))
                return false;

            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(joyId, ref info) != 0) return false;
            return (info.dwButtons & (1 << button)) != 0;
        }

        if (binding.StartsWith("key", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(binding.Substring(3), out int vk))
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        return false;
    }

    /// <summary>Scans all joysticks for a currently-pressed button - used by the
    /// "press a button to bind it" flow in Settings.</summary>
    public static string? DetectPressedButton()
    {
        for (int joyId = 0; joyId < 4; joyId++)
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(joyId, ref info) != 0) continue;
            if (info.dwButtons == 0) continue;

            for (int b = 0; b < 32; b++)
                if ((info.dwButtons & (1 << b)) != 0)
                    return $"joy{joyId}:{b}";
        }
        return null;
    }

    public void StartRecording()
    {
        if (_isRecording) return;
        try
        {
            _currentRecordingPath = Path.Combine(Path.GetTempPath(), $"pitwatch_voice_{Guid.NewGuid():N}.wav");
            // MCI is used rather than a NuGet audio library to keep this dependency-free.
            mciSendString("open new type waveaudio alias pitwatch_rec", null, 0, IntPtr.Zero);
            mciSendString("set pitwatch_rec time format ms bitspersample 16 channels 1 samplespersec 16000", null, 0, IntPtr.Zero);
            mciSendString("record pitwatch_rec", null, 0, IntPtr.Zero);
            _isRecording = true;
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't start voice recording.", ex);
            _isRecording = false;
        }
    }

    /// <summary>Stops recording and returns the WAV file path, or null if it failed.</summary>
    public string? StopRecording()
    {
        if (!_isRecording) return null;
        _isRecording = false;

        try
        {
            mciSendString("stop pitwatch_rec", null, 0, IntPtr.Zero);
            mciSendString($"save pitwatch_rec \"{_currentRecordingPath}\"", null, 0, IntPtr.Zero);
            mciSendString("close pitwatch_rec", null, 0, IntPtr.Zero);

            if (File.Exists(_currentRecordingPath))
            {
                var info = new FileInfo(_currentRecordingPath);
                // Very small files mean the button was tapped rather than held - not speech.
                if (info.Length < 4000) { File.Delete(_currentRecordingPath); return null; }
                return _currentRecordingPath;
            }
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't save voice recording.", ex);
        }
        return null;
    }

    public void Dispose()
    {
        if (_isRecording)
        {
            try { mciSendString("close pitwatch_rec", null, 0, IntPtr.Zero); } catch { }
        }
    }
}
