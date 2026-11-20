using System.Windows;
using System.Windows.Media;

// El reloj de los resortes: un paso por cada cuadro que dibuja la ventana, y solo mientras algo se mueve.
static partial class Resorte
{
    static TimeSpan anterior;
    static bool enganchado;

    // «Efectos de animación» apagado en Windows: nada se desliza ni rebota, todo llega al instante.
    // Lo que entra o sale lo hace con un fundido (ver Ventana.Aparecer).
    public static bool Quieto => !SystemParameters.ClientAreaAnimation;

    static partial void Enganchar()
    {
        if (enganchado) return;
        anterior = TimeSpan.Zero;
        CompositionTarget.Rendering += Cuadro;
        enganchado = true;
    }

    static void Cuadro(object? s, EventArgs a)
    {
        var ahora = ((RenderingEventArgs)a).RenderingTime;
        // WPF a veces avisa dos veces el mismo cuadro: sin tiempo transcurrido no hay nada que mover.
        if (ahora == anterior) return;
        var dt = anterior == TimeSpan.Zero ? 1 / 60.0 : Math.Min((ahora - anterior).TotalSeconds, 1 / 20.0);
        anterior = ahora;
        Paso(dt);
        if (activos.Count > 0) return;
        CompositionTarget.Rendering -= Cuadro;
        enganchado = false;
    }
}
