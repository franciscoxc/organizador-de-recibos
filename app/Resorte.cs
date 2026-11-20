// Todo lo que se mueve va con resortes: ni la posición ni la velocidad saltan, y si el destino cambia a
// mitad de camino (otro clic, otro PDF) sigue desde donde está con la velocidad que traía. Las
// animaciones de WPF no sirven para eso: al cambiarles el destino arrancan de cero la velocidad.
// Sin WPF: así las pruebas lo compilan directo y mueven los resortes a mano, con Paso. El reloj (un paso por cada
// cuadro que dibuja la ventana) y Quieto están en Resorte.Reloj.cs.
static partial class Resorte
{
    sealed class Estado
    {
        public double X, V, Destino, Rigidez, Freno;
        public Action<double> Aplicar = _ => { };
        public Action? AlLlegar;
    }

    static readonly Dictionary<object, Estado> estados = [];
    static readonly HashSet<object> activos = [];

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
        Enganchar();
    }

    // Arranca el reloj, si está parado. Lo pone Resorte.Reloj.cs; en las pruebas no hay.
    static partial void Enganchar();

    // Para lo que se descarta (un recibo que ya llegó): sin esto su estado quedaría guardado para siempre.
    public static void Olvidar(object clave)
    {
        activos.Remove(clave);
        estados.Remove(clave);
    }

    /// <summary>Mueve dt segundos todo lo que se está moviendo.</summary>
    internal static void Paso(double dt)
    {
        foreach (var clave in activos.ToList())
        {
            // ponytail-keep: se saltea lo que ya no está. El alLlegar de una animación puede olvidar otras (el mes que se
            // va, las burbujas): si una de esas seguía en esta copia de la lista, buscarla tiraba KeyNotFoundException
            // en medio del dibujo y la app se cerraba sola (cambiando de mes mientras se revisaba, el 19/11/2026).
            if (!activos.Contains(clave) || !estados.TryGetValue(clave, out var e)) continue;
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
