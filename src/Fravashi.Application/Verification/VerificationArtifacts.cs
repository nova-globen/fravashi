namespace Fravashi.Application.Verification;

/// <summary>A text file kept with a verification run: a report, a console, a log.</summary>
/// <param name="Name">The file's name in the run's folder, such as <c>report.md</c>.</param>
/// <param name="Text">What it says. The store redacts it before it is written.</param>
public sealed record VerificationText(string Name, string Text);

/// <summary>A picture kept with a verification run.</summary>
/// <param name="Name">The file's name in the run's folder, such as <c>screen-0.wide.00-at-rest.build.png</c>.</param>
/// <param name="Png">The picture, PNG.</param>
public sealed record VerificationPicture(string Name, ReadOnlyMemory<byte> Png);

/// <summary>Everything one verification run keeps on disk (backlog 224).</summary>
/// <param name="Texts">The report, the consoles and the application's logs.</param>
/// <param name="Pictures">The screenshots, build and prototype paired by name.</param>
public sealed record VerificationArtifactSet(IReadOnlyList<VerificationText> Texts, IReadOnlyList<VerificationPicture> Pictures);

/// <summary>
/// Where a verification run's evidence lives (backlog 224, the evidence-lifecycle class "Verification artifacts" of
/// backlog 75). A run's files are written once into a folder of their own, and the evidence record names that folder by
/// the reference <see cref="SaveAsync"/> returns. Text is redacted before it is written; pictures are kept as taken.
/// </summary>
public interface IVerificationArtifacts
{
    /// <summary>
    /// Writes <paramref name="set"/> into a new folder and returns the reference the evidence record carries, or null when
    /// nothing could be written. Never throws for a disk failure.
    /// </summary>
    Task<string?> SaveAsync(VerificationArtifactSet set, CancellationToken cancellationToken = default);

    /// <summary>The names of the files a run kept, in name order; empty when the folder is gone (deleted under backlog 75).</summary>
    Task<IReadOnlyList<string>> ListAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>One file of a run, or null when the reference or the name is not one of the store's.</summary>
    Task<byte[]?> ReadAsync(string reference, string name, CancellationToken cancellationToken = default);
}
