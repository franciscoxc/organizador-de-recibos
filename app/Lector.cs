using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RapidOcrNet;
using SkiaSharp;

// El OCR de los firmados: RapidOcrNet con los modelos PP-OCRv6 small de PaddleOCR, sobre ONNX Runtime. Con las fotos y
// los escaneos reales de octubre y las dos pasadas, encuentra a la persona por el CUIL en 187 de 201 hojas; el v5 mobile
// que usaba antes, en 182, y el OCR de Windows, en 88. El medium se descartó: pesa 132 MB y tarda unas cinco veces más
// por pasada. Dos lecturas: la rápida, una pasada para todas las hojas, y a fondo, al final, solo para las que no
// trajeron a nadie de la nómina.
static class Lector
{
    // El clasificador viene en el paquete (es el de v5: v6 no trae uno propio); los otros tres, en app/modelos.
    const string Det = "PP-OCRv6_det_small.onnx", Cls = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        Rec = "PP-OCRv6_rec_small.onnx", Dic = "ppocrv6_dict.txt";

    // Las opciones de v6 y no las Default del paquete, que achican la hoja a 1024 puntos y le agregan un borde: están
    // hechas para v5. Con estas, la hoja llega al detector de hasta 2000 puntos de lado largo; la ampliada, de 3200.
    static readonly RapidOcrOptions Normal = RapidOcrOptions.PPOCRv6;
    static readonly RapidOcrOptions Ampliado = RapidOcrOptions.PPOCRv6 with { MaxSideLen = 3200 };

