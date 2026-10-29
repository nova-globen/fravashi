using Fravashi.Infrastructure.Sessions;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 295: container-shaped facts handed to <see cref="CommitContainerCoverage.Scan"/> so a test holds
/// wherever the test SUITE ITSELF runs (a Windows dev host carries pwsh and powershell.exe on PATH, which the
/// studio's own Linux container never does), rather than reading this process's own platform and PATH.
/// </summary>
internal sealed class FakeContainerFacts : IContainerFacts
{
    public static readonly FakeContainerFacts Container = new() { IsWindows = false, HasPowerShellOnPath = false };

    public bool IsWindows { get; init; }

    public bool HasPowerShellOnPath { get; init; }

    private readonly Dictionary<string, bool> _envVars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _existingFiles = new(StringComparer.Ordinal);

    public FakeContainerFacts WithEnvVar(string name, bool isSet)
    {
        _envVars[name] = isSet;
        return this;
    }

    public FakeContainerFacts WithExistingFile(string path)
    {
        _existingFiles.Add(path);
        return this;
    }

    public bool EnvVarIsSet(string name) => _envVars.TryGetValue(name, out var isSet) && isSet;

    public bool FileExists(string path) => _existingFiles.Contains(path);
}
