using System.Runtime.InteropServices;
using System.Text;
using Fravashi.Domain;

namespace Fravashi.WebSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args, out var error);
        var quiet = options?.Quiet ?? SetupOptions.AsksForQuiet(args);

        if (quiet)
        {
            NativeConsole.AttachToParent();
            if (options is null)
            {
                Console.Error.WriteLine(error);
                return (int)SetupExitCode.BadArguments;
            }

            if (options.ShowHelp)
            {
                // Never 0, which means installed: nothing was started, the same as a bad argument.
                Console.WriteLine(SetupOptions.Usage);
                return (int)SetupExitCode.BadArguments;
            }

            return (int)RunQuiet(options);
        }

        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        if (options is null || options.ShowHelp)
        {
            MessageBox.Show(options is null ? error : SetupOptions.Usage, $"{ProductName.Full} setup", MessageBoxButtons.OK,
                options is null ? MessageBoxIcon.Error : MessageBoxIcon.Information);
            return (int)SetupExitCode.BadArguments;
        }

        using var wizard = new WizardForm(options, Host());
        System.Windows.Forms.Application.Run(wizard);
        return (int)wizard.ExitCode;
    }

    /// <summary>
    /// /quiet: the same install with no window. Every engine line goes to the log and to the parent console
    /// when there is one; the verdict is the exit code (<see cref="SetupExitCode"/>).
    /// </summary>
    private static SetupExitCode RunQuiet(SetupOptions options)
    {
        var host = Host();
        var logPath = options.LogPath ?? SetupLog.DefaultPath(host.TempRoot, DateTimeOffset.UtcNow);

        // Every failure here ends in a contract exit code, never an unhandled exception: a GUI-subsystem
        // process that crashes can raise a dialog nobody at an unattended machine's desktop will dismiss.
        SetupLog log;
        try
        {
            log = SetupLog.Open(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"{ProductName.Full} setup could not open its log '{logPath}': {ex.Message}");
            return SetupExitCode.EngineDidNotRun;
        }

        using var owned = log;
        Console.WriteLine($"{ProductName.Full} setup: installing {options.Describe()}; log: {log.Path}");

        var progress = new EngineProgress();
        SetupVerdict verdict;
        try
        {
            verdict = SetupRun.RunAsync(options, host, log, progress, Console.WriteLine, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            verdict = new SetupVerdict(SetupExitCode.EngineDidNotRun, "Setup could not run the installer.", ex.Message);
            log.Write($"Verdict: {verdict.Headline} {verdict.Detail} (exit code {(int)verdict.Code})");
        }

        Console.WriteLine();
        Console.WriteLine(verdict.Headline);
        if (verdict.Detail is not null)
        {
            Console.WriteLine(verdict.Detail);
        }

        Console.WriteLine($"Exit code {(int)verdict.Code}; log: {log.Path}");
        return verdict.Code;
    }

    internal static SetupHost Host() =>
        new(EngineCommand.WindowsPowerShell(Environment.SystemDirectory), Path.GetTempPath(), ChildConsoleEncoding());

    /// <summary>
    /// How Windows PowerShell 5.1 is EXPECTED to encode what it prints. It is started with no window, so it should
    /// get a console of its own whose code page is the system's OEM one: 437 or 850 on most machines, and 65001
    /// where "Use Unicode UTF-8 for worldwide language support" is on, which is the machine that broke 1.0.0. This
    /// is unmeasured until the VM install through the EXE. A wrong guess garbles only non-ASCII text, and every
    /// marker the progress and the verdict read is ASCII.
    /// </summary>
    private static Encoding ChildConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