    /// <summary>La lectura rápida y la lectura a fondo. null si los modelos no se pudieron preparar.</summary>
    public static (LeerImagen Rapido, LeerImagen AFondo)? Crear()
    {
        if (Modelos() is not { } carpeta) return null;
        // El conjunto de v6 y no las rutas sueltas: trae la normalización del detector de v6, que no es la de v5.
        var modelos = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(carpeta, Det), ClsModelPath = Path.Combine(carpeta, Cls),
            RecModelPath = Path.Combine(carpeta, Rec), KeysPath = Path.Combine(carpeta, Dic),
        };
        var libres = new ConcurrentBag<RapidOcr>();
        async Task<Lectura> Con(Func<RapidOcr, byte[], int, int, Func<string, bool>, Lectura> leer, string ruta, int hoja, Func<string, bool> buscado)
        {
            var (px, ancho, alto) = await WindowsOcr.Cargar(ruta, hoja, 3000);
            if (!libres.TryTake(out var motor))
            {
                motor = new RapidOcr();
                // Un hilo por motor: la revisión ya lee un archivo por núcleo.
                var opciones = RapidOcr.GetDefaultSessionOptions(1);
                // ponytail-keep: sin la reserva de memoria de ONNX. Con ella, cada motor se quedaba con su pico para
                // siempre: con la app quieta, Windows seguía guardando 2,2 GB comprimidos suyos en la VM de 4 GB, y
                // las pruebas que corrían al lado fallaban. Sin ella, cada lectura devuelve lo suyo al terminar.
                opciones.EnableCpuMemArena = false;
                motor.InitModels(modelos, opciones);
            }
            try { return leer(motor, px, ancho, alto, buscado); }
            finally { libres.Add(motor); }
        }
        return ((ruta, hoja, buscado) => Con(Rapida, ruta, hoja, buscado), (ruta, hoja, buscado) => Con(AFondo, ruta, hoja, buscado));
    }

    // Una pasada, derecha: el clasificador del motor ya da vuelta los renglones que vienen cabeza abajo.
    static Lectura Rapida(RapidOcr motor, byte[] px, int ancho, int alto, Func<string, bool> buscado)
    {
        var r = Leer(motor, px, ancho, alto, Normal);
        return new Lectura(r.StrRes, Ocr: true, Angulo: Inclinacion(r));
    }

    // Lo que la pasada rápida no leyó. Primero, si el texto está de costado; después más ampliación, contraste y la
    // franja de arriba (ahí tiene el CUIL el recibo) ampliada. La primera que trae a alguien de la nómina gana; si
    // ninguna, queda la que más leyó, marcada para no volver a intentar.
    static Lectura AFondo(RapidOcr motor, byte[] px, int ancho, int alto, Func<string, bool> buscado)
    {
        var derecha = Leer(motor, px, ancho, alto, Normal);
        var mejor = new Lectura(derecha.StrRes, Ocr: true, Angulo: Inclinacion(derecha), AFondo: true);
        if (buscado(mejor.Texto)) return mejor;
        foreach (var giro in DeCostado(derecha) ? new[] { 90, 270 } : [0])
        {
            var (g, w, h) = Retoque.Girar(px, ancho, alto, giro);
            var (arriba, aw, ah) = Retoque.Ajustar(Franja(g, w, h, 0.45), w, (int)(h * 0.45), 3000, agrandar: true);
            var intentos = new List<Func<OcrResult>>();
            if (giro != 0) intentos.Add(() => Leer(motor, g, w, h, Normal));
            intentos.Add(() => Leer(motor, g, w, h, Ampliado));
            intentos.Add(() => Leer(motor, Retoque.Contraste(g), w, h, Ampliado));
            intentos.Add(() => Leer(motor, Retoque.Contraste(arriba), aw, ah, Ampliado));
            foreach (var intento in intentos)
            {
                var r = intento();
                var l = new Lectura(r.StrRes, Ocr: true, Giro: giro, Angulo: Inclinacion(r), AFondo: true);
                if (buscado(l.Texto)) return l with { Angulo = giro == 0 ? mejor.Angulo : l.Angulo };
                if (l.Texto.Length > mejor.Texto.Length) mejor = l;
            }
        }
        return mejor;
    }

    static OcrResult Leer(RapidOcr motor, byte[] px, int ancho, int alto, RapidOcrOptions opciones)
    {
        using var bmp = new SKBitmap(new SKImageInfo(ancho, alto, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return motor.Detect(bmp, opciones, CancellationToken.None);
    }

    // Una hoja de costado da renglones más altos que anchos (el detector los encuentra igual, parados).
    static bool DeCostado(OcrResult r)
    {
        int parados = 0, acostados = 0;
        foreach (var b in r.TextBlocks.Where(b => b.BoxPoints.Length == 4))
        {
            double ancho = Distancia(b.BoxPoints[0], b.BoxPoints[1]), alto = Distancia(b.BoxPoints[1], b.BoxPoints[2]);
            if (alto > ancho * 1.5) parados++;
            else if (ancho > alto * 1.5) acostados++;
        }
        return parados > acostados;
    }

    // La inclinación de la hoja: la mediana de la de los renglones largos, en sentido horario (como la daba el OCR de
    // Windows: Corregir la endereza al revés).
    static double Inclinacion(OcrResult r)
    {
        var angulos = r.TextBlocks.Where(b => b.BoxPoints.Length == 4 && Distancia(b.BoxPoints[0], b.BoxPoints[1]) > 3 * Distancia(b.BoxPoints[1], b.BoxPoints[2]))
            .Select(b => Math.Atan2(b.BoxPoints[1].Y - b.BoxPoints[0].Y, b.BoxPoints[1].X - b.BoxPoints[0].X) * 180 / Math.PI)
            .Where(a => Math.Abs(a) < 10).Order().ToList();
        return angulos.Count == 0 ? 0 : angulos[angulos.Count / 2];
    }

    static double Distancia(SKPointI a, SKPointI b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    static byte[] Franja(byte[] px, int ancho, int alto, double hasta)
    {
        var filas = (int)(alto * hasta);
        var salida = new byte[ancho * filas * 4];
        Buffer.BlockCopy(px, 0, salida, 0, salida.Length);
        return salida;
    }

    // Los modelos viajan adentro del .exe y se sacan una vez a la carpeta de la app: el motor los abre por ruta.
    static string? Modelos()
    {
        try
        {
            var carpeta = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Organizador de Recibos", "modelos", "v6")).FullName;
            foreach (var nombre in new[] { Det, Cls, Rec, Dic })
            {
                using var recurso = typeof(Lector).Assembly.GetManifestResourceStream("modelos/" + nombre) ?? throw new FileNotFoundException(nombre);
                var destino = Path.Combine(carpeta, nombre);
                if (File.Exists(destino) && new FileInfo(destino).Length == recurso.Length) continue;
                using var archivo = File.Create(destino);
                recurso.CopyTo(archivo);
            }
            return carpeta;
        }
        catch
        {
            return null; // sin los modelos, las fotos y los escaneos quedan sin leer, y se avisa (ver RevisarClientes)
        }
    }
}
