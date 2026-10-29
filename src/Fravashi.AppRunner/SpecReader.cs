using System.Text.Json;
using System.Text.RegularExpressions;
using Fravashi.Domain;

namespace Fravashi.AppRunner;

/// <summary>
/// Reads a project's container spec into the app runner's narrow <see cref="RunSpec"/> (backlog 87, ADR 0027).
/// <para>
/// A spec is a PROGRAM written by whoever owns the repository the person opened, so nothing in it is passed through
/// to an engine. Each key is one of three things, decided here by name: CARRIED into the model after its value is
/// checked; NOT READ because it only configures editor tooling (and then named in <see cref="RunSpec.NotRead"/> so
/// that is visible); or REFUSED, by name, with the reason. A key this reader has never heard of is refused too:
/// the formats grow, and a reader that ignores what it does not recognise is one release away from ignoring the
/// next dangerous thing.
/// </para>
/// <para>
/// Two formats: a <c>devcontainer.json</c> (JSON with comments), and a bare Dockerfile or Containerfile. Compose files
/// are refused as not supported yet rather than half-read. What Fravashi needs that a devcontainer file has no
/// field for (the command that starts the app, its port, lowered ceilings) lives under
/// <c>customizations.fravashi</c>, which is the place the devcontainer format reserves for a tool's own settings.
/// </para>
/// </summary>
public static partial class SpecReader
{
    /// <summary>The spec files looked for, in order, when the caller names none.</summary>
    public static readonly IReadOnlyList<string> Conventional =
        [".devcontainer/devcontainer.json", ".devcontainer.json", "Dockerfile", "Containerfile"];

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 32,
    };

    private const int MaxSpecBytes = 256 * 1024;

    /// <summary>Keys that only configure editor tooling. Not applied, and named as such.</summary>
    private static readonly HashSet<string> ToolingOnly = new(StringComparer.Ordinal)
    {
        "name", "$schema", "portsAttributes", "otherPortsAttributes", "hostRequirements", "shutdownAction",
        "overrideCommand", "userEnvProbe", "updateRemoteUserUID", "waitFor", "remoteUser",
    };

    /// <summary>Keys refused by name, with the reason the person is told.</summary>
    private static readonly Dictionary<string, string> RefusedKeys = new(StringComparer.Ordinal)
    {
        ["privileged"] = "a privileged container can reach the host; runs never get one",
        ["capAdd"] = "added capabilities widen what a run can do to the machine; the app runner keeps a small fixed set and a spec adds none",
        ["securityOpt"] = "security options are the app runner's to set, never a spec's",
        ["runArgs"] = "engine arguments are built from a fixed template; none is taken from a spec",
        ["mounts"] = "a run mounts nothing; the source arrives in the image through the build",
        ["workspaceMount"] = "a run mounts nothing; the source arrives in the image through the build",
        ["appPort"] = "a spec never chooses a published port; the app runner publishes the one entry port itself",
        ["init"] = "the run template is fixed; an init process is not something a spec may add",
        ["initializeCommand"] = "it runs on the HOST before the container exists, which a verifier run never does",
        ["features"] = "features are installed from the internet by tooling the app runner does not run; bake them into the Dockerfile",
        ["dockerComposeFile"] = "compose-based specs are not supported yet",
        ["service"] = "compose-based specs are not supported yet",
        ["runServices"] = "compose-based specs are not supported yet",
        ["onCreateCommand"] = "lifecycle commands run at run time, where there is no internet; do the work in the Dockerfile, or name the start command under customizations.fravashi.command",
        ["updateContentCommand"] = "lifecycle commands run at run time, where there is no internet; do the work in the Dockerfile",
        ["postCreateCommand"] = "lifecycle commands run at run time, where there is no internet; do the work in the Dockerfile, or name the start command under customizations.fravashi.command",
        ["postStartCommand"] = "lifecycle commands run at run time, where there is no internet; name the start command under customizations.fravashi.command",
        ["postAttachCommand"] = "lifecycle commands belong to an attached editor, which a verifier run does not have",
        ["remoteEnv"] = "it applies to editor processes, not the application; use containerEnv",
    };

    /// <summary>Reads the spec at <paramref name="specPath"/>, or the first conventional one when it is null.</summary>
    public static SpecReadResult Read(string workspaceRoot, string? specPath, Ceilings ceilings)
    {
        var chosen = specPath;
        if (string.IsNullOrWhiteSpace(chosen))
        {
            chosen = Conventional.FirstOrDefault(candidate => WorkspacePath.Resolve(workspaceRoot, candidate) is { } full && File.Exists(full));
            if (chosen is null)
            {
                return SpecReadResult.Refused(new SpecRefusal(
                    "spec", $"the workspace has none of {string.Join(", ", Conventional)}, and no spec was named"));
            }
        }

        var normalized = chosen.Replace('\\', '/').Trim();
        var full = WorkspacePath.Resolve(workspaceRoot, normalized);
        if (full is null)
        {
            return SpecReadResult.Refused(new SpecRefusal("spec", "the path leaves the workspace or passes through a link"));
        }

        if (!File.Exists(full))
        {
            return SpecReadResult.Refused(new SpecRefusal("spec", "there is no such file in the workspace"));
        }

        var fileName = Path.GetFileName(full);
        if (fileName.EndsWith("devcontainer.json", StringComparison.OrdinalIgnoreCase))
        {
            return ReadDevcontainer(workspaceRoot, normalized, full, ceilings);
        }

        if (fileName.Contains("compose", StringComparison.OrdinalIgnoreCase)
            && (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return SpecReadResult.Refused(new SpecRefusal("spec", "compose-based specs are not supported yet"));
        }

        if (IsDockerfileName(fileName))
        {
            return ReadDockerfile(workspaceRoot, normalized, full, ceilings);
        }

        return SpecReadResult.Refused(new SpecRefusal("spec", "only a devcontainer.json or a Dockerfile/Containerfile can be read"));
    }

    private static bool IsDockerfileName(string fileName) =>
        fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".Containerfile", StringComparison.OrdinalIgnoreCase);

    // ---- a Dockerfile on its own ---------------------------------------------------------------------------------

    private static SpecReadResult ReadDockerfile(string root, string relative, string full, Ceilings ceilings)
    {
        if (new FileInfo(full).Length > MaxSpecBytes)
        {
            return SpecReadResult.Refused(new SpecRefusal("spec", $"a spec larger than {MaxSpecBytes / 1024} KiB is not read"));
        }

        var lines = File.ReadAllLines(full);
        if (DeclaresVerificationField(relative, lines) is { Count: > 0 } declared)
        {
            return new SpecReadResult(null, declared);
        }

        // The Dockerfile's own folder is its context, which is what `docker build` does when pointed at it there.
        var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
        return new SpecReadResult(
            new RunSpec(
                SpecPath: relative,
                BaseImage: null,
                Dockerfile: relative,
                Context: directory,
                Target: null,
                BuildArgs: new Dictionary<string, string>(),
                Command: null,
                EntryPort: FirstExposedPort(lines),
                User: null,
                Environment: new Dictionary<string, string>(),
                WorkspaceFolder: DefaultWorkspaceFolder(root),
                Ceilings: ceilings,
                NotRead: []),
            []);
    }

    /// <summary>The first port an <c>EXPOSE</c> line names, when it is a plain number.</summary>
    internal static int? FirstExposedPort(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("EXPOSE ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in trimmed[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var number = token.Split('/')[0];
                if (int.TryParse(number, out var port) && port is > 0 and < 65536)
                {
                    return port;
                }
            }
        }

        return null;
    }

    // ---- a devcontainer.json ---------------------------------------------------------------------------------------

    private static SpecReadResult ReadDevcontainer(string root, string relative, string full, Ceilings ceilings)
    {
        if (new FileInfo(full).Length > MaxSpecBytes)
        {
            return SpecReadResult.Refused(new SpecRefusal("spec", $"a spec larger than {MaxSpecBytes / 1024} KiB is not read"));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(full), JsonOptions);
        }
        catch (JsonException)
        {
            return SpecReadResult.Refused(new SpecRefusal("spec", "the file is not valid JSON (comments and trailing commas are allowed)"));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SpecReadResult.Refused(new SpecRefusal("spec", "the file is not a JSON object"));
            }

            return Carry(root, relative, document.RootElement, ceilings);
        }
    }

    private static SpecReadResult Carry(string root, string relative, JsonElement spec, Ceilings ceilings)
    {
        var refusals = new List<SpecRefusal>();
        var notRead = new List<string>();
        var specFolder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;

        string? image = null;
        string? dockerfile = null;
        var context = specFolder;
        string? target = null;
        var buildArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        string? user = null;
        int? port = null;
        IReadOnlyList<string>? command = null;
        var workspaceFolder = DefaultWorkspaceFolder(root);
        var lowered = ceilings;

        foreach (var property in spec.EnumerateObject())
        {
            var key = property.Name;
            var value = property.Value;
            if (RefusedKeys.TryGetValue(key, out var why))
            {
                refusals.Add(new SpecRefusal(key, why));
                continue;
            }

            if (ToolingOnly.Contains(key))
            {
                notRead.Add(key);
                continue;
            }

            switch (key)
            {
                case "image":
                    image = value.ValueKind == JsonValueKind.String && ImageReference().IsMatch(value.GetString()!)
                        ? value.GetString()
                        : Refuse(refusals, key, "must be a plain image reference");
                    break;

                case "build":
                    ReadBuild(root, specFolder, value, refusals, ref dockerfile, ref context, ref target, buildArgs);
                    break;

                case "forwardPorts":
                    // Ports an editor would forward; the first number is the application's port unless Fravashi's own
                    // customization names one. A "host:port" string names another container, which a run does not have.
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in value.EnumerateArray())
                        {
                            if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out var p) && p is > 0 and < 65536)
                            {
                                port ??= p;
                                break;
                            }
                        }
                    }
                    else
                    {
                        Refuse(refusals, key, "must be a list of ports");
                    }

                    break;

                case "containerEnv":
                    ReadStringMap(value, key, EnvironmentName(), refusals, environment);
                    break;

                case "containerUser":
                    user = value.ValueKind == JsonValueKind.String && UserName().IsMatch(value.GetString()!)
                        ? value.GetString()
                        : Refuse(refusals, key, "must be a user name or a numeric uid[:gid]");
                    break;

                case "workspaceFolder":
                    workspaceFolder = value.ValueKind == JsonValueKind.String && ImageFolder().IsMatch(value.GetString()!)
                        && !value.GetString()!.Split('/').Contains("..") && value.GetString() != "/"
                        ? value.GetString()!.TrimEnd('/')
                        : Refuse(refusals, key, "must be an absolute path inside the image, without '..'") ?? workspaceFolder;
                    break;

                case "customizations":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        Refuse(refusals, key, "must be an object");
                        break;
                    }

                    // The former key first, so the new one wins where a spec carries both: a project's devcontainer.json
                    // written before the internal rename keeps working (the bridge release; dropped in the cleanup release).
                    foreach (var tool in value.EnumerateObject().OrderBy(t => t.Name == "fravashi" ? 1 : 0))
                    {
                        if (tool.Name == "fravashi" || tool.Name == FormerNames.DevcontainerCustomization)
                        {
                            ReadFravashi(tool.Value, refusals, ref command, ref port, ref lowered);
                        }
                        else
                        {
                            notRead.Add("customizations." + tool.Name);
                        }
                    }

                    break;

                default:
                    refusals.Add(new SpecRefusal(key, "not a key this app runner knows; it is refused rather than ignored"));
                    break;
            }
        }

        // A Dockerfile refused for what it declares (backlog 226, "<file>: <name>") is a build that was named.
        if (image is null && dockerfile is null
            && !refusals.Any(r => r.Field is "image" or "build" or "build.dockerfile" || r.Field.Contains(": ", StringComparison.Ordinal)))
        {
            refusals.Add(new SpecRefusal("image", "the spec names neither an image nor a build"));
        }

        if (image is not null && dockerfile is not null)
        {
            refusals.Add(new SpecRefusal("image", "the spec names both an image and a build; it must name one"));
        }

        if (refusals.Count > 0)
        {
            return new SpecReadResult(null, refusals);
        }

        return new SpecReadResult(
            new RunSpec(relative, image, dockerfile, context, target, buildArgs, command, port, user, environment,
                workspaceFolder, lowered, notRead),
            []);
    }

    private static void ReadBuild(
        string root, string specFolder, JsonElement build, List<SpecRefusal> refusals,
        ref string? dockerfile, ref string context, ref string? target, Dictionary<string, string> buildArgs)
    {
        if (build.ValueKind != JsonValueKind.Object)
        {
            refusals.Add(new SpecRefusal("build", "must be an object"));
            return;
        }

        string? dockerfileName = null;
        string? contextName = null;
        foreach (var property in build.EnumerateObject())
        {
            var field = "build." + property.Name;
            switch (property.Name)
            {
                case "dockerfile":
                    dockerfileName = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : Refuse(refusals, field, "must be a path");
                    break;
                case "context":
                    contextName = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : Refuse(refusals, field, "must be a path");
                    break;
                case "target":
                    target = property.Value.ValueKind == JsonValueKind.String && StageName().IsMatch(property.Value.GetString()!)
                        ? property.Value.GetString()
                        : Refuse(refusals, field, "must be a stage name");
                    break;
                case "args":
                    ReadStringMap(property.Value, field, EnvironmentName(), refusals, buildArgs);
                    break;
                case "options":
                    refusals.Add(new SpecRefusal(field, "engine build options are the app runner's to set, never a spec's"));
                    break;
                case "cacheFrom":
                    refusals.Add(new SpecRefusal(field, "a build only uses images it builds or names as its base"));
                    break;
                default:
                    refusals.Add(new SpecRefusal(field, "not a key this app runner knows; it is refused rather than ignored"));
                    break;
            }
        }

        if (dockerfileName is null)
        {
            if (!refusals.Any(r => r.Field.StartsWith("build.", StringComparison.Ordinal)))
            {
                refusals.Add(new SpecRefusal("build.dockerfile", "a build must name its Dockerfile"));
            }

            return;
        }

        // Both paths are relative to the spec's own folder, as the devcontainer format defines them, and both must
        // stay inside the workspace without passing through a link.
        var dockerfileRelative = Combine(specFolder, dockerfileName);
        var contextRelative = Combine(specFolder, contextName ?? ".");
        if (dockerfileRelative is null || WorkspacePath.Resolve(root, dockerfileRelative) is not { } dockerfileFull || !File.Exists(dockerfileFull))
        {
            refusals.Add(new SpecRefusal("build.dockerfile", "must name a file inside the workspace, not through a link"));
            return;
        }

        if (contextRelative is null || WorkspacePath.Resolve(root, contextRelative) is not { } contextFull || !Directory.Exists(contextFull))
        {
            refusals.Add(new SpecRefusal("build.context", "must name a folder inside the workspace, not through a link"));
            return;
        }

        if (new FileInfo(dockerfileFull).Length <= MaxSpecBytes
            && DeclaresVerificationField(dockerfileRelative, File.ReadAllLines(dockerfileFull)) is { Count: > 0 } declared)
        {
            refusals.AddRange(declared);
            return;
        }

        dockerfile = dockerfileRelative;
        context = contextRelative;
    }

    /// <summary>
    /// Backlog 226: a Dockerfile the spec names may not name the verification-run field ANYWHERE outside a comment, so it
    /// cannot be baked into the image a run starts from: not in an <c>ENV</c> or <c>ARG</c>, not on a continuation line of
    /// one, and not in a <c>CMD</c> or <c>ENTRYPOINT</c> that sets it with <c>env</c>. The text is read twice, as written and
    /// with continuation lines joined the way the builder joins them (its escape character, or the one an
    /// <c>escape</c> parser directive names), so a name split across a line break is found too. Refused by name, once
    /// per name.
    /// </summary>
    internal static IReadOnlyList<SpecRefusal> DeclaresVerificationField(string relative, IEnumerable<string> lines)
    {
        var physical = lines.ToList();
        var escape = EscapeCharacter(physical);
        var text = physical.Where(l => !IsComment(l)).Concat(LogicalLines(physical, escape)).ToList();
        var refusals = new List<SpecRefusal>();
        foreach (var name in VerificationRunField.Names)
        {
            var whole = new Regex($@"(?<![A-Za-z0-9_]){name}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
            if (text.Any(whole.IsMatch))
            {
                refusals.Add(new SpecRefusal($"{relative}: {name}", VerificationRunField.Refusal));
            }
        }

        return refusals;
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    /// <summary>The escape character: a leading <c># escape=`</c> parser directive names it, otherwise a backslash.</summary>
    private static char EscapeCharacter(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('#'))
            {
                break; // Parser directives only come before the first instruction.
            }

            var directive = trimmed.TrimStart('#').Replace(" ", string.Empty, StringComparison.Ordinal);
            if (directive.StartsWith("escape=", StringComparison.OrdinalIgnoreCase) && directive.Length > 7)
            {
                return directive[7];
            }
        }

        return '\\';
    }

    /// <summary>
    /// The instructions as the builder reads them: a line ending in the escape character continues on the next, and a
    /// comment line inside a continuation is dropped, as the builder drops it.
    /// </summary>
    private static IEnumerable<string> LogicalLines(IEnumerable<string> lines, char escape)
    {
        var current = new System.Text.StringBuilder();
        var continuing = false;
        foreach (var line in lines)
        {
            if (IsComment(line) && (continuing || current.Length == 0))
            {
                continue;
            }

            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith(escape))
            {
                current.Append(trimmed[..^1]);
                continuing = true;
                continue;
            }

            current.Append(line);
            yield return current.ToString();
            current.Clear();
            continuing = false;
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void ReadFravashi(
        JsonElement fravashi, List<SpecRefusal> refusals, ref IReadOnlyList<string>? command, ref int? port, ref Ceilings lowered)
    {
        if (fravashi.ValueKind != JsonValueKind.Object)
        {
            refusals.Add(new SpecRefusal("customizations.fravashi", "must be an object"));
            return;
        }

        foreach (var property in fravashi.EnumerateObject())
        {
            var field = "customizations.fravashi." + property.Name;
            var value = property.Value;
            switch (property.Name)
            {
                case "command":
                    if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } line)
                    {
                        command = ["/bin/sh", "-c", line];
                    }
                    else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                             && value.GetArrayLength() > 0)
                    {
                        command = [.. value.EnumerateArray().Select(e => e.GetString()!)];
                    }
                    else
                    {
                        refusals.Add(new SpecRefusal(field, "must be a command line or a list of arguments"));
                    }

                    break;

                case "port":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var p) && p is > 0 and < 65536)
                    {
                        port = p;
                    }
                    else
                    {
                        refusals.Add(new SpecRefusal(field, "must be a port number"));
                    }

                    break;

                case "limits":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        refusals.Add(new SpecRefusal(field, "must be an object"));
                        break;
                    }

                    double? cpus = null;
                    int? memory = null, processes = null, writable = null, minutes = null;
                    foreach (var limit in value.EnumerateObject())
                    {
                        if (limit.Value.ValueKind != JsonValueKind.Number)
                        {
                            refusals.Add(new SpecRefusal(field + "." + limit.Name, "must be a number"));
                            continue;
                        }

                        switch (limit.Name)
                        {
                            case "cpus": cpus = limit.Value.GetDouble(); break;
                            case "memoryMb": memory = (int)Math.Min(int.MaxValue, limit.Value.GetDouble()); break;
                            case "processes": processes = (int)Math.Min(int.MaxValue, limit.Value.GetDouble()); break;
                            case "writableMb": writable = (int)Math.Min(int.MaxValue, limit.Value.GetDouble()); break;
                            case "runMinutes": minutes = (int)Math.Min(int.MaxValue, limit.Value.GetDouble()); break;
                            default:
                                refusals.Add(new SpecRefusal(field + "." + limit.Name, "not a ceiling this app runner knows"));
                                break;
                        }
                    }

                    var (result, refusal) = lowered.Lower(cpus, memory, processes, writable, minutes);
                    if (refusal is not null)
                    {
                        refusals.Add(new SpecRefusal(field, refusal));
                    }
                    else
                    {
                        lowered = result!;
                    }

                    break;

                default:
                    refusals.Add(new SpecRefusal(field, "not a key this app runner knows; it is refused rather than ignored"));
                    break;
            }
        }
    }

    private static void ReadStringMap(
        JsonElement value, string field, Regex keyShape, List<SpecRefusal> refusals, Dictionary<string, string> into)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            refusals.Add(new SpecRefusal(field, "must be an object of plain strings"));
            return;
        }

        foreach (var entry in value.EnumerateObject())
        {
            if (VerificationRunField.Names.Contains(entry.Name, StringComparer.Ordinal))
            {
                // Backlog 226: refused by name, never carried, whatever its value.
                refusals.Add(new SpecRefusal($"{field}.{entry.Name}", VerificationRunField.Refusal));
            }
            else if (!keyShape.IsMatch(entry.Name))
            {
                refusals.Add(new SpecRefusal($"{field}.{entry.Name}", "is not a valid name"));
            }
            else if (entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString()!.Length > 4096)
            {
                refusals.Add(new SpecRefusal($"{field}.{entry.Name}", "must be a plain string"));
            }
            else
            {
                into[entry.Name] = entry.Value.GetString()!;
            }
        }
    }

    /// <summary>A folder-relative path joined onto the spec's folder, POSIX-style; null when it climbs or is rooted.</summary>
    private static string? Combine(string folder, string? path)
    {
        if (path is null)
        {
            return null;
        }

        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        if (path.StartsWith('/') || Path.IsPathRooted(path))
        {
            return null;
        }

        return string.Join('/', parts);
    }

    private static string? Refuse(List<SpecRefusal> refusals, string field, string reason)
    {
        refusals.Add(new SpecRefusal(field, reason));
        return null;
    }

    /// <summary>Where the source goes in the image when the spec does not say: <c>/workspaces/&lt;folder name&gt;</c>.</summary>
    internal static string DefaultWorkspaceFolder(string root)
    {
        var name = new string(Path.GetFileName(Path.TrimEndingDirectorySeparator(root))
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('.');
        return "/workspaces/" + (name.Length == 0 ? "app" : name);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._\-/:@]{0,254}\z")]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex EnvironmentName();

    [GeneratedRegex(@"^([a-z_][a-z0-9_\-]{0,31}|[0-9]{1,10}(:[0-9]{1,10})?)\z")]
    private static partial Regex UserName();

    [GeneratedRegex(@"^/[A-Za-z0-9._\-/]{0,254}\z")]
    private static partial Regex ImageFolder();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,127}\z")]
    private static partial Regex StageName();
}
