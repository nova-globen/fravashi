using System.Runtime.InteropServices;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>Where an install is, in the order it happens.</summary>
public enum NodeProvisionPhase
{
    /// <summary>Turning a request like "22" into an exact release, from the distribution index.</summary>
    Resolving,

    /// <summary>The tarball is streaming in; bytes are reported.</summary>
    Downloading,

    /// <summary>The downloaded bytes are being checked against SHASUMS256.txt.</summary>
    Verifying,

    /// <summary>The verified tarball is being unpacked into a staging folder.</summary>
    Unpacking,

    /// <summary>The unpacked tree is being moved into the nvs layout.</summary>
    Installing,
}

/// <summary>A progress report from an install; the coordinator turns these into what the banner shows.</summary>
/// <param name="Phase">Where the install is.</param>
/// <param name="BytesReceived">Bytes of the tarball received so far (meaningful while downloading).</param>
/// <param name="TotalBytes">The tarball's size when the server said, else null.</param>
/// <param name="Version">The exact version once resolved.</param>
public sealed record NodeProvisionProgress(
    NodeProvisionPhase Phase,
    long BytesReceived = 0,
    long? TotalBytes = null,
    string? Version = null);

/// <summary>How an install ended. Never half-done: on failure nothing of it is left in the layout.</summary>
/// <param name="Succeeded">True when the version's bin folder is in the layout now.</param>
/// <param name="Version">The exact version installed (or the one that failed, once it was resolved).</param>
/// <param name="BinDirectory">The bin folder the PATH lookup will serve, on success.</param>
/// <param name="Error">Why it failed, in a sentence for the banner.</param>
/// <param name="SkippedLinks">Symbolic links the unpack could not create (only ever on a non-Linux host, see <see cref="NodeProvisioner"/>).</param>
public sealed record NodeProvisionResult(
    bool Succeeded,
    string? Version,
    string? BinDirectory,
    string? Error,
    int SkippedLinks = 0)
{
    public static NodeProvisionResult Failure(string error, string? version = null) => new(false, version, null, error);
}

/// <summary>
/// Installs a Node version into the nvs layout <see cref="NodePathProvisioner"/> reads. One
/// implementation fetches nodejs.org; the banner tests substitute one that reports what they tell it to.
/// </summary>
public interface INodeProvisioner
{
    /// <summary>
    /// Installs <paramref name="requestedVersion"/> ("22", "22.12" or "22.12.0"), reporting progress along
    /// the way. Returns rather than throws for every expected failure (no network, a hash mismatch, a
    /// corrupt archive, no such version): the result carries the reason.
    /// </summary>
    Task<NodeProvisionResult> InstallAsync(
        string requestedVersion,
        IProgress<NodeProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The distribution the container needs: the official tarballs are named by OS and architecture
/// (<c>node-v22.12.0-linux-x64.tar.gz</c>), and the nvs layout keys the install by its own arch name.
/// </summary>
/// <param name="Os">The distribution OS segment; the studio only ever installs into a Linux container.</param>
/// <param name="Arch">The distribution architecture segment, which nvs uses as the layout folder too.</param>
public sealed record NodeDistributionPlatform(string Os, string Arch)
{
    public static NodeDistributionPlatform LinuxX64 { get; } = new("linux", "x64");

    public static NodeDistributionPlatform LinuxArm64 { get; } = new("linux", "arm64");

    /// <summary>The <c>files</c> key nodejs.org's index uses for this platform's tarball.</summary>
    public string IndexKey => $"{Os}-{Arch}";

    /// <summary>The tarball name under <c>{dist}/v{version}/</c>.</summary>
    public string ArchiveNameFor(string version) => $"node-v{version}-{Os}-{Arch}.tar.gz";

    /// <summary>
    /// The platform of the process, when it is one the official distribution serves for the container:
    /// Linux on x64 or arm64. Anything else (a Windows dev host, a 32-bit or exotic arch) is null, and an
    /// install then fails with that reason rather than fetching a tarball that could not run here.
    /// </summary>
    public static NodeDistributionPlatform? Detect()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => LinuxX64,
            Architecture.Arm64 => LinuxArm64,
            _ => null,
        };
    }
}
