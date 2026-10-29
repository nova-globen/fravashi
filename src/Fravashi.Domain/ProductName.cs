namespace Fravashi.Domain;

/// <summary>
/// The product's name as a person reads it: in the studio, the window, the tray, the launcher's output and the web
/// setup. Every user-visible sentence names the product through this and nowhere else (item #373, the host's
/// decision 3), so the rename to Nova Globen Fravashi (<c>docs/plans/rename-to-fravashi.md</c>, approved by the owner
/// 2026-10-26) is this file, and any later rename is too.
/// </summary>
/// <remarks>
/// It lives in Domain, the one project with no dependencies, so everything that shows a name can read it: the studio
/// through Application, and the launcher library and the web setup core as LINKED SOURCE, because those two take no
/// project references by design (the same arrangement as <c>NetworkFailure</c> in the launcher). The cost is the one
/// that arrangement already has: Launcher.Tests sees two copies (the launcher's and Domain's through Infrastructure),
/// so a test there names the words, never this type.
///
/// What is NOT here, on purpose: internal names an old <c>fravashi.exe</c> and a new image share after <c>fravashi update</c>
/// (the <c>FRAVASHI_*</c> variables, the container, image and volume names, the tray mutexes, the update URL, the
/// signing key, the IdP client). Renaming those strands installed clients; they are the rename plan's later step.
/// </remarks>
public static class ProductName
{
    /// <summary>The short name, as in "Board and wiki here in Fravashi" and "&lt;workspace&gt; - Fravashi".</summary>
    public const string Short = "Fravashi";

    /// <summary>The full name, for About, the installer and the status headers.</summary>
    public const string Full = "Nova Globen Fravashi";

    /// <summary>The company tagline, wherever one is shown.</summary>
    public const string Tagline = "Nova Globen: did a bit.";

    /// <summary>The launcher's command, as a person types it (<c>fravashi status</c>).</summary>
    public const string Command = "fravashi";

    /// <summary>The launcher's executable file name.</summary>
    public const string Executable = "fravashi.exe";

    /// <summary>The name the product had before the rename, for the one line that says "formerly ..." (read from
    /// <see cref="FormerNames"/>, the one file that holds the old names until the cleanup release).</summary>
    public const string Former = FormerNames.Product;
}
