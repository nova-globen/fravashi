namespace Fravashi.AppRunner;

/// <summary>
/// The app runner's NARROW model of a project's container spec (ADR 0027, "What a spec may ask for"). It is the only
/// thing a spec can influence, and it carries nothing an engine could be told to do with the host: no mount, no
/// capability, no device, no network mode, no published port, no engine flag. A field a spec might contain that is
/// not here cannot reach the engine, because there is no code that would carry it.
/// </summary>
/// <param name="SpecPath">The spec file this was read from, relative to the workspace.</param>
/// <param name="BaseImage">An image to start from (devcontainer <c>image</c>), or null when a Dockerfile builds it.</param>
/// <param name="Dockerfile">The Dockerfile, relative to the workspace, or null when <see cref="BaseImage"/> is used.</param>
/// <param name="Context">The build context, relative to the workspace (for a Dockerfile build).</param>
/// <param name="Target">A build stage to stop at, or null.</param>
/// <param name="BuildArgs">Plain-string build arguments.</param>
/// <param name="Command">The command that starts the application, as argv; null runs the image's own.</param>
/// <param name="EntryPort">The port the application listens on inside its container; the one way in.</param>
/// <param name="User">The user to run as inside the image, or null for the image's own.</param>
/// <param name="Environment">Plain-string environment for the application.</param>
/// <param name="WorkspaceFolder">Where the project's source is placed inside the image.</param>
/// <param name="Ceilings">The ceilings, already lowered by whatever the spec asked for.</param>
/// <param name="NotRead">Keys that belong to editor tooling, named so it is visible that they were not applied.</param>
public sealed record RunSpec(
    string SpecPath,
    string? BaseImage,
    string? Dockerfile,
    string Context,
    string? Target,
    IReadOnlyDictionary<string, string> BuildArgs,
    IReadOnlyList<string>? Command,
    int? EntryPort,
    string? User,
    IReadOnlyDictionary<string, string> Environment,
    string WorkspaceFolder,
    Ceilings Ceilings,
    IReadOnlyList<string> NotRead);

/// <summary>A field of a spec the app runner will not carry out, named, with why.</summary>
public sealed record SpecRefusal(string Field, string Reason)
{
    public override string ToString() => $"{Field}: {Reason}";
}

/// <summary>What reading a spec produced: a model, or the refusals that stop it. Never both.</summary>
public sealed record SpecReadResult(RunSpec? Spec, IReadOnlyList<SpecRefusal> Refusals)
{
    public static SpecReadResult Refused(params SpecRefusal[] refusals) => new(null, refusals);
}
