namespace Mission4Me.Core;

/// <summary>
/// Paleta "Lagoon". Regra do desenho: tons claros puxam para o verde-água,
/// tons escuros puxam para o azul — o mesmo degradê teal→cobalto
/// aparece na barra nativa (sólida) e no contador (gradiente).
/// </summary>
public static class Palette
{
    // Paleta de ênfase gravada no Windows (AccentPalette, 8 entradas RGBA).
    // Ordem que o Windows espera: Claro3, Claro2, Claro1, Base, Escuro1, Escuro2, Escuro3, (reserva)
    public static readonly uint[] AccentShades =
    {
        0x9EF0E6, // Claro3  – espuma
        0x5FD9CE, // Claro2  – água rasa
        0x2EC4B6, // Claro1  – turquesa (destaques / progresso)
        0x13A3A8, // Base    – teal
        0x0E7C86, // Escuro1 – lagoa
        0x0B5F73, // Escuro2 – teal-petróleo (corpo da barra no Win11)
        0x0A4560, // Escuro3 – azul profundo
        0x1E5AA8, // reserva – cobalto
    };

    public const uint Accent      = 0x13A3A8;
    public const uint TaskbarBody = 0x0B5F73;

    // Contador sobre a barra (gradientes por estado) — formato #RRGGBB para WPF.
    public const string RunFrom      = "#0B6E79", RunTo      = "#1E5AA8"; // teal → cobalto
    public const string WarnFrom     = "#7C4A03", WarnTo     = "#D97706"; // âmbar: últimos minutos
    public const string OverFrom     = "#7F1D2D", OverTo     = "#E5484D"; // coral: tempo estourado
    public const string PauseFrom    = "#26313D", PauseTo    = "#3B4A5A"; // ardósia: pausado

    // Pulso de cor: para onde cada tom caminha (base → meio → vermelho)
    // Tons suavizados: vinho e rosé em vez de vermelho puro; violetas de passagem menos saturados.
    public const string RunFromMid  = "#3E3A7A", RunFromRed  = "#6B1F3A";   // teal    → índigo  → vinho
    public const string RunToMid    = "#5A4FB0", RunToRed    = "#C2415A";   // cobalto → lavanda → rosé
    public const string WarnFromRed = "#6B2A1A", WarnToRed   = "#C2452D";   // âmbar   → terracota
    public const string OverFromRed = "#5A0F22", OverToRed   = "#D6404C";   // coral   → carmim
    public const string ProgressRed = "#F08A8A";

    // degradê "Lagoon" da janela (3 paradas)
    public static readonly (string Base, string Mid, string Red)[] LagoonPulse =
    {
        ("#13A3A8", "#4F7AC8", "#D9606A"),
        ("#0E7C86", "#3E3A7A", "#7E2848"),
        ("#1E5AA8", "#5A4FB0", "#C2415A"),
    };

    public const string Pulse        = "#2EC4B6";
    public const string PulseWarn    = "#FCD34D";
    public const string PulseOver    = "#FFC2C4";
    public const string PulsePause   = "#8B98A5";
    public const string TextPrimary  = "#F0FFFC";
}
