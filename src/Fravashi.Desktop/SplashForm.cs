using System.Drawing.Drawing2D;
using Fravashi.Launcher;
using Product = Fravashi.Domain.ProductName;

namespace Fravashi.Desktop;

/// <summary>
/// The launch splash: a small borderless card showing the current <see cref="LaunchPhase"/> while
/// the container comes up. Phase updates arrive from the launch thread; <see cref="SetPhase"/>
/// marshals to the UI thread. Colors match the studio's dark chrome (#0B0C0F).
/// <para>
/// Backlog 4, as the owner specified it: the brand icon beside the name, one progress SEGMENT per real launch
/// step (<see cref="LaunchProgress"/>) instead of a marquee that spins the same whether the launch is moving or
/// hung, the step named in words under it, and a fade into the studio window instead of a hard swap.
/// </para>
/// </summary>
internal sealed class SplashForm : Form
{
    /// <summary>Logical (96 dpi) sizes; fravashi.exe is per-monitor v2, so they are scaled to the real DPI.</summary>
    private static readonly Size DesignSize = new(420, 150);
    private const int DesignTitleHeight = 64;
    private const int DesignBarHeight = 18;
    private const int DesignIconSize = 28;

    private static readonly Color Card = Color.FromArgb(11, 12, 15);
    private static readonly Color SegmentPending = Color.FromArgb(38, 42, 51);
    private static readonly Color SegmentDone = Color.FromArgb(99, 140, 255);

    private const int FadeIntervalMs = 15;
    private const double FadeStep = 0.09;

    private readonly Label _phaseLabel;
    private readonly Panel _header;
    private readonly Panel _bar;
    private readonly Icon _brand;
    private readonly Font _titleFont = new("Segoe UI", 15f, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 40 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = FadeIntervalMs };

    private LaunchPhase _phase = LaunchPhase.CheckingRuntime;
    private int _pulseTick;

    public SplashForm()
    {
        // Native chrome (the taskbar entry) is named for the command the owner types, which since the rename is the
        // product name too (backlog 32).
        Text = Product.Short;
        // Borderless, but it IS in the taskbar, and a WinForms form with no Icon shows the framework's
        // default there rather than the executable's, so the first thing a launch puts on screen was
        // unbranded until backlog 112.
        _brand = DesktopIcon.Load();
        Icon = _brand;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = DesignSize;
        BackColor = Card;
        ShowInTaskbar = true;
        DoubleBuffered = true;

        _bar = new BufferedPanel { Dock = DockStyle.Bottom, Height = DesignBarHeight, BackColor = Card };
        _bar.Paint += PaintSegments;
        _phaseLabel = new Label
        {
            Text = _phase.Describe(),
            ForeColor = Color.FromArgb(170, 178, 192),
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };
        _header = new BufferedPanel { Dock = DockStyle.Top, Height = DesignTitleHeight, BackColor = Card };
        _header.Paint += PaintHeader;
        Controls.Add(_phaseLabel);
        Controls.Add(_header);
        Controls.Add(_bar);

        // The current segment breathes, so a launch that is ON a step looks alive and one that has moved past
        // it looks settled. Cheap: one small panel repainted 25 times a second while the splash is showing.
        _pulse.Tick += (_, _) =>
        {
            _pulseTick++;
            _bar.Invalidate();
        };
        _fade.Tick += OnFadeTick;
        VisibleChanged += (_, _) => _pulse.Enabled = Visible;
    }

    /// <summary>
    /// Sizes the card for the monitor it opens on. Under the SystemAware default this was free, because
    /// Windows scaled the whole window; per-monitor v2 hands us real pixels, so an unscaled 420x150 card
    /// would open at two thirds size on a 150% display.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var scale = Scale96;
        if (Math.Abs(scale - 1.0) < 0.001)
        {
            return;
        }

