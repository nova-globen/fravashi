namespace Fravashi.Launcher;

/// <summary>
/// An engine command line as it may be SHOWN: in an exception, on the splash, in <c>launcher-*.log</c>
/// (backlog 133).
/// <para>
/// The launcher hands the container its secrets as <c>--env NAME=value</c>: the host-bridge token, which reads
/// and writes the user's real folders, the window token and the integrity key. A failed <c>run</c> used to
/// quote its whole command line, so one engine hiccup wrote all three into a plain-text log that lives for
/// weeks. Every environment VALUE is masked here and every NAME is kept. All of them, not a list of the secret
/// ones: nothing about a failure is explained by the value of a variable, and a list of secret names is a list
/// somebody forgets to extend the day a new one is added.
/// </para>
/// </summary>
public static class EngineCommandText
{
    private const string Mask = "***";

    /// <summary>The command, with every environment value masked.</summary>
    public static string ForMessages(string cli, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var shown = new List<string>(args.Count + 1) { cli };
        var nextIsEnvironment = false;
        foreach (var arg in args)
        {
            if (nextIsEnvironment)
            {
                shown.Add(MaskValue(arg));
                nextIsEnvironment = false;
                continue;
            }

            if (arg is "--env" or "-e")
            {
                nextIsEnvironment = true;
                shown.Add(arg);
                continue;
            }

            const string joined = "--env=";
            shown.Add(arg.StartsWith(joined, StringComparison.Ordinal)
                ? joined + MaskValue(arg[joined.Length..])
                : arg);
        }

        return string.Join(' ', shown);
    }

    // NAME=value becomes NAME=***. A bare NAME is a passthrough of the launcher's own environment and carries
    // no value to mask.
    private static string MaskValue(string assignment)
    {
        var equals = assignment.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? assignment : assignment[..(equals + 1)] + Mask;
    }
}
