using System.Text.RegularExpressions;

namespace RoSTools.Sidecar.Core.Blizzard;

/// <summary>
/// One Blizzard API application's client-credentials pair, plus the region it is
/// used against.
/// <para>
/// The secret never leaves this process in plaintext: <see cref="SettingsStore"/>
/// persists it through <see cref="ISecretProtector"/>, and the web console is only
/// ever told whether one is present - see <c>ConsoleApi</c>.
/// </para>
/// </summary>
public sealed partial record BlizzardCredentials(string ClientId, string ClientSecret, string Region)
{
    /// <summary>
    /// The only regions this client will talk to.
    /// <para>
    /// This is a whitelist rather than a format check because the value is
    /// interpolated straight into the API host - <c>https://{region}.api.blizzard.com</c>.
    /// A region of <c>evil.example.com/x</c> would otherwise send the bearer token
    /// to somebody else's server, and the token is minted from the user's own
    /// client secret. <c>cn</c> is deliberately absent: it uses a different OAuth
    /// host and a different API host entirely, so accepting it here would fail in a
    /// confusing way rather than a clear one.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Regions = ["us", "eu", "kr", "tw"];

    public static bool IsKnownRegion(string? region) =>
        region is not null && Regions.Contains(region, StringComparer.Ordinal);

    /// <summary>
    /// Client IDs are lowercase hex, but Blizzard has never promised that, so this
    /// only rejects what could not possibly be one. The point is to catch a
    /// pasted-in whole line ("client_id: abc") before it becomes an auth failure
    /// the user cannot interpret.
    /// </summary>
    public static bool LooksLikeClientId(string? value) =>
        value is not null && ClientIdShape().IsMatch(value);

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{16,128}\z")]
    private static partial Regex ClientIdShape();
}
