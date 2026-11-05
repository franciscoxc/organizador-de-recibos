using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

// Coordenadas PDF: origen abajo a la izquierda, en puntos.
public record Palabra(string Texto, double Izq, double Abajo, double Der, double Arriba)
{
    public double CentroY => (Abajo + Arriba) / 2;
}

public record Hoja(string Empleador, string Nombre, int Mes, string Anio, Palabra? Firma);

// Lo que necesita el motor del OCR: las palabras de una hoja escaneada (ruta, número de hoja desde 1).
public delegate Task<IReadOnlyList<Palabra>> Ocr(string ruta, int hoja);

// Sin WinForms ni WinRT: así las pruebas lo compilan directo.
public static class Recibos
{
    static readonly Regex Periodo = new(@"(?<![\d/])(\d{1,2})\s*/\s*(\d{4})(?![\d/])");

    // Un rótulo de varias palabras: cada una a la derecha de la anterior y en la misma línea.
    public static IEnumerable<Palabra> Buscar(IReadOnlyList<Palabra> ws, string rotulo)
    {
        var partes = rotulo.Split(' ');
        foreach (var primera in ws.Where(w => Igual(w.Texto, partes[0])))
        {
            Palabra? caja = primera;
            foreach (var parte in partes.Skip(1))
            {
                var c = caja;
                var sig = ws.Where(x => Igual(x.Texto, parte) && Math.Abs(x.CentroY - c.CentroY) < 4 && x.Izq > c.Der - 1 && x.Izq - c.Der < 15)
                            .OrderBy(x => x.Izq).FirstOrDefault();
                caja = sig is null ? null : c with { Der = sig.Der, Abajo = Math.Min(c.Abajo, sig.Abajo), Arriba = Math.Max(c.Arriba, sig.Arriba) };
                if (caja is null) break;
            }
            if (caja is not null) yield return caja with { Texto = rotulo };
        }
    }

    static bool Igual(string a, string b) => string.Equals(a.Trim(':', '.'), b, StringComparison.OrdinalIgnoreCase);

    // Cada hoja trae original y duplicado lado a lado: los datos se leen de la mitad izquierda.
    public static Hoja LeerHoja(IReadOnlyList<Palabra> ws, double ancho)
    {
        var mitad = ancho / 2;
        Palabra? Izquierdo(string rotulo) => Buscar(ws, rotulo).Where(r => r.Izq < mitad).OrderBy(r => r.Izq).FirstOrDefault();

        var rn = Izquierdo("APELLIDO Y NOMBRE") ?? throw new FormatException("no encontré el rótulo APELLIDO Y NOMBRE");
        // El nombre ocupa la celda entre el borde y la columna CUIL.
        var xFin = Izquierdo("CUIL")?.Izq - 5 ?? mitad - 150;
        // El CUIL está alineado a la derecha y se mete en la celda del nombre: sin dígitos, afuera.
        var nombre = Unir(ws.Where(w => w.CentroY < rn.Abajo && w.CentroY > rn.Abajo - 18 && w.Izq > rn.Izq - 100 && w.Izq < xFin
                                        && !w.Texto.Any(char.IsDigit)));

        // El empleador es la primera línea de la hoja, a la izquierda del CUIT.
        var izquierda = ws.Where(w => w.Izq < mitad).ToList();
        var tope = izquierda.Count > 0 ? izquierda.Max(w => w.Arriba) : 0;
        var xCuit = Izquierdo("CUIT")?.Izq ?? mitad;
        var empleador = Unir(izquierda.Where(w => w.Arriba > tope - 8 && w.Der < xCuit));

        var m = Periodo.Match(string.Join(" ", ws.Select(w => w.Texto)));
        if (nombre.Length == 0 || !m.Success) throw new FormatException($"no pude leer el nombre o el período (nombre: «{nombre}»)");

        // El del empleado es el de la derecha; el de la izquierda dice EMPLEADOR y no coincide igual.
        var firma = Buscar(ws, "FIRMA DEL EMPLEADO").OrderByDescending(r => r.Izq).FirstOrDefault();
        return new Hoja(empleador.Length > 0 ? empleador : "EMPLEADOR", nombre, int.Parse(m.Groups[1].Value), m.Groups[2].Value, firma);
    }

    static string Unir(IEnumerable<Palabra> ws) => string.Join(" ", ws.OrderBy(w => w.Izq).Select(w => w.Texto)).Trim();

    public static string SinComa(string nombre) => Regex.Replace(nombre.Replace(",", " "), @"\s+", " ").Trim().ToUpperInvariant();

