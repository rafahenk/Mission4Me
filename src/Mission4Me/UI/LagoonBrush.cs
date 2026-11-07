using System.Windows;
using System.Windows.Media;

namespace Mission4Me.UI;

/// <summary>
/// Degradê "Lagoon" (faixa do topo e botão principal da janela).
/// Fica fora dos recursos do App de propósito: o WPF congela pincéis guardados em
/// Application.Resources, e congelado ele não pode pulsar. As telas ligam nele por
/// {Binding Source={x:Static ui:LagoonBrush.Brush}}.
/// </summary>
public static class LagoonBrush
{
    public static LinearGradientBrush Brush { get; } = new(
        new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0x13, 0xA3, 0xA8), 0),
            new GradientStop(Color.FromRgb(0x0E, 0x7C, 0x86), 0.45),
            new GradientStop(Color.FromRgb(0x1E, 0x5A, 0xA8), 1),
        },
        new Point(0, 0), new Point(1, 1));
}
