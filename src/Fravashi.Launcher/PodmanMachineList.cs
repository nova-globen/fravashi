using System.Text.Json;

namespace Fravashi.Launcher;

/// <summary>One entry of <c>podman machine list --format json</c>, reduced to what the launcher needs.</summary>
/// <param name="Name">The machine name (<c>podman-machine-default</c> on a stock install).</param>
/// <param name="IsDefault">Whether this is the machine <c>podman machine start</c> would pick with no name.</param>
/// <param name="Running">Whether Podman reports it as up.</param>
/// <param name="Starting">Whether Podman reports it as mid-start (a second start would fail).</param>
public sealed record PodmanMachine(string Name, bool IsDefault, bool Running, bool Starting);

/// <summary>
/// Parses <c>podman machine list --format json</c>. Kept separate from <see cref="PodmanRuntime"/> so the
/// decision the launcher makes from that output (start it, refuse because there is none, or report a wedged
/// machine) is unit-testable without a Podman on the machine running the tests.
/// </summary>
public static class PodmanMachineList
{
    /// <summary>
    /// Reads the machine array. Tolerant by design: anything that is not the expected JSON array yields an
    /// empty list, and the caller then reports honestly rather than guessing.
    /// </summary>
    public static IReadOnlyList<PodmanMachine> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var machines = new List<PodmanMachine>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(element, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                machines.Add(new PodmanMachine(
                    name!,
                    ReadBool(element, "Default"),
                    ReadBool(element, "Running"),
                    ReadBool(element, "Starting")));
            }

            return machines;
        }
        catch (JsonException)
        {
            // Not JSON (an error banner, or a Podman build whose --format json is unavailable).
            return [];
        }
    }

    /// <summary>
    /// The machine a nameless <c>podman machine start</c> would act on: the default one, else the only one,
    /// else null when the choice is ambiguous (several machines, none marked default) so nothing is guessed.
    /// </summary>
    public static PodmanMachine? StartTarget(IReadOnlyList<PodmanMachine> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);
        return machines.FirstOrDefault(m => m.IsDefault) ?? (machines.Count == 1 ? machines[0] : null);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
