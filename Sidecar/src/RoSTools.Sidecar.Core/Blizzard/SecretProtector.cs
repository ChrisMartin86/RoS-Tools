using System.Security.Cryptography;
using System.Text;

namespace RoSTools.Sidecar.Core.Blizzard;

/// <summary>
/// Encrypts the Blizzard client secret at rest.
/// <para>
/// An interface rather than a direct DPAPI call for one structural reason: this
/// assembly targets plain <c>net10.0</c> so the xunit suite runs on
/// <c>ubuntu-latest</c> (see the csproj), and DPAPI is Windows-only. Tests
/// substitute <see cref="PassthroughSecretProtector"/>; the tray app never does.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Whether a secret may be persisted through this implementation. False means
    /// there is no way to encrypt it here, and the caller must refuse to store one
    /// rather than fall back to plaintext on disk.
    /// </summary>
    bool CanStoreSecrets { get; }

    /// <summary>Plaintext to a base64 blob safe to write into sidecar.json.</summary>
    string Protect(string plaintext);

    /// <summary>Blob back to plaintext, or null when it cannot be decrypted.</summary>
    string? Unprotect(string protectedValue);
}

/// <summary>
/// Windows DPAPI, <c>CurrentUser</c> scope. Another account on the same machine -
/// and anyone who copies sidecar.json off it - gets ciphertext they cannot use.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Mixed into the DPAPI ciphertext. Not a secret and not a substitute for one:
    /// it scopes the blob to this app, so a blob lifted from some other DPAPI
    /// consumer running as the same user cannot be decrypted through this path.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RoSTools.Sidecar/blizzard-credentials/v1");

    public bool CanStoreSecrets => true;

    public string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            Entropy,
            DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string protectedValue)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue),
                Entropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // A blob from another Windows account, another machine, or a profile
            // that has been reset. Not an error worth failing over - the user just
            // has to re-enter the secret - but worth a line, because otherwise
            // "credentials disappeared" has no explanation anywhere.
            Log.Warn($"the stored Blizzard secret could not be decrypted: {ex.Message}");
            return null;
        }
    }

    /// <summary>DPAPI where it exists, and nothing anywhere else.</summary>
    public static ISecretProtector Default =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new UnavailableSecretProtector();
}

/// <summary>
/// What non-Windows builds get. Every <see cref="Protect"/> throws rather than
/// silently writing a secret to disk in the clear - a fallback that "worked" would
/// be the worst possible outcome here.
/// </summary>
public sealed class UnavailableSecretProtector : ISecretProtector
{
    public bool CanStoreSecrets => false;

    public string Protect(string plaintext) =>
        throw new PlatformNotSupportedException(
            "Storing the Blizzard secret needs Windows DPAPI. Use the environment " +
            "variables BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET instead.");

    public string? Unprotect(string protectedValue) => null;
}

/// <summary>
/// Test-only, and never wired into the tray app - <c>TrayContext</c> constructs
/// <see cref="DpapiSecretProtector.Default"/>, which degrades to
/// <see cref="UnavailableSecretProtector"/> off Windows. It reports that it can
/// store so the API's persistence path is reachable in tests on Linux; what it
/// writes is reversible, which is exactly why it must never ship.
/// </summary>
public sealed class PassthroughSecretProtector : ISecretProtector
{
    public bool CanStoreSecrets => true;

    public string Protect(string plaintext) =>
        "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith("plain:", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[6..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
