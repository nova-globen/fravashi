namespace Fravashi.Application.Telemetry;

/// <summary>
/// The opt-in switch for local product-event recording (backlog 74). One settings row, defaulting OFF,
/// read fresh on every record so switching it off in Settings stops the next event rather than the next
/// restart.
/// </summary>
public static class TelemetrySettings
{
    /// <summary>The settings-table key holding the switch.</summary>
    public const string EnabledKey = "Telemetry:Enabled";

    /// <summary>
    /// Whether the stored value switches recording on. Anything that is not a parseable <c>true</c> is
    /// OFF, including a missing row, an empty value and a typo: an opt-in that a malformed value could
    /// switch on would not be an opt-in.
    /// </summary>
    public static bool IsEnabled(string? storedValue) =>
        bool.TryParse(storedValue, out var enabled) && enabled;
}
