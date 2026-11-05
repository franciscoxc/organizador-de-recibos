using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

// El OCR que trae Windows: no hay que instalar ni empaquetar nada.
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
        var archivo = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(ruta));
        var pdf = await PdfDocument.LoadFromFileAsync(archivo);
        using var pagina = pdf.GetPage((uint)hoja - 1);
        // Size viene en DIP (1/96"); el PDF mide en puntos (1/72").
        var anchoPt = pagina.Size.Width * 72 / 96;
        var altoPt = pagina.Size.Height * 72 / 96;
        var anchoPx = (uint)Math.Min(anchoPt * Dpi / 72, OcrEngine.MaxImageDimension);

        using var flujo = new InMemoryRandomAccessStream();
        await pagina.RenderToStreamAsync(flujo, new PdfPageRenderOptions { DestinationWidth = anchoPx });
        var decoder = await BitmapDecoder.CreateAsync(flujo);
        using var imagen = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var resultado = await motor.RecognizeAsync(imagen);

        var escala = anchoPt / imagen.PixelWidth;
        return resultado.Lines.SelectMany(l => l.Words).Select(w =>
        {
            var r = w.BoundingRect;
            return new Palabra(w.Text, r.X * escala, altoPt - (r.Y + r.Height) * escala, (r.X + r.Width) * escala, altoPt - r.Y * escala);
        }).ToList();
    }
}
