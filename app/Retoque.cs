using System.Runtime.InteropServices;

// Las fotos de los recibos firmados llegan por WhatsApp: a veces dadas vuelta, un poco torcidas y lavadas.
// Todo sobre BGRA (4 bytes por punto, como lo dan PDFium y el decodificador de Windows), sin dependencias.
public static class Retoque
{
    /// <summary>Gira en sentido horario 0, 90, 180 o 270 grados.</summary>
    public static (byte[] Px, int Ancho, int Alto) Girar(byte[] px, int ancho, int alto, int grados)
    {
        grados = (grados % 360 + 360) % 360;
        if (grados == 0) return (px, ancho, alto);
        var (w, h) = grados == 180 ? (ancho, alto) : (alto, ancho);
        var salida = new byte[px.Length];
        var s = MemoryMarshal.Cast<byte, uint>(px);
        // ponytail-keep: AsSpan explícito. Con C# 14, un arreglo pasado directo elige la versión de solo lectura
        // de Cast, y la asignación de abajo no compila (CS8331).
        var d = MemoryMarshal.Cast<byte, uint>(salida.AsSpan());
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // De cada punto de la imagen girada, de dónde viene en la original.
                var (sx, sy) = grados switch { 90 => (y, alto - 1 - x), 180 => (ancho - 1 - x, alto - 1 - y), _ => (ancho - 1 - y, x) };
                d[y * w + x] = s[sy * ancho + sx];
            }
        return (salida, w, h);
    }

    /// <summary>Gira unos pocos grados en sentido horario sobre el centro. Mismo tamaño; las esquinas que
    /// quedan sin imagen, blancas como el papel.</summary>
    public static byte[] Enderezar(byte[] px, int ancho, int alto, double grados)
    {
        var salida = new byte[px.Length];
        double a = grados * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a), cx = (ancho - 1) / 2.0, cy = (alto - 1) / 2.0;
        for (var y = 0; y < alto; y++)
            for (var x = 0; x < ancho; x++)
            {
                // El giro al revés: de dónde viene este punto. Con «y» hacia abajo, esta matriz gira en sentido horario.
                double dx = x - cx, dy = y - cy, sx = cos * dx + sin * dy + cx, sy = -sin * dx + cos * dy + cy;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy), i = (y * ancho + x) * 4;
                if (x0 < 0 || y0 < 0 || x0 + 1 >= ancho || y0 + 1 >= alto)
                {
                    salida[i] = salida[i + 1] = salida[i + 2] = salida[i + 3] = 255;
                    continue;
                }
                double fx = sx - x0, fy = sy - y0;
                int a00 = (y0 * ancho + x0) * 4, a10 = a00 + 4, a01 = a00 + ancho * 4, a11 = a01 + 4;
                for (var c = 0; c < 3; c++)
                {
                    var arriba = px[a00 + c] * (1 - fx) + px[a10 + c] * fx;
                    var abajo = px[a01 + c] * (1 - fx) + px[a11 + c] * fx;
                    salida[i + c] = (byte)Math.Round(arriba * (1 - fy) + abajo * fy);
                }
                salida[i + 3] = 255;
            }
        return salida;
    }

    /// <summary>Estira los niveles: el 1 % más oscuro va a negro y el 1 % más claro a blanco, que es el papel.
    /// Una foto ya contrastada casi no cambia; una lavada se estira hasta el triple, no más, para no
    /// agrandar el ruido.</summary>
    public static byte[] Contraste(byte[] px)
    {
        var histograma = new int[256];
        for (var i = 0; i < px.Length; i += 4) histograma[(px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000]++;
        int total = px.Length / 4, bajo = Percentil(histograma, total * 0.01), alto = Percentil(histograma, total * 0.99);
        if (alto - bajo < 8) return px; // una hoja lisa: no hay nada que estirar
        var ganancia = Math.Min(3.0, 255.0 / (alto - bajo));
        // Con la ganancia limitada, el papel igual llega a blanco: lo que se resigna es el negro.
        var desde = alto - 255 / ganancia;
        var tabla = new byte[256];
        for (var v = 0; v < 256; v++) tabla[v] = (byte)Math.Clamp(Math.Round((v - desde) * ganancia), 0, 255);
        var salida = new byte[px.Length];
        for (var i = 0; i < px.Length; i += 4)
        {
            salida[i] = tabla[px[i]];
            salida[i + 1] = tabla[px[i + 1]];
            salida[i + 2] = tabla[px[i + 2]];
            salida[i + 3] = 255;
        }
        return salida;
    }

    static int Percentil(int[] histograma, double cuantos)
    {
        var suma = 0;
        for (var v = 0; v < 256; v++)
            if ((suma += histograma[v]) >= cuantos) return v;
        return 255;
    }

    /// <summary>Al tamaño en que el lado largo mide maxLado. Achica siempre que haga falta; agranda (hasta el
    /// doble) solo si se pide, porque al OCR le cuesta la letra chica de las fotos de WhatsApp.</summary>
    public static (byte[] Px, int Ancho, int Alto) Ajustar(byte[] px, int ancho, int alto, int maxLado, bool agrandar)
    {
        var f = Math.Min(agrandar ? 2 : 1, (double)maxLado / Math.Max(ancho, alto));
        if (Math.Abs(f - 1) < 0.02) return (px, ancho, alto);
        int w = Math.Max(1, (int)(ancho * f)), h = Math.Max(1, (int)(alto * f));
        var salida = new byte[w * h * 4];
        if (f > 1)
        {
            // Al agrandar, interpolado: punto por punto quedaría en escalones, y el OCR los lee como ruido.
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    double sx = Math.Clamp((x + 0.5) / f - 0.5, 0, ancho - 1), sy = Math.Clamp((y + 0.5) / f - 0.5, 0, alto - 1);
                    int x0 = Math.Min((int)sx, Math.Max(0, ancho - 2)), y0 = Math.Min((int)sy, Math.Max(0, alto - 2));
                    int x1 = Math.Min(x0 + 1, ancho - 1), y1 = Math.Min(y0 + 1, alto - 1);
                    double fx = sx - x0, fy = sy - y0;
                    var o = (y * w + x) * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        var arriba = px[(y0 * ancho + x0) * 4 + c] * (1 - fx) + px[(y0 * ancho + x1) * 4 + c] * fx;
                        var abajo = px[(y1 * ancho + x0) * 4 + c] * (1 - fx) + px[(y1 * ancho + x1) * 4 + c] * fx;
                        salida[o + c] = (byte)Math.Round(arriba * (1 - fy) + abajo * fy);
                    }
                    salida[o + 3] = 255;
                }
            return (salida, w, h);
        }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // Al achicar, el promedio de todos los puntos que caen en este: un muestreo suelto deja
                // la letra chica dentada.
                int x0 = (int)(x / f), y0 = (int)(y / f), x1 = Math.Max(x0 + 1, Math.Min(ancho, (int)((x + 1) / f))), y1 = Math.Max(y0 + 1, Math.Min(alto, (int)((y + 1) / f)));
                int b = 0, g = 0, r = 0, n = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var i = (yy * ancho + xx) * 4;
                        b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
                    }
                var o = (y * w + x) * 4;
                salida[o] = (byte)(b / n);
                salida[o + 1] = (byte)(g / n);
                salida[o + 2] = (byte)(r / n);
                salida[o + 3] = 255;
            }
        return (salida, w, h);
    }
}
