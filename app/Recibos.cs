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

public record Empleado(string Cuil, string Nombre);

// La nómina de la declaración jurada de AFIP (F.931): quiénes tienen que tener recibo ese mes.
public record Nomina(string Ruta, int Mes, string Anio, List<Empleado> Empleados);

public record Resultado(List<string> Creados, List<Nomina> Nominas);

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

    // Sirve para los dos formatos conocidos: hoja apaisada con original y duplicado lado a lado (se lee
    // la copia de la izquierda) y hoja vertical con una sola copia, a veces en dos hojas.
    public static Hoja LeerHoja(IReadOnlyList<Palabra> ws, double ancho)
    {
        var mitad = ancho / 2;
        Palabra? Primero(string rotulo) => Buscar(ws, rotulo).OrderBy(r => r.Izq).FirstOrDefault();

        var rn = Primero("APELLIDO Y NOMBRE") ?? throw new FormatException("no encontré el rótulo APELLIDO Y NOMBRE");
        var nombre = ValorDebajo(ws, rn);

        // El empleador es la primera línea de la hoja, a la izquierda del CUIT (si está en esa línea).
        var izquierda = ws.Where(w => w.Izq < mitad).ToList();
        var tope = izquierda.Count > 0 ? izquierda.Max(w => w.Arriba) : 0;
        var xCuit = Primero("CUIT")?.Izq ?? mitad;
        var empleador = Unir(izquierda.Where(w => w.Arriba > tope - 8 && w.Der < xCuit));

        var m = Periodo.Match(string.Join(" ", ws.Select(w => w.Texto)));
        if (nombre.Length == 0 || !m.Success) throw new FormatException($"no pude leer el nombre o el período (nombre: «{nombre}»)");

        // Por palabra entera: FIRMA DEL EMPLEADOR, que está en la misma hoja, no coincide.
        var firma = Buscar(ws, "FIRMA DEL EMPLEADO").OrderByDescending(r => r.Izq).FirstOrDefault();
        return new Hoja(empleador.Length > 0 ? empleador : "EMPLEADOR", nombre, int.Parse(m.Groups[1].Value), m.Groups[2].Value, firma);
    }

    static string Unir(IEnumerable<Palabra> ws) => string.Join(" ", ws.OrderBy(w => w.Izq).Select(w => w.Texto)).Trim();

    // El valor de un rótulo de tabla: la primera línea debajo, sin las palabras con números (el CUIL,
    // el legajo y los importes comparten esa línea), y de ella el bloque de palabras más cercano al rótulo.
    static string ValorDebajo(IReadOnlyList<Palabra> ws, Palabra rotulo)
    {
        var debajo = ws.Where(w => w.CentroY < rotulo.Abajo && w.CentroY > rotulo.Abajo - 25 && !w.Texto.Any(char.IsDigit)).ToList();
        if (debajo.Count == 0) return "";
        var y = debajo.Max(w => w.CentroY);
        var bloques = new List<List<Palabra>>();
        foreach (var w in debajo.Where(w => Math.Abs(w.CentroY - y) < 3).OrderBy(w => w.Izq))
        {
            // Dentro de un nombre las palabras van a un espacio; entre columnas hay mucho más.
            if (bloques.Count > 0 && w.Izq - bloques[^1][^1].Der < 12) bloques[^1].Add(w);
            else bloques.Add([w]);
        }
        var centro = (rotulo.Izq + rotulo.Der) / 2;
        var bloque = bloques.MinBy(b => Math.Abs((b[0].Izq + b[^1].Der) / 2 - centro))!;
        return string.Join(" ", bloque.Select(w => w.Texto));
    }

    // Con o sin espacios junto a los guiones, que el OCR a veces agrega; sin dígitos pegados a los lados.
    static readonly Regex CuilRegex = new(@"(?<!\d)(\d{2})\s?-\s?(\d{8})\s?-\s?(\d)(?!\d)");

    /// <summary>Los CUIL y CUIT del texto con dígito verificador válido, escritos «20-12345678-6».</summary>
    public static IEnumerable<string> Cuils(string texto) =>
        CuilRegex.Matches(texto).Select(m => $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}").Where(CuilValido);

    // El dígito verificador descarta los números que el OCR leyó mal.
    public static bool CuilValido(string cuil)
    {
        var d = cuil.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (d.Length != 11) return false;
        int[] pesos = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var dv = 11 - pesos.Select((p, i) => p * d[i]).Sum() % 11;
        return (dv == 11 ? 0 : dv == 10 ? 9 : dv) == d[10];
    }

    // Solo la nómina de AFIP lleva «Contribuyente» en el encabezado; ningún formato de recibo lo tiene.
    static bool EsNomina(IReadOnlyList<Palabra> ws) =>
        ws.Any(w => Igual(w.Texto, "Contribuyente") || w.Texto.Contains("Nomina", StringComparison.OrdinalIgnoreCase));

    /// <summary>Período y empleados de una hoja de la nómina: una columna por empleado, con su CUIL y su nombre.</summary>
    public static (int Mes, string Anio, List<Empleado> Empleados) LeerNomina(IReadOnlyList<Palabra> ws, Action<string> avisar)
    {
        var m = Periodo.Match(string.Join(" ", ws.Select(w => w.Texto)));
        if (!m.Success) throw new FormatException("no encontré el período de la nómina");
        // La fila de los empleados; el CUIT del empleador está en el encabezado y así queda afuera.
        var rotulo = ws.Where(w => w.Texto.StartsWith("CUIL", StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.Izq).FirstOrDefault()
                     ?? throw new FormatException("no encontré la fila CUIL de la nómina");
        var columnas = new List<(double X, string Cuil)>();
        foreach (var w in ws.Where(w => Math.Abs(w.CentroY - rotulo.CentroY) < 4 && w.Izq >= rotulo.Izq - 1))
        {
            var leidos = CuilRegex.Matches(w.Texto).Where(c => CuilValido(c.Value)).ToList();
            // El OCR a veces pega el rótulo al número («CUIL20-...»): la columna empieza donde empieza el número.
            columnas.AddRange(leidos.Select(c => (w.Izq + (w.Der - w.Izq) * c.Index / w.Texto.Length, Cuils(c.Value).First())));
            if (leidos.Count == 0 && w.Texto.Count(char.IsDigit) >= 9) avisar($"no pude leer bien el CUIL «{w.Texto}» de la nómina");
        }
        columnas.Sort();

        // Los nombres van debajo, hasta la fila «Obra Social», alineados a la izquierda de cada columna.
        var piso = ws.Where(w => w.Texto.StartsWith("Obra", StringComparison.OrdinalIgnoreCase) && w.CentroY < rotulo.CentroY)
                     .Select(w => w.Arriba).DefaultIfEmpty(rotulo.Abajo - 40).Max();
        // Ancho de columna: la última hoja termina con «Suma Totales», que no es de ningún empleado.
        var paso = columnas.Count > 1 ? (columnas[^1].X - columnas[0].X) / (columnas.Count - 1) : 150;
        var nombres = columnas.Select(_ => new List<Palabra>()).ToList();
        foreach (var w in ws.Where(w => w.CentroY < rotulo.Abajo && w.CentroY > piso))
        {
            var texto = w;
            // El rótulo «Apellido y Nombre» termina justo donde empieza el primer nombre y el OCR los pega
            // («NombrePEREZ»): se le saca el rótulo.
            if (columnas.Count > 0 && w.Izq < columnas[0].X - 3 && w.Der > columnas[0].X && w.Texto.StartsWith("Nombre", StringComparison.OrdinalIgnoreCase))
                texto = w with { Texto = w.Texto[6..], Izq = columnas[0].X };
            var i = columnas.FindLastIndex(c => c.X <= texto.Izq + 3);
            if (i >= 0 && texto.Izq < columnas[i].X + paso - 3 && texto.Texto.Length > 0) nombres[i].Add(texto);
        }
        var empleados = columnas.Select((c, i) => new Empleado(c.Cuil, EnRenglones(nombres[i]))).ToList();
        return (int.Parse(m.Groups[1].Value), m.Groups[2].Value, empleados);
    }

    // Un nombre en dos o tres renglones: de arriba abajo y, en cada renglón, de izquierda a derecha.
    static string EnRenglones(IEnumerable<Palabra> ws)
    {
        var renglones = new List<List<Palabra>>();
        foreach (var w in ws.OrderByDescending(w => w.CentroY))
            if (renglones.Count > 0 && renglones[^1][0].CentroY - w.CentroY < 4) renglones[^1].Add(w);
            else renglones.Add([w]);
        return string.Join(" ", renglones.SelectMany(r => r.OrderBy(w => w.Izq)).Select(w => w.Texto));
    }

    public static string Sufijo(Nomina n) => $" {n.Mes:00} {n.Anio}.pdf";

    /// <summary>Cada empleado de la nómina con su recibo de ese período, o null si no está en ninguna carpeta.</summary>
    public static List<(Empleado Empleado, string? Recibo)> Revisar(Nomina n, IEnumerable<string> carpetas)
    {
        var recibos = new Dictionary<string, string>(); // CUIL -> archivo
        foreach (var carpeta in carpetas.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Directory.GetFiles(carpeta, "*.pdf"))
            {
                try
                {
                    using var d = PdfDocument.Open(f);
                    // Un recibo ocupa una o dos hojas: los PDF más largos (el de todos juntos, o cualquier otro
                    // documento de la carpeta) no se leen, así revisar una carpeta como Descargas no tarda.
                    if (d.NumberOfPages > 3) continue;
                    var texto = d.Information.Keywords + " " + string.Join(" ", d.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
                    // Del período por el nombre que le pone la app o por el contenido, para los recibos que tienen
                    // otro nombre. Periodo descarta las fechas: la de pago del recibo de junio (05/07/2026) no
                    // lo vuelve de julio.
                    var delPeriodo = f.EndsWith(Sufijo(n), StringComparison.OrdinalIgnoreCase)
                        || Periodo.Matches(texto).Any(m => int.Parse(m.Groups[1].Value) == n.Mes && m.Groups[2].Value == n.Anio);
                    var cs = Cuils(texto).ToHashSet();
                    // Un recibo separado tiene a lo sumo dos números: el CUIL del empleado y el CUIT del empleador.
                    // Uno con más es el PDF de todos juntos y no prueba que el recibo de cada uno se haya separado.
                    if (delPeriodo && cs.Count <= 2) foreach (var c in cs) recibos.TryAdd(c, f);
                }
                catch { } // un PDF dañado o ajeno no frena la revisión
            }
        return n.Empleados.Select(e => (e, recibos.GetValueOrDefault(e.Cuil))).ToList();
    }

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

    /// <summary>Separa los recibos y lee las nóminas. Sin carpetaSalida, cada recibo va junto a su PDF de origen.</summary>
    public static async Task<Resultado> Procesar(IEnumerable<string> pdfs, Ocr? ocr, Action<string> avisar, string? carpetaSalida = null)
    {
        var grupos = new Dictionary<(string Carpeta, string Empleador, string Nombre, int Mes, string Anio), (List<(PdfDocument Doc, int Hoja, Palabra? Firma)> Hojas, HashSet<string> Cuils)>();
        var docs = new List<PdfDocument>();
        var creados = new List<string>();
        var nominas = new List<Nomina>();
        try
        {
            foreach (var ruta in pdfs)
            {
                var doc = PdfDocument.Open(ruta);
                docs.Add(doc);
                Nomina? nomina = null;
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

                        if (EsNomina(ws))
                        {
                            var (mes, anio, empleados) = LeerNomina(ws, msg => avisar($"⚠ {donde}: {msg}"));
                            var n = nomina ??= new Nomina(ruta, mes, anio, []);
                            n.Empleados.AddRange(empleados.Where(e => n.Empleados.All(x => x.Cuil != e.Cuil)));
                            continue;
                        }

                        var h = LeerHoja(ws, page.Width);
                        var clave = (carpetaSalida ?? Path.GetDirectoryName(Path.GetFullPath(ruta))!, h.Empleador, h.Nombre, h.Mes, h.Anio);
                        if (!grupos.TryGetValue(clave, out var g)) grupos[clave] = g = ([], []);
                        g.Hojas.Add((doc, page.Number, h.Firma));
                        g.Cuils.UnionWith(Cuils(string.Join(" ", ws.Select(w => w.Texto))));
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        avisar($"⚠ {donde}: {e.Message}");
                    }
                }
                if (nomina is not null) nominas.Add(nomina);
            }

            foreach (var ((carpeta, empleador, nombre, mes, anio), (hojas, cuils)) in grupos)
            {
                var builder = new PdfDocumentBuilder();
                // El CUIL y el período van también en las palabras clave: un recibo escaneado no tiene texto,
                // y la revisión de la nómina los busca ahí.
                builder.DocumentInformation.Keywords = string.Join(" ", cuils) + $" {mes:00}/{anio}";
                var fuente = builder.AddTrueTypeFont(Manuscrita.Value);
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
        return new Resultado(creados, nominas);
    }

    // Segoe Print, la letra manuscrita que trae Windows: se toma de la PC y no viaja en el .exe,
    // porque es de Microsoft. Si faltara, queda Caveat embebida (licencia OFL), igual de sencilla.
    static readonly Lazy<byte[]> Manuscrita = new(() =>
    {
        var segoe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoepr.ttf");
        if (File.Exists(segoe)) return File.ReadAllBytes(segoe);
        using var s = typeof(Recibos).Assembly.GetManifestResourceStream("Caveat-Regular.ttf")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    static void Estampar(PdfPageBuilder p, PdfDocumentBuilder.AddedFont fuente, Palabra firma, string texto)
    {
        const double tam = 16;
        var letras = p.MeasureText(texto, tam, new PdfPoint(0, 0), fuente);
        var ancho = letras.Count == 0 ? 0 : letras[^1].EndBaseLine.X - letras[0].StartBaseLine.X;
        // Línea base apenas encima de la raya de firma, que está unos 8 pt sobre el rótulo.
        var punto = new PdfPoint((firma.Izq + firma.Der - ancho) / 2, firma.Arriba + 12);
        // Negro explícito: el texto va después del contenido de la página copiada y no debe depender
        // del color en que ese contenido haya terminado.
        p.SetTextAndFillColor(0, 0, 0);
        p.AddText(texto, tam, punto, fuente);
        p.ResetColor();
    }
}
