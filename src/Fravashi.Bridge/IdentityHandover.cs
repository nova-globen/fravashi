using System.Text.Json;

namespace Fravashi.Bridge;

/// <summary>
/// What the studio tells the host about its sign-in (backlog 94): which identity provider and client it signs in
/// with, and whether its own session was still usable the last time it looked. None of it is secret; it is what the
/// launcher needs to sign the user in BEFORE the container starts, and to know when that is worth doing.
/// </summary>
/// <param name="Issuer">The identity provider's issuer URL.</param>
/// <param name="ClientId">The client the studio signs in as.</param>
/// <param name="Scope">The scopes it asks for.</param>
/// <param name="SessionUsable">True when the studio held a session it can keep alive by itself (refreshable).</param>
/// <param name="UpdatedUtc">When the studio said so.</param>
public sealed record SignInCoordinates(
    string Issuer, string ClientId, string Scope, bool SessionUsable, DateTimeOffset UpdatedUtc);

/// <summary>
/// A session the launcher obtained, waiting for the studio to take it (backlog 94). The studio validates it with the
/// identity provider before using any of it; nothing here is trusted because of where it came from.
/// </summary>
public sealed record HandedIdentity(
    string Issuer,
    string ClientId,
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    DateTimeOffset? ExpiresUtc);

/// <summary>
/// The host side of the sign-in hand-over (backlog 94, ADR 0026). Two things live here, deliberately apart:
/// <list type="bullet">
/// <item>The studio's sign-in COORDINATES, persisted beside <c>workspaces.json</c> as <c>identity.json</c>
/// because a fresh fravashi.exe needs them before any container runs. Non-secret: an issuer, a client id, a scope
/// and a yes/no.</item>
/// <item>At most ONE handed-over session, held in this process's memory only and given out exactly once. It is
/// never written to disk, so a crash or an exit simply loses it and the studio falls back to its own sign-in
/// page, which is the safe direction.</item>
/// </list>
/// Why the bridge and not the container's environment (which ADR 0026 first suggested): an identity token is a
/// snapshot that expires, and a container restarted by <c>fravashi stop</c> then <c>fravashi</c> keeps the environment it
/// was created with. Taking it once over the bridge, which only the studio holds a token for, needs no recreate and
/// cannot go stale in a variable.
/// </summary>
public sealed class IdentityHandover(string coordinatesPath)
{
    /// <summary>The file name beside <c>bridge.token</c> and <c>workspaces.json</c>.</summary>
    public const string FileName = "identity.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private HandedIdentity? _waiting;

    /// <summary>Records what the studio said about its sign-in. Never throws: a failed write only costs a browser sign-in later.</summary>
    public bool Publish(SignInCoordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (string.IsNullOrWhiteSpace(coordinates.Issuer) || string.IsNullOrWhiteSpace(coordinates.ClientId))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(coordinatesPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = coordinatesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(coordinates, Json));
            File.Move(temporary, coordinatesPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What the studio last said, or null when it never has (or the file is unreadable).</summary>
    public SignInCoordinates? Coordinates()
    {
        try
        {
            if (!File.Exists(coordinatesPath))
            {
                return null;
            }

            var read = JsonSerializer.Deserialize<SignInCoordinates>(File.ReadAllText(coordinatesPath), Json);
            return read is { Issuer.Length: > 0, ClientId.Length: > 0 } ? read : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Holds a session for the studio to take, replacing any that was never taken.</summary>
    public void Offer(HandedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            _waiting = identity;
        }
    }

    /// <summary>The waiting session, exactly once; null when there is none or it was already taken.</summary>
    public HandedIdentity? Take()
    {
        lock (_gate)
        {
            var taken = _waiting;
            _waiting = null;
            return taken;
        }
    }

    /// <summary>True while a session waits to be taken. For logs and tests; never exposes the session.</summary>
    public bool HasWaiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting is not null;
            }
        }
    }
}