    // "PEREZ, JUAN CARLOS" -> "P.J.C.": en el orden del recibo, apellidos y después nombres.
    public static string Iniciales(string nombre) =>
        string.Concat(SinComa(nombre).Split(' ').Where(p => p.Length > 0 && p.All(char.IsLetter)).Select(p => p[0] + "."));

    public static string NombreArchivo(string nombre, int mes, string anio) =>
        $"{Limpiar(SinComa(nombre))} {mes:00} {anio}.pdf";

    // Los caracteres que Windows no acepta en un nombre de archivo.
    static string Limpiar(string s) => string.Concat(s.Where(c => !"\\/:*?\"<>|".Contains(c) && !char.IsControl(c)));

    static IReadOnlyList<Palabra> Palabras(UglyToad.PdfPig.Content.Page p) =>
        p.GetWords().Select(w => new Palabra(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToList();

    /// <summary>Separa los recibos y devuelve los archivos creados. Sin carpetaSalida, cada uno va junto a su PDF de origen.</summary>
    public static async Task<List<string>> Procesar(IEnumerable<string> pdfs, Ocr? ocr, Action<string> avisar, string? carpetaSalida = null)
    {
        var grupos = new Dictionary<(string Carpeta, string Empleador, string Nombre, int Mes, string Anio), List<(PdfDocument Doc, int Hoja, Palabra? Firma)>>();
        var docs = new List<PdfDocument>();
        var creados = new List<string>();
        try
        {
            foreach (var ruta in pdfs)
            {
                var doc = PdfDocument.Open(ruta);
                docs.Add(doc);
                foreach (var page in doc.GetPages())
                {
                    var donde = $"{Path.GetFileName(ruta)}, hoja {page.Number}";
                    try
                    {
                        // Los recibos que genera el sistema traen texto; solo los escaneados pasan por OCR.
                        IReadOnlyList<Palabra> ws;
                        if (page.Letters.Count > 0) ws = Palabras(page);
                        else if (ocr is not null)
                        {
                            avisar($"leyendo {donde} con OCR…");
                            ws = await ocr(ruta, page.Number);
                        }
                        else throw new FormatException("es una imagen y no hay OCR");

                        var h = LeerHoja(ws, page.Width);
                        var clave = (carpetaSalida ?? Path.GetDirectoryName(Path.GetFullPath(ruta))!, h.Empleador, h.Nombre, h.Mes, h.Anio);
                        if (!grupos.TryGetValue(clave, out var hojas)) grupos[clave] = hojas = [];
                        hojas.Add((doc, page.Number, h.Firma));
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        avisar($"⚠ {donde}: {e.Message}");
                    }
                }
            }

            foreach (var ((carpeta, empleador, nombre, mes, anio), hojas) in grupos)
            {
                var builder = new PdfDocumentBuilder();
                var fuente = builder.AddTrueTypeFont(Cursiva.Value);
                foreach (var (doc, n, firma) in hojas)
                {
                    var p = builder.AddPage(doc, n);
                    if (firma is not null) Estampar(p, fuente, firma, Iniciales(nombre));
                    else avisar($"⚠ {nombre}: no encontré el espacio de firma, va sin iniciales");
                }
                var destino = Path.Combine(carpeta, Limpiar(SinComa(empleador)));
                Directory.CreateDirectory(destino);
                var salida = Path.Combine(destino, NombreArchivo(nombre, mes, anio));
                await File.WriteAllBytesAsync(salida, builder.Build());
                creados.Add(salida);
                avisar($"✓ {Path.GetFileName(destino)}\\{Path.GetFileName(salida)}");
            }
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
        return creados;
    }

    // Parisienne (licencia OFL), embebida en el .exe: imita letra manuscrita y se lee clara.
    static readonly Lazy<byte[]> Cursiva = new(() =>
    {
        using var s = typeof(Recibos).Assembly.GetManifestResourceStream("Parisienne-Regular.ttf")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    static void Estampar(PdfPageBuilder p, PdfDocumentBuilder.AddedFont fuente, Palabra firma, string texto)
    {
        const double tam = 20;
        var letras = p.MeasureText(texto, tam, new PdfPoint(0, 0), fuente);
        var ancho = letras.Count == 0 ? 0 : letras[^1].EndBaseLine.X - letras[0].StartBaseLine.X;
        // Línea base apenas encima de la raya de firma, que está unos 8 pt sobre el rótulo.
        var punto = new PdfPoint((firma.Izq + firma.Der - ancho) / 2, firma.Arriba + 12);
        p.SetTextAndFillColor(0, 0, 140);
        p.AddText(texto, tam, punto, fuente);
        p.ResetColor();
    }
}
