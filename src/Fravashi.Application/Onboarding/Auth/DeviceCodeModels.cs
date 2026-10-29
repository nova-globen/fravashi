namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// The device + user code a device-authorization grant returns from its first call. The wizard shows
/// <see cref="UserCode"/> and <see cref="VerificationUri"/> to the user and then polls with
/// <see cref="DeviceCode"/>.
/// </summary>
/// <param name="DeviceCode">The secret code the client polls the token endpoint with (not shown to the user).</param>
/// <param name="UserCode">The short code the user types at the verification page.</param>
/// <param name="VerificationUri">The URL the user opens to enter the code and consent.</param>
/// <param name="IntervalSeconds">The minimum seconds between polls the provider asks for.</param>
/// <param name="ExpiresInSeconds">How long the device code is valid before the flow must restart.</param>
/// <param name="Message">A provider-supplied human instruction (Entra sends one), or null.</param>
public sealed record DeviceCodeStart(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int IntervalSeconds,
    int ExpiresInSeconds,
    string? Message);

/// <summary>
/// The state of a single device-code poll. This is the alphabet of the polling state machine: the driver
/// keeps polling while <see cref="Pending"/>, widens the interval on <see cref="SlowDown"/> (GitHub only -
/// Entra never sends it), and stops on any terminal state.
/// </summary>
public enum DevicePollState
{
    /// <summary>The user has not finished authorizing yet - keep polling.</summary>
    Pending,

    /// <summary>Polled too fast - add to the interval and keep polling (GitHub <c>slow_down</c>).</summary>
    SlowDown,

    /// <summary>The user authorized and a token was issued - terminal, success.</summary>
    Success,

    /// <summary>The user denied the request - terminal, failure.</summary>
    Denied,

    /// <summary>The device code expired before the user finished - terminal; restart the flow.</summary>
    Expired,

    /// <summary>An unexpected error (network, malformed response, unknown error code) - terminal.</summary>
    Error,
}

/// <summary>The outcome of a single device-code poll, plus the token on success and a detail for the UI.</summary>
public sealed record DevicePollResult(DevicePollState State, StoredCredential? Credential, string? Detail)
{
    public static DevicePollResult Pending() => new(DevicePollState.Pending, null, null);
    public static DevicePollResult SlowDown() => new(DevicePollState.SlowDown, null, null);
    public static DevicePollResult Success(StoredCredential credential) => new(DevicePollState.Success, credential, null);
    public static DevicePollResult Denied(string detail) => new(DevicePollState.Denied, null, detail);
    public static DevicePollResult Expired(string detail) => new(DevicePollState.Expired, null, detail);
    public static DevicePollResult Error(string detail) => new(DevicePollState.Error, null, detail);
}

/// <summary>
/// The pure state-transition rule for device-code polling, isolated so it can be unit-tested without any HTTP
/// or timing. Given the interval currently in effect and the outcome of a single poll, it yields the next
/// interval and whether polling should continue. On <see cref="DevicePollState.SlowDown"/> GitHub asks the
/// client to add five seconds to the interval; every terminal state stops the loop.
/// </summary>
public static class DeviceCodePolling
{
    public const int SlowDownIncrementSeconds = 5;

    public static (int NextIntervalSeconds, bool Continue) Next(int currentIntervalSeconds, DevicePollState state) => state switch
    {
        DevicePollState.SlowDown => (currentIntervalSeconds + SlowDownIncrementSeconds, true),
        DevicePollState.Pending => (currentIntervalSeconds, true),
        _ => (currentIntervalSeconds, false),
    };
}
