using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

// El OCR que trae Windows: no hay que instalar ni empaquetar nada. La página la dibuja PDFium.
static class WindowsOcr
{
    const double Dpi = 300;

    static OcrEngine? Motor() => OcrEngine.TryCreateFromLanguage(new Language("es"))
                                 ?? OcrEngine.TryCreateFromUserProfileLanguages()
                                 ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));

    // null si este Windows no tiene ningún idioma de OCR instalado.
    public static Ocr? Crear()
    {
        var motor = Motor();
        return motor is null ? null : (ruta, hoja) => Leer(motor, ruta, hoja);
    }

    /// <summary>Para las fotos y los escaneos de los firmados, que se leen de a varios a la vez. null si no hay OCR.</summary>
    public static LeerImagen? Lector()
    {
        if (Motor() is not { } primero) return null;
        // Un motor por lectura simultánea: Windows no documenta que un OcrEngine admita dos a la vez.
        var libres = new System.Collections.Concurrent.ConcurrentBag<OcrEngine> { primero };
        return async (ruta, hoja, buscado) =>
        {
            var motor = libres.TryTake(out var m) ? m : Motor()!;
            try { return await LeerImagen(motor, ruta, hoja, buscado); }
            finally { libres.Add(motor); }
        };
    }

    // ponytail-keep: se prueban los cuatro giros. Hay fotos cabeza abajo, y el OCR de Windows no las
    // endereza: lee basura y no encuentra el CUIL. El que manda es el giro que trae a alguien de la nómina;
    // si ninguno lo trae, el que más palabras leyó, y ahí se prueba con más contraste.
    static async Task<Lectura> LeerImagen(OcrEngine motor, string ruta, int hoja, Func<string, bool> buscado)
    {
        var (px, ancho, alto) = await Cargar(ruta, hoja, (int)OcrEngine.MaxImageDimension, agrandar: true);
        Lectura? mejor = null;
        var puntos = -1;
        async Task<Lectura?> Probar(byte[] imagen, int giro)
        {
            var (g, w, h) = Retoque.Girar(imagen, ancho, alto, giro);
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(g.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
            var r = await motor.RecognizeAsync(bmp);
            var l = new Lectura(string.Join(" ", r.Lines.Select(x => x.Text)), true, giro, r.TextAngle ?? 0);
            if (buscado(l.Texto)) return l;
            // Al revés, el OCR lee pedacitos sueltos; derecho, palabras enteras.
            var n = Regex.Count(l.Texto, @"\b\p{L}{4,}\b");
            if (n > puntos) (mejor, puntos) = (l, n);
            return null;
        }
        foreach (var giro in (int[])[0, 180, 90, 270])
            if (await Probar(px, giro) is { } l) return l;
        return await Probar(Retoque.Contraste(px), mejor!.Giro) ?? mejor!;
    }

    /// <summary>La versión que se guarda: girada como se leyó, derecha, con contraste y de un tamaño razonable
    /// (2400 puntos de lado largo: unos 200 por pulgada en una hoja A4).</summary>
    public static async Task<(byte[] Jpeg, int Ancho, int Alto)> Corregir(Hallazgo h)
    {
        var (px, ancho, alto) = await Cargar(h.Ruta, h.Hoja, 2400, agrandar: false);
        (px, ancho, alto) = Retoque.Girar(px, ancho, alto, h.Lectura.Giro);
        // Más de 10 grados no es una foto torcida, es una lectura rara: mejor no tocarla.
        if (Math.Abs(h.Lectura.Angulo) is > 0.3 and < 10) px = Retoque.Enderezar(px, ancho, alto, -h.Lectura.Angulo);
        return (await Jpeg(Retoque.Contraste(px), ancho, alto), ancho, alto);
    }

    // Una foto, o una hoja de un PDF escaneado (hoja desde 1), en BGRA y con el lado largo de maxLado o menos.
    internal static async Task<(byte[] Px, int Ancho, int Alto)> Cargar(string ruta, int hoja, int maxLado, bool agrandar)
    {
        if (hoja > 0)
        {
            var p = Pdfium.Dibujar(ruta, hoja - 1, Dpi, maxLado);
            return (p.Pixeles, p.Ancho, p.Alto);
        }
        var bytes = await File.ReadAllBytesAsync(ruta);
        using var memoria = new InMemoryRandomAccessStream();
        await memoria.WriteAsync(bytes.AsBuffer());
        memoria.Seek(0);
        var decodificador = await BitmapDecoder.CreateAsync(memoria);
        // Con la orientación EXIF: la foto queda como se ve en el visor de Windows.
        var datos = await decodificador.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return Retoque.Ajustar(datos.DetachPixelData(), (int)decodificador.OrientedPixelWidth, (int)decodificador.OrientedPixelHeight, maxLado, agrandar);
    }

    internal static async Task<byte[]> Jpeg(byte[] px, int ancho, int alto)
    {
        using var memoria = new InMemoryRandomAccessStream();
        var calidad = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85f, PropertyType.Single) };
        var codificador = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, memoria, calidad);
        codificador.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)ancho, (uint)alto, 96, 96, px);
        await codificador.FlushAsync();
        var salida = new byte[memoria.Size];
        using var lector = new DataReader(memoria.GetInputStreamAt(0));
        await lector.LoadAsync((uint)memoria.Size);
        lector.ReadBytes(salida);
        return salida;
    }

    static async Task<IReadOnlyList<Palabra>> Leer(OcrEngine motor, string ruta, int hoja)
    {
        var p = Pdfium.Dibujar(ruta, hoja - 1, Dpi, (int)OcrEngine.MaxImageDimension);
        using var imagen = SoftwareBitmap.CreateCopyFromBuffer(p.Pixeles.AsBuffer(), BitmapPixelFormat.Bgra8, p.Ancho, p.Alto, BitmapAlphaMode.Ignore);
        var resultado = await motor.RecognizeAsync(imagen);

        var escala = p.AnchoPt / p.Ancho;
        return resultado.Lines.SelectMany(l => l.Words).Select(w =>
        {
            var r = w.BoundingRect;
            return new Palabra(w.Text, r.X * escala, p.AltoPt - (r.Y + r.Height) * escala, (r.X + r.Width) * escala, p.AltoPt - r.Y * escala);
        }).ToList();
    }
}

