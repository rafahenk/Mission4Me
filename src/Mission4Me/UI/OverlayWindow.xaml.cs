using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mission4Me.Core;

namespace Mission4Me.UI;

/// <summary>
/// Pílula sobreposta à barra de tarefas: sem borda, sempre no topo, o clique atravessa,
/// nunca rouba foco e some quando há algo em tela cheia.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double WidthDip = 272;

    private readonly MissionService _svc;
    private readonly Func<Settings> _settings;
    private IntPtr _hwnd;
    private Phase _shownPhase = (Phase)(-1);
    private Native.RECT _lastRect;
    private Storyboard? _pulse, _blink;
    private readonly SolidColorBrush _progressBrush = new(Colors.Transparent);
    private ColorPulse.Channel _g1 = null!, _g2 = null!, _progress = null!;

    public OverlayWindow(MissionService svc, Func<Settings> settings)
    {
        InitializeComponent();
        _svc = svc;
        _settings = settings;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            // clicável (abre a edição), mas nunca rouba o foco nem aparece no Alt+Tab
            ex |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        BuildAnimations();
        ProgressFill.Background = _progressBrush;
        var engine = ColorPulse.Instance;
        _g1 = engine.Register(c => G1.Color = c, Hex(Palette.RunFrom));
        _g2 = engine.Register(c => G2.Color = c, Hex(Palette.RunTo));
        _progress = engine.Register(c => _progressBrush.Color = c, Hex(Palette.Pulse));
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        MouseEnter += (_, _) => Hover.Opacity = 1;
        MouseLeave += (_, _) => Hover.Opacity = 0;
    }

    /// <summary>Clique no contador → abrir edição.</summary>
    public event Action? Clicked;

    /// <summary>Chamado a cada segundo (e a cada mudança de estado).</summary>
    public void Refresh()
    {
        var phase = _svc.Phase;
        bool shouldShow = phase != Phase.Idle && !Native.IsSomethingFullscreen();

        if (!shouldShow)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) { Show(); _lastRect = default; }

        var m = _svc.Current!;
        ProjectText.Text = m.Project;
        ClockText.Text = MissionService.FormatClock(_svc.Remaining);
        int pct = (int)Math.Round(_svc.Progress * 100);
        MetaText.Text = phase switch
        {
            Phase.Paused   => $"pausado · {MissionService.FormatShort(_svc.Elapsed)} feitos",
            Phase.Overtime => $"tempo extra · meta {MissionService.FormatShort(m.Planned)}",
            Phase.Warning  => $"reta final · {pct}%",
            _              => $"meta {MissionService.FormatShort(m.Planned)} · {pct}%",
        };

        double track = Track.ActualWidth;
        if (track > 0) ProgressFill.Width = track * _svc.Progress;

        if (phase != _shownPhase) ApplyPhase(phase);
        Reposition();
    }

    // ----------------------------------------------------------- aparência
    private void ApplyPhase(Phase phase)
    {
        _shownPhase = phase;
        var (from, to, pulse) = phase switch
        {
            Phase.Warning  => (Palette.WarnFrom,  Palette.WarnTo,  Palette.PulseWarn),
            Phase.Overtime => (Palette.OverFrom,  Palette.OverTo,  Palette.PulseOver),
            Phase.Paused   => (Palette.PauseFrom, Palette.PauseTo, Palette.PulsePause),
            _              => (Palette.RunFrom,   Palette.RunTo,   Palette.Pulse),
        };

        // fundo e progresso: transição suave + pulso para o vermelho (exceto pausado)
        var engine = ColorPulse.Instance;
        bool live = phase != Phase.Paused;
        switch (phase)
        {
            case Phase.Warning:
                engine.Target(_g1, from, null, Palette.WarnFromRed, live);
                engine.Target(_g2, to, null, Palette.WarnToRed, live);
                engine.Target(_progress, pulse, null, Palette.ProgressRed, live);
                break;
            case Phase.Overtime:
                engine.Target(_g1, from, null, Palette.OverFromRed, live);
                engine.Target(_g2, to, null, Palette.OverToRed, live);
                engine.Target(_progress, pulse, null, Palette.OverToRed, live);
                break;
            default:
                engine.Target(_g1, from, Palette.RunFromMid, Palette.RunFromRed, live);
                engine.Target(_g2, to, Palette.RunToMid, Palette.RunToRed, live);
                engine.Target(_progress, phase == Phase.Running ? Palette.Pulse : pulse, null, Palette.ProgressRed, live);
                break;
        }

        var pulseBrush = new SolidColorBrush(Hex(pulse));
        PulseDot.Fill = pulseBrush;
        PulseHalo.Fill = pulseBrush;

        if (phase == Phase.Paused) _pulse?.Stop(this); else _pulse?.Begin(this, true);
        if (phase == Phase.Overtime) _blink?.Begin(this, true);
        else { _blink?.Stop(this); ClockText.Opacity = 1; }
    }

    private void BuildAnimations()
    {
        var period = TimeSpan.FromMilliseconds(1600);
        _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation(0.6, 1.7, period) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, PulseHalo);
            Storyboard.SetTargetProperty(a, new PropertyPath($"(UIElement.RenderTransform).(ScaleTransform.{axis})"));
            _pulse.Children.Add(a);
        }
        var fade = new DoubleAnimation(0.55, 0, period);
        Storyboard.SetTarget(fade, PulseHalo);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _pulse.Children.Add(fade);

        _blink = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var b = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(900)) { EasingFunction = new SineEase() };
        Storyboard.SetTarget(b, ClockText);
        Storyboard.SetTargetProperty(b, new PropertyPath(OpacityProperty));
        _blink.Children.Add(b);
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    // ------------------------------------------------------------- posição
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;

        var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) == UIntPtr.Zero) return;
        var tb = abd.rc;

        IntPtr trayWnd = Native.FindWindow("Shell_TrayWnd", null);
        double scale = (trayWnd != IntPtr.Zero ? Native.GetDpiForWindow(trayWnd) : 96) / 96.0;
        if (scale <= 0) scale = 1;
        int S(double dip) => (int)Math.Round(dip * scale);

        int x, y, w, h;
        if (tb.Width >= tb.Height) // barra horizontal (padrão do Win11)
        {
            h = Math.Max(S(28), tb.Height - S(10));
            w = S(WidthDip);
            y = tb.Top + (tb.Height - h) / 2;

            if (_settings().Position == OverlayPosition.Left)
                x = tb.Left + S(8);
            else
            {
                IntPtr notify = trayWnd == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
                x = notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Width > 0 && nr.Left > tb.Left
                    ? nr.Left - w - S(8)
                    : tb.Right - w - S(300);
            }
        }
        else // barra vertical (Win10 com barra na lateral)
        {
            w = tb.Width - S(8);
            h = S(64);
            x = tb.Left + S(4);
            y = tb.Bottom - h - S(240);
        }

        var r = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (r.Left != _lastRect.Left || r.Top != _lastRect.Top || r.Right != _lastRect.Right || r.Bottom != _lastRect.Bottom)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            _lastRect = r;
        }
        else
        {
            // a barra de tarefas também é "topmost": ao clicar nela, ela sobe. Reafirma nossa posição.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    public void ForceReposition() => _lastRect = default;
}