        Size = new Size((int)Math.Round(DesignSize.Width * scale), (int)Math.Round(DesignSize.Height * scale));
        _header.Height = (int)Math.Round(DesignTitleHeight * scale);
        _bar.Height = (int)Math.Round(DesignBarHeight * scale);
        CenterToScreen();
    }

    private double Scale96 => DeviceDpi / 96.0;

    public void SetPhase(LaunchPhase phase)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetPhase(phase));
            return;
        }

        // A phase arriving means a launch is running, so a fade still in flight from the last one is over:
        // the tray's Open after a Stop shows this same form again, and it must come back solid.
        CancelFade();
        _phase = phase;
        _phaseLabel.Text = phase.Describe();
        _bar.Invalidate();
    }

    /// <summary>
    /// Leaves by fading out ABOVE the studio window that is opening under it, so launch reads as one motion.
    /// Only the splash is ever faded: a form hosting WebView2 must not be made a layered window, which is what
    /// <see cref="Form.Opacity"/> does, because the embedded browser stops painting inside one. Call it just
    /// before the window is shown; a failure path keeps the plain <see cref="Control.Hide"/>.
    /// </summary>
    public void FadeAway()
    {
        if (IsDisposed || Disposing || !Visible)
        {
            return;
        }

        TopMost = true;
        _fade.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        var next = Opacity - FadeStep;
        if (next > 0.02)
        {
            Opacity = next;
            return;
        }

        CancelFade();
        Hide();
    }

    private void CancelFade()
    {
        _fade.Stop();
        TopMost = false;
        Opacity = 1.0;
    }

    /// <summary>The brand icon and the name, centered together as one group.</summary>
    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var icon = (int)Math.Round(DesignIconSize * Scale96);
        var gap = (int)Math.Round(10 * Scale96);
        var text = g.MeasureString(Text, _titleFont);
        var width = icon + gap + text.Width;
        var left = (_header.Width - width) / 2f;
        // Sits low in the header, where the old bottom-aligned title sat, so the card's balance is unchanged.
        var middle = _header.Height - (int)Math.Round(8 * Scale96) - Math.Max(icon, text.Height) / 2f;

        using (var sized = new Icon(_brand, new Size(icon, icon)))
        using (var bitmap = sized.ToBitmap())
        {
            g.DrawImage(bitmap, left, middle - icon / 2f, icon, icon);
        }

        using var brush = new SolidBrush(Color.White);
        g.DrawString(Text, _titleFont, brush, left + icon + gap, middle - text.Height / 2f);
    }

    /// <summary>One rounded segment per launch step: done is solid, the current one breathes, the rest wait.</summary>
    private void PaintSegments(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var states = LaunchProgress.For(_phase);
        var scale = Scale96;
        var margin = (float)(24 * scale);
        var gap = (float)(5 * scale);
        var height = (float)(4 * scale);
        var top = (float)(4 * scale);
        var width = (_bar.Width - 2 * margin - gap * (states.Count - 1)) / states.Count;

        // A slow triangle wave between 35% and 100% of the accent, about one breath every 1.6 seconds.
        var wave = Math.Abs(((_pulseTick % 40) / 20.0) - 1.0);
        var breathing = Blend(SegmentPending, SegmentDone, 0.35 + 0.65 * wave);

        for (var i = 0; i < states.Count; i++)
        {
            var color = states[i] switch
            {
                LaunchSegmentState.Done => SegmentDone,
                LaunchSegmentState.Current => breathing,
                _ => SegmentPending,
            };
            using var brush = new SolidBrush(color);
            using var path = Rounded(new RectangleF(margin + i * (width + gap), top, width, height), height / 2f);
            g.FillPath(brush, path);
        }
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 90, 180);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulse.Dispose();
            _fade.Dispose();
            _titleFont.Dispose();
            _brand.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>A panel that paints through a back buffer, so a segment repainted 25 times a second never flickers.</summary>
    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => DoubleBuffered = true;
    }
}
