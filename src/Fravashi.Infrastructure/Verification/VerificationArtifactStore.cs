using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Verification;

/// <summary>
/// <see cref="IVerificationArtifacts"/> under <c>&lt;data&gt;/verification/&lt;run&gt;/</c> (backlog 224), the evidence class
/// "Verification artifacts" of <c>docs/evidence-lifecycle.md</c> (backlog 75). Every text file passes through
/// <see cref="TranscriptRedactor"/> whole before it is written; a picture cannot be scanned for text and is kept as taken.
/// A reference is <c>verification/&lt;run&gt;</c> and nothing else, and a name is one plain file name, so reading can never
/// leave the folder whatever an evidence record says.
/// </summary>
public sealed partial class VerificationArtifactStore : IVerificationArtifacts
{
    /// <summary>The folder under the data directory every run's folder lives in.</summary>
    public const string FolderName = "verification";

    private readonly DataDirectory _data;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public VerificationArtifactStore(DataDirectory data, TimeProvider? clock = null, ILogger<VerificationArtifactStore>? logger = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<VerificationArtifactStore>.Instance;
    }

    /// <summary>Where every run's folder is.</summary>
    public string Root => Path.Combine(_data.Path, FolderName);

    /// <inheritdoc />
    public async Task<string?> SaveAsync(VerificationArtifactSet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        var run = _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(Root, run);
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var text in set.Texts)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(folder, Checked(text.Name)), TranscriptRedactor.Redact(text.Text), Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var picture in set.Pictures)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, Checked(picture.Name)), picture.Png, cancellationToken)
                    .ConfigureAwait(false);
            }

            return FolderName + "/" + run;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning("Verification artifacts could not be written: {Error}", ex.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (FolderOf(reference) is not { } folder || !Directory.Exists(folder))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> names = [.. Directory.EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => PlainName().IsMatch(n))
            .Order(StringComparer.Ordinal)];
        return Task.FromResult(names);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string reference, string name, CancellationToken cancellationToken = default)
    {
        if (FolderOf(reference) is not { } folder || name is null || !PlainName().IsMatch(name))
        {
            return null;
        }

        var file = Path.Combine(folder, name);
        try
        {
            return File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("A verification artifact could not be read: {Error}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The run's folder, or null when <paramref name="reference"/> is not one this store makes.</summary>
    private string? FolderOf(string? reference)
    {
        if (reference is null || !reference.StartsWith(FolderName + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var run = reference[(FolderName.Length + 1)..];
        return RunName().IsMatch(run) ? Path.Combine(Root, run) : null;
    }

    private static string Checked(string name) =>
        PlainName().IsMatch(name) ? name : throw new ArgumentException($"'{name}' is not a plain file name.", nameof(name));

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}
