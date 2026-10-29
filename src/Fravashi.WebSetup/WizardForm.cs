using System.Drawing;
using System.Reflection;
using Product = Fravashi.Domain.ProductName;

namespace Fravashi.WebSetup;

/// <summary>
/// The wizard: a welcome, the version page, progress, and the verdict. It decides nothing itself:
/// <see cref="SetupRun"/> runs the install and <see cref="SetupVerdict"/> says what it came to, exactly as in
/// /quiet.
/// </summary>
internal sealed class WizardForm : Form
{
    private readonly SetupOptions _options;
    private readonly SetupHost _host;

    private readonly Panel _welcome = Page();
    private readonly Panel _settings = Page();
    private readonly Panel _running = Page();
    private readonly Panel _done = Page();

    private readonly Label _releaseLabel = MakeLabel("Version (empty installs the newest, or for example 1.0.0):", bold: false);
    private readonly TextBox _release = new() { Width = 200 };
    private readonly Label _sourceAddress = MakeLabel(
        "From " + SetupOptions.DownloadsBase + "/, public, so nothing to sign in to. Each release's manifest is " +
        $"verified against {Product.Short}'s release key before anything it names is downloaded.", bold: false);
    private readonly Label _settingsProblem = MakeLabel("", bold: false);

    private readonly Label _stage = MakeLabel("Starting ...", bold: true);
    private readonly Label _item = MakeLabel("", bold: false);
    private readonly ProgressBar _bar = new() { Width = 520, Height = 18, Style = ProgressBarStyle.Marquee };
    private readonly TextBox _output = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        Width = 520,
        Height = 190,
        Font = new Font(FontFamily.GenericMonospace, 8.25f),
    };

    private readonly Label _headline = MakeLabel("", bold: true);
    private readonly Label _detail = MakeLabel("", bold: false);
    private readonly Label _logLine = MakeLabel("", bold: false);

    private readonly Button _back = new() { Text = "< Back", Width = 90 };
    private readonly Button _next = new() { Text = "Next >", Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90 };

    private CancellationTokenSource? _run;
    private bool _closeWhenStopped;
    private SetupVerdict? _verdict;

    public WizardForm(SetupOptions options, SetupHost host)
    {
        _options = options;
        _host = host;

        Text = $"{Product.Full} setup";
        Icon = LoadIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 400);
        Font = SystemFonts.MessageBoxFont ?? Font;

        BuildWelcome();
        BuildSettings();
        BuildRunning();
        BuildDone();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        buttons.Controls.AddRange([_cancel, _next, _back]);
        Controls.Add(buttons);
        foreach (var page in new[] { _welcome, _settings, _running, _done })
        {
            Controls.Add(page);
        }

        _back.Click += (_, _) => ShowPage(_settings == Current ? _welcome : _settings);
        _next.Click += (_, _) => Next();
        _cancel.Click += (_, _) => Close();
        FormClosing += OnClosing;

        ShowPage(_welcome);
    }

    public SetupExitCode ExitCode { get; private set; } = SetupExitCode.Cancelled;

    private Panel? Current { get; set; }

    private void BuildWelcome()
    {
        Add(_welcome,
            MakeLabel($"{Product.Full} setup", bold: true),
            MakeLabel($"This downloads the newest {Product.Short} release for this machine from the {Product.Short} update channel, " +
                 "checks the release's signed manifest and SHA-256 sums, and installs it: the studio " +
                 $"image into your container runtime (docker, podman or wslc) and the {Product.Short} launcher with its shortcuts.", bold: false),
            MakeLabel("A container runtime with a ready machine must already be installed; setup says so if one is missing. " +
                 "Nothing is installed until every download has been verified.", bold: false));
    }

    private void BuildSettings()
    {
        Add(_settings,
            MakeLabel("Which release to install", bold: true),
            _sourceAddress,
            _releaseLabel,
            _release,
            _settingsProblem);
        _settingsProblem.ForeColor = Color.Firebrick;
        _release.Text = _options.Version ?? "";
    }

    private void BuildRunning()
    {
        Add(_running, _stage, _item, _bar, _output);
    }

    private void BuildDone()
    {
        Add(_done, _headline, _detail, _logLine);
    }

    private void Next()
    {
        if (Current == _welcome)
        {
            ShowPage(_settings);
            return;
        }

        if (Current == _settings)
        {
            var chosen = _options.Choose(_release.Text, out var problem);
            _settingsProblem.Text = problem ?? "";
            if (problem is null && chosen is not null)
            {
                _ = InstallAsync(chosen);
            }

            return;
        }

        if (Current == _done)
        {
            Close();
        }
    }

    private async Task InstallAsync(SetupOptions options)
    {
        _output.Clear();
        _stage.Text = "Starting ...";
        _item.Text = "";
        _bar.Style = ProgressBarStyle.Marquee;
        ShowPage(_running);

        var progress = new EngineProgress();

        // curl redraws its bar many times a second; one pending repaint at a time is plenty.
        var repaintPending = 0;
        progress.Changed += () =>
        {
            if (Interlocked.Exchange(ref repaintPending, 1) == 0)
            {
                BeginInvoke(new Action(() =>
                {
                    Volatile.Write(ref repaintPending, 0);
                    ShowProgress(progress);
                }));
            }
        };

        var logPath = SetupLog.DefaultPath(_host.TempRoot, DateTimeOffset.UtcNow);
        SetupLog? log = null;
        _run = new CancellationTokenSource();
        var cancellation = _run.Token;
        try
        {
            log = SetupLog.Open(logPath);
            var openLog = log;
            _verdict = await Task.Run(() => SetupRun.RunAsync(
                options, _host, openLog, progress,
                line => BeginInvoke(new Action(() => _output.AppendText(line + Environment.NewLine))),
                cancellation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _verdict = new SetupVerdict(SetupExitCode.EngineDidNotRun, "Setup could not run the installer.", ex.Message);
            log?.Write($"Verdict: {_verdict.Headline} {_verdict.Detail} (exit code {(int)_verdict.Code})");
        }
        finally
        {
            log?.Dispose();
            _run.Dispose();
            _run = null;
        }

        ExitCode = _verdict.Code;
        if (_closeWhenStopped)
        {
            Close();
            return;
        }

        _headline.Text = _verdict.Headline;
        _headline.ForeColor = _verdict.Code == SetupExitCode.Installed ? Color.DarkGreen : Color.Firebrick;
        _detail.Text = _verdict.Detail ?? "";
        _logLine.Text = log is null ? "No log could be written to " + logPath : "The whole output is in " + logPath;
        if (_verdict.Code == SetupExitCode.NotPublished)
        {
            // Back goes to the settings page, where the version is one edit away.
            _settingsProblem.Text = "The update channel has no release for that version. Check it, or leave it empty for the newest.";
        }

        ShowPage(_done);
    }

    private void ShowProgress(EngineProgress progress)
    {
        _stage.Text = progress.Stage switch
        {
            EngineStage.Starting => "Starting ...",
            EngineStage.LookingUp => "Looking up the release ...",
            EngineStage.Downloading => "Downloading" + (progress.ResolvedTag is { } tag ? " " + tag : "") + " ...",
            EngineStage.Verifying => "Verifying the downloads ...",
            EngineStage.Unpacking => "Unpacking ...",
            EngineStage.Installing => "Installing (the release's own installer is running) ...",
            EngineStage.Installed => "Installed.",
            _ => _stage.Text,
        };
        _item.Text = progress.Item ?? "";
        if (progress.Percent is { } percent)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Clamp(percent, 0, 100);
        }
        else
        {
            _bar.Style = ProgressBarStyle.Marquee;
        }
    }

    private void ShowPage(Panel page)
    {
        foreach (var candidate in new[] { _welcome, _settings, _running, _done })
        {
            candidate.Visible = candidate == page;
        }

        Current = page;
        _back.Enabled = page == _settings || (page == _done && _verdict is { Code: not SetupExitCode.Installed });
        _next.Enabled = page != _running;
        _next.Text = page == _settings ? "Install" : page == _done ? "Finish" : "Next >";
        _cancel.Text = page == _done ? "Close" : "Cancel";
        _cancel.Enabled = true;
        AcceptButton = _next;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_run is null)
        {
            return;
        }

        e.Cancel = true;
        if (_closeWhenStopped)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            "Stop the install? Files already downloaded may be left in a fravashi-webinstall folder under your temp " +
            "folder (%TEMP%); you can delete it by hand.",
            $"{Product.Full} setup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            _closeWhenStopped = true;
            _cancel.Enabled = false;
            _stage.Text = "Stopping ...";
            _run?.Cancel();
        }
    }

    private static Panel Page() => new() { Dock = DockStyle.Fill, Padding = new Padding(16), Visible = false };

    private static Label MakeLabel(string text, bool bold)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        if (bold)
        {
            label.Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold);
        }

        return label;
    }

    private static void Add(Panel page, params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        flow.Controls.AddRange(controls);
        page.Controls.Add(flow);
    }

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Fravashi.WebSetup.icon.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
