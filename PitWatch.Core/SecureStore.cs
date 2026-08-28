using System.Security.Cryptography;
using System.Text;

namespace PitWatch;

/// <summary>
/// Encrypts API keys before they're written to config.json.
///
/// WHY: previously keys sat in the config file as plain readable text, which matters
/// because the natural way to share PitWatch with a friend is to zip the publish folder -
/// and that folder contains config.json. Anyone doing that would have been handing over
/// their own Gemini/ElevenLabs key without realising.
///
/// Uses Windows DPAPI tied to the current user account, so an encrypted key copied to
/// another machine (or another user) simply fails to decrypt rather than leaking. That's
/// the desired behaviour here - keys shouldn't travel.
///
/// Values are stored with a marker prefix so plaintext keys from older installs still
/// load correctly and get upgraded to encrypted form on the next save.
/// </summary>
public static class SecureStore
{
    private const string Marker = "enc:v1:";

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (plainText.StartsWith(Marker)) return plainText; // already encrypted

        try
        {
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.CurrentUser);
            return Marker + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            // If encryption isn't available for some reason, storing the key still needs
            // to work - fall back to plaintext rather than losing the user's key entirely.
            Logger.Warn($"Couldn't encrypt a stored key, saving as plain text instead: {ex.Message}");
            return plainText;
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Marker)) return stored; // legacy plaintext value

        try
        {
            byte[] raw = Convert.FromBase64String(stored[Marker.Length..]);
            byte[] decrypted = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            // Most likely cause: the config was copied from another machine or user
            // account, which DPAPI deliberately can't decrypt. Treat it as "no key set"
            // so the app still starts and the user can just paste theirs in.
            Logger.Warn($"Couldn't decrypt a stored key (config likely came from another PC): {ex.Message}");
            return "";
        }
    }
}
