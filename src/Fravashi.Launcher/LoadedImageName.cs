namespace Fravashi.Launcher;

/// <summary>Reads what an engine's <c>load</c> said it loaded (backlog 190).</summary>
public static class LoadedImages
{
    /// <summary>
    /// The names in a <c>load</c> answer. Measured on wslc 3.0.1 and the shape Docker and current podman print:
    /// <c>Loaded image: localhost/fravashi:latest</c>, one line per name. Older podman prints
    /// <c>Loaded image(s): a, b</c>. A <c>Loaded image ID: sha256:...</c> line is an untagged image and names
    /// nothing, so it is skipped. Anything else (progress bars, banners) is ignored.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? loadOutput)
    {
        if (string.IsNullOrWhiteSpace(loadOutput))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var raw in loadOutput.Split('\n'))
        {
            var line = raw.Trim();
            var rest = line.StartsWith("Loaded image:", StringComparison.Ordinal) ? line["Loaded image:".Length..]
                : line.StartsWith("Loaded image(s):", StringComparison.Ordinal) ? line["Loaded image(s):".Length..]
                : null;
            if (rest is null)
            {
                continue;
            }

            foreach (var name in rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }
}

/// <summary>An image a <c>load</c> brought in: the name the engine said it gave it, and the id that name resolves to.</summary>
public sealed record LoadedImage(string Source, string Id);

/// <summary>
/// Makes the name the launcher creates the studio from answer to the image a <c>load</c> just brought in (backlog
/// 190). The release tarball names its image <c>localhost/fravashi:latest</c> (<c>setup/build-bundle.sh</c>, so a
/// Docker-built tarball lands where podman looks) and launcher.json names <c>fravashi:latest</c>. On podman those are
/// one reference; on wslc and Docker they are two, and an update that did not bridge them recreated the container from
/// the OLD image, recorded the new version, and the studio's integrity check stopped it.
/// </summary>
public static class LoadedImageName
{
    /// <summary>
    /// Points <paramref name="target"/> at the loaded image when it does not already name it, and returns the loaded
    /// image's id (null when it cannot be known, which callers treat as "not confirmed"). The source is what the
    /// engine SAID it loaded: the name that is the same reference as the target once a registry prefix is dropped,
    /// else the only name it printed. Only an engine that printed nothing falls back to the two prefixes a tarball is
    /// known to carry, and then only to a guess that resolves. Nothing is tagged when the source and the target already
    /// resolve to the same id, which is podman's case and keeps it untouched.
    /// </summary>
    public static string? Answer(IContainerRuntime runtime, IReadOnlyList<string> loaded, string target, Action<string> log) =>
        Find(runtime, loaded, target) is { } image ? Point(runtime, image, target, log) : null;

    /// <summary>
    /// Which name the load gave the image and its id, without changing anything (backlog 256): the update reads the
    /// loaded image's labels from here and refuses BEFORE <see cref="Point"/> moves the install's name. Null when the
    /// load named nothing that can be attributed to <paramref name="target"/> or the name resolves to no image.
    /// </summary>
    public static LoadedImage? Find(IContainerRuntime runtime, IReadOnlyList<string> loaded, string target)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loaded);

        var source = Source(runtime, loaded, target);
        if (source is null)
        {
            return null;
        }

        var loadedId = runtime.ResolveImageDigest(source);
        return string.IsNullOrWhiteSpace(loadedId) ? null : new LoadedImage(source, loadedId);
    }

    /// <summary>
    /// Points <paramref name="target"/> at <paramref name="image"/> when it does not already name it, and returns the
    /// image's id. Nothing is tagged when the target already resolves to it, which is podman's case.
    /// </summary>
    public static string Point(IContainerRuntime runtime, LoadedImage image, string target, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(log);

        var targetId = runtime.ResolveImageDigest(target);
        if (targetId is not null && SameId(targetId, image.Id))
        {
            return image.Id;
        }

        if (runtime.TagImage(image.Source, target))
        {
            log($"The loaded image is named {image.Source}; also named it {target}, the name the studio is created from.");
        }
        else
        {
            log($"The loaded image is named {image.Source}, and {runtime.Name} would not also name it {target}.");
        }

        return image.Id;
    }

    /// <summary>Engines report ids with and without the <c>sha256:</c> prefix; compare the bare hex.</summary>
    public static bool SameId(string a, string b) =>
        string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reference without a registry: <c>localhost/fravashi:latest</c> and <c>docker.io/library/fravashi:latest</c>
    /// are both <c>fravashi:latest</c>. A first segment is a registry when it is <c>localhost</c> or carries a dot or
    /// a port, which is the rule the engines themselves apply.
    /// </summary>
    public static string Unqualified(string reference)
    {
        var name = reference.Trim();
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var first = name[..slash];
            if (first == "localhost" || first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal))
            {
                name = name[(slash + 1)..];
            }
        }

        return name.StartsWith("library/", StringComparison.Ordinal) ? name["library/".Length..] : name;
    }

    private static string? Source(IContainerRuntime runtime, IReadOnlyList<string> loaded, string target)
    {
        var bare = Unqualified(target);
        var same = loaded.FirstOrDefault(n => string.Equals(n, target, StringComparison.Ordinal))
            ?? loaded.FirstOrDefault(n => string.Equals(Unqualified(n), bare, StringComparison.Ordinal));
        if (same is not null)
        {
            return same;
        }

        if (loaded.Count == 1)
        {
            return loaded[0];
        }

        if (loaded.Count > 1)
        {
            // Several names and none of them the target: picking one would be a guess about which image is ours.
            return null;
        }

        return new[] { $"localhost/{bare}", $"docker.io/library/{bare}" }
            .FirstOrDefault(guess => runtime.ResolveImageDigest(guess) is not null);
    }

    private static string Bare(string id) =>
        id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? id["sha256:".Length..] : id;
}