// ponytail-keep: PDFium y no Windows.Data.Pdf. En la VM de UTM, Windows.Data.Pdf dibujaba casi
// transparentes las letras con curvas de la nómina de AFIP (su texto viene convertido en trazos): el OCR
// leía solo las rectas (L, T, I, N, E, 1, 7) y perdía los CUIL. PDFium dibuja por software, sin placa de video.
static class Pdfium
{
    const string Dll = "pdfium";
    static readonly object Llave = new(); // PDFium no admite llamadas simultáneas

    static Pdfium() => FPDF_InitLibrary();

    public record Imagen(byte[] Pixeles, int Ancho, int Alto, double AnchoPt, double AltoPt);

    /// <summary>La hoja (desde 0) en BGRA sobre blanco, a esa resolución o menos si no entra en maxLado.</summary>
    public static Imagen Dibujar(string ruta, int indice, double dpi, int maxLado) => Dibujar(File.ReadAllBytes(ruta), indice, dpi, maxLado);

    /// <summary>Lo mismo con un PDF que está en memoria: la vista previa de las iniciales, que todavía no se guardó.</summary>
    public static Imagen Dibujar(byte[] bytes, int indice, double dpi, int maxLado)
    {
        lock (Llave)
        {
            // Desde memoria: así no importa cómo codifica PDFium las rutas con acentos.
            var datos = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, datos, bytes.Length);
            var doc = FPDF_LoadMemDocument(datos, bytes.Length, null);
            try
            {
                if (doc == IntPtr.Zero) throw new InvalidDataException("PDFium no pudo abrir el PDF");
                var pagina = FPDF_LoadPage(doc, indice);
                try
                {
                    double anchoPt = FPDF_GetPageWidth(pagina), altoPt = FPDF_GetPageHeight(pagina);
                    var escala = Math.Min(dpi / 72, maxLado / Math.Max(anchoPt, altoPt));
                    int ancho = (int)(anchoPt * escala), alto = (int)(altoPt * escala);
                    var pixeles = new byte[ancho * alto * 4];
                    var fijado = GCHandle.Alloc(pixeles, GCHandleType.Pinned);
                    try
                    {
                        const int Bgra = 4;
                        var bmp = FPDFBitmap_CreateEx(ancho, alto, Bgra, fijado.AddrOfPinnedObject(), ancho * 4);
                        FPDFBitmap_FillRect(bmp, 0, 0, ancho, alto, 0xFFFFFFFF);
                        FPDF_RenderPageBitmap(bmp, pagina, 0, 0, ancho, alto, 0, 0);
                        FPDFBitmap_Destroy(bmp);
                    }
                    finally { fijado.Free(); }
                    return new Imagen(pixeles, ancho, alto, anchoPt, altoPt);
                }
                finally { FPDF_ClosePage(pagina); }
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                Marshal.FreeHGlobal(datos);
            }
        }
    }

    [DllImport(Dll)] static extern void FPDF_InitLibrary();
    [DllImport(Dll)] static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Dll)] static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Dll)] static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Dll)] static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport(Dll)] static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Dll)] static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Dll)] static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Dll)] static extern void FPDFBitmap_Destroy(IntPtr bitmap);
}
