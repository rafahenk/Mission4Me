using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;

namespace Mission4Me.UI;

/// <summary>
/// Motor do "pulso de cor": a cada quadro da tela (CompositionTarget.Rendering, sincronizado
/// com o monitor, mas com a cor atualizada em passos fixos de 60 Hz) calcula
///
///     mistura(t) = (1 − cos 2πft) / 2        → senoide pura entre 0 (cor base) e 1 (vermelho)
///
/// a partir de um relógio contínuo, então a frequência é exata mesmo se algum quadro atrasar.
/// As frequências oferecidas dividem 60 Hz: cada ciclo dura um número inteiro de quadros.
///
/// O caminho base → vermelho é uma curva de Bézier quadrática passando pelo tom "meio"
/// (violeta), porque a mistura direta teal↔vermelho em RGB fica acinzentada.
/// Todos os canais compartilham o mesmo relógio: contador e janela pulsam em sincronia.
/// </summary>
public sealed class ColorPulse
{
    public static ColorPulse Instance { get; } = new();

    /// <summary>Frequências (Hz) cujo período é inteiro em quadros de 60 Hz.</summary>
    public static readonly double[] Frequencies = { 0.1, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.75, 1.0 };

    public static double Snap(double hz) => Frequencies.OrderBy(f => Math.Abs(f - hz)).First();
    public static int FramesPerCycle(double hz) => (int)Math.Round(60 / hz);

    private const double FadeSeconds = 0.6;   // transição suave quando o estado muda

    public sealed class Channel
    {
        internal Action<Color> Set = null!;
        internal Color Base, Red, Current, From;
        internal Color? Mid;
        internal bool Pulse;
        internal double FadeStart = -1;
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Channel> _channels = new();
    private bool _running;
    private bool _visible = true;

    /// <summary>
    /// Nada na tela para animar (tela cheia, bloqueio, contador oculto e janela fechada):
    /// para de assinar o Rendering, que força o WPF a desenhar todo quadro.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (visible) EnsureRunning();
    }

    public bool Enabled { get; private set; } = true;
    public double Hz { get; private set; } = 0.25;

    private double Now => _clock.Elapsed.TotalSeconds;

    public void Configure(bool enabled, double hz)
    {
        Enabled = enabled;
        Hz = Snap(hz);
        foreach (var c in _channels) StartFade(c);  // liga/desliga sem salto
        EnsureRunning();
    }

    public Channel Register(Action<Color> set, Color initial)
    {
        var c = new Channel { Set = set, Base = initial, Red = initial, Current = initial, From = initial };
        _channels.Add(c);
        set(initial);
        return c;
    }

    /// <summary>Define base/vermelho do canal. pulse=false → fica parado na base.</summary>
    public void Target(Channel c, Color baseColor, Color? mid, Color red, bool pulse)
    {
        if (c.Base == baseColor && c.Red == red && c.Mid == mid && c.Pulse == pulse) return;
        c.Base = baseColor; c.Mid = mid; c.Red = red; c.Pulse = pulse;
        StartFade(c);
        EnsureRunning();
    }

    public void Target(Channel c, string baseHex, string? midHex, string redHex, bool pulse) =>
        Target(c, Hex(baseHex), midHex == null ? null : Hex(midHex), Hex(redHex), pulse);

    private void StartFade(Channel c) { c.From = c.Current; c.FadeStart = Now; }

    private void EnsureRunning()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Taxa fixa de atualização da cor, independente do monitor (60, 75, 144 Hz…).</summary>
    public const int TickRate = 60;

    private long _lastTick = -1;

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_visible)
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
            _lastTick = -1;
            return;
        }
        double t = Now;

        // Travado em 60 Hz: a cor só muda a cada 1/60 s. Num monitor de 60 Hz é 1 passo por quadro;
        // em 120/144 Hz os quadros extras repetem a cor, então o ciclo tem sempre exatamente
        // 60/f passos (ex.: 0,6 Hz = 100 passos) em qualquer tela.
        long tick = (long)Math.Floor(t * TickRate);
        bool fading = _channels.Any(c => c.FadeStart >= 0);
        if (tick == _lastTick && !fading) return;
        _lastTick = tick;

        double tq = (double)tick / TickRate;
        double mix = (1 - Math.Cos(2 * Math.PI * Hz * tq)) / 2;
        bool keepGoing = false;

        foreach (var c in _channels)
        {
            bool pulsing = Enabled && c.Pulse;
            var target = pulsing ? Along(c, mix) : c.Base;
            if (pulsing) keepGoing = true;

            if (c.FadeStart >= 0)
            {
                double k = (t - c.FadeStart) / FadeSeconds;
                if (k >= 1) c.FadeStart = -1;
                else { keepGoing = true; target = Lerp(c.From, target, k * k * (3 - 2 * k)); } // smoothstep
            }

            if (target != c.Current) { c.Current = target; c.Set(target); }
        }

        if (!keepGoing)                // nada pulsando nem em transição: para de gastar CPU
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
            _lastTick = -1;
        }
    }

    // ---------------------------------------------------------------- cor
    /// <summary>
    /// Posição da cor no caminho base → (meio) → vermelho, calculada em Oklab (espaço perceptual):
    /// passos iguais de "mistura" parecem passos iguais ao olho, sem trechos que escurecem ou lavam.
    /// </summary>
    private static Color Along(Channel c, double m)
    {
        var a = Oklab.From(c.Base);
        var r = Oklab.From(c.Red);
        if (c.Mid is not Color midColor) return Oklab.Lerp(a, r, m).ToColor();

        // Bézier quadrática em Oklab que passa exatamente pelo tom do meio em m = 0,5
        var mid = Oklab.From(midColor);
        var ctrl = 2 * mid - 0.5 * (a + r);
        double u = 1 - m;
        return (u * u * a + 2 * u * m * ctrl + m * m * r).ToColor();
    }

    /// <summary>Oklab (Björn Ottosson, 2020): L = luminosidade, a/b = eixos de cor.</summary>
    private readonly record struct Oklab(double L, double A, double B)
    {
        public static Oklab operator +(Oklab x, Oklab y) => new(x.L + y.L, x.A + y.A, x.B + y.B);
        public static Oklab operator -(Oklab x, Oklab y) => new(x.L - y.L, x.A - y.A, x.B - y.B);
        public static Oklab operator *(double k, Oklab x) => new(k * x.L, k * x.A, k * x.B);

        public static Oklab Lerp(Oklab x, Oklab y, double k) => x + k * (y - x);

        private static double ToLinear(byte c)
        {
            double v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static byte ToSrgb(double v)
        {
            v = Math.Clamp(v, 0, 1);
            double s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
        }

        public static Oklab From(Color c)
        {
            double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
            double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            return new Oklab(
                0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        public Color ToColor()
        {
            double l = L + 0.3963377774 * A + 0.2158037573 * B;
            double m = L - 0.1055613458 * A - 0.0638541728 * B;
            double s = L - 0.0894841775 * A - 1.2914855480 * B;
            l = l * l * l; m = m * m * m; s = s * s * s;
            return Color.FromRgb(
                ToSrgb(+4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                ToSrgb(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                ToSrgb(-0.0041960863 * l - 0.7034186147 * m + 1.7076953078 * s));
        }
    }

    private static Color Lerp(Color a, Color b, double k) =>
        Color.FromArgb(Clamp(a.A + (b.A - a.A) * k), Clamp(a.R + (b.R - a.R) * k),
                       Clamp(a.G + (b.G - a.G) * k), Clamp(a.B + (b.B - a.B) * k));

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    public static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);
}
