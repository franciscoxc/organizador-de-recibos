using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// El OCR que trae Windows: no hay que instalar ni empaquetar nada. La página la dibuja PDFium.
static class WindowsOcr
{
    const double Dpi = 300;

    // null si este Windows no tiene ningún idioma de OCR instalado.
    public static Ocr? Crear()
    {
        var motor = OcrEngine.TryCreateFromLanguage(new Language("es"))
                    ?? OcrEngine.TryCreateFromUserProfileLanguages()
                    ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        return motor is null ? null : (ruta, hoja) => Leer(motor, ruta, hoja);
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
    public static Imagen Dibujar(string ruta, int indice, double dpi, int maxLado)
    {
        var bytes = File.ReadAllBytes(ruta);
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
