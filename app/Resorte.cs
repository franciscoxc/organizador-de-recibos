using System.Windows;
using System.Windows.Media;

// Todo lo que se mueve va con resortes: ni la posición ni la velocidad saltan, y si el destino cambia a
// mitad de camino (otro clic, otro PDF) sigue desde donde está con la velocidad que traía. Las
// animaciones de WPF no sirven para eso: al cambiarles el destino arrancan de cero la velocidad.
static class Resorte
{
    sealed class Estado
    {
        public double X, V, Destino, Rigidez, Freno;
        public Action<double> Aplicar = _ => { };
        public Action? AlLlegar;
    }

    static readonly Dictionary<object, Estado> estados = [];
    static readonly HashSet<object> activos = [];
    static TimeSpan anterior;
    static bool enganchado;

    // «Efectos de animación» apagado en Windows: nada se desliza ni rebota, todo llega al instante.
    // Lo que entra o sale lo hace con un fundido (ver Ventana.Aparecer).
    public static bool Quieto => !SystemParameters.ClientAreaAnimation;

    /// <param name="clave">Lo que se anima; con la misma clave, un destino nuevo sigue desde donde está.</param>
    /// <param name="rebote">0: llega sin pasarse; 0,3: un rebote chico.</param>
    /// <param name="velocidad">Un empujón inicial, para latir o sacudirse.</param>
    public static void Animar(object clave, double destino, Action<double> aplicar, double? desde = null,
        double velocidad = 0, double rigidez = 300, double rebote = 0, Action? alLlegar = null)
    {
        if (!estados.TryGetValue(clave, out var e)) estados[clave] = e = new Estado { X = desde ?? destino };
        else if (desde is double d) (e.X, e.V) = (d, 0);
        e.Aplicar = aplicar;
        e.Destino = destino;
        e.V += velocidad;
        e.Rigidez = rigidez;
        e.Freno = 2 * (1 - rebote) * Math.Sqrt(rigidez);
        e.AlLlegar = alLlegar;
        if (Quieto) { Terminar(clave, e); return; }
        activos.Add(clave);
        if (enganchado) return;
        anterior = TimeSpan.Zero;
        CompositionTarget.Rendering += Cuadro;
        enganchado = true;
    }

    // Para lo que se descarta (un recibo que ya llegó): sin esto su estado quedaría guardado para siempre.
    public static void Olvidar(object clave)
    {
        activos.Remove(clave);
        estados.Remove(clave);
    }

    static void Cuadro(object? s, EventArgs a)
    {
        var ahora = ((RenderingEventArgs)a).RenderingTime;
        // WPF a veces avisa dos veces el mismo cuadro: sin tiempo transcurrido no hay nada que mover.
        if (ahora == anterior) return;
        var dt = anterior == TimeSpan.Zero ? 1 / 60.0 : Math.Min((ahora - anterior).TotalSeconds, 1 / 20.0);
        anterior = ahora;
        foreach (var clave in activos.ToList())
        {
            var e = estados[clave];
            // Pasos chicos: con un resorte rígido, integrar un cuadro entero de golpe lo vuelve inestable.
            for (var resto = dt; resto > 0; resto -= 1 / 480.0)
            {
                var h = Math.Min(resto, 1 / 480.0);
                e.V += (-e.Rigidez * (e.X - e.Destino) - e.Freno * e.V) * h;
                e.X += e.V * h;
            }
            if (Math.Abs(e.X - e.Destino) < 0.002 && Math.Abs(e.V) < 0.02) Terminar(clave, e);
            else e.Aplicar(e.X);
        }
        if (activos.Count > 0) return;
        CompositionTarget.Rendering -= Cuadro;
        enganchado = false;
    }

    static void Terminar(object clave, Estado e)
    {
        (e.X, e.V) = (e.Destino, 0);
        activos.Remove(clave);
        e.Aplicar(e.X);
        var fin = e.AlLlegar;
        e.AlLlegar = null;
        fin?.Invoke();
    }
}
