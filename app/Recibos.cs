using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
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

// YaEstaban: los recibos que ya tenían su archivo de antes. No se reemplazan. Combinados: los PDF con todos los recibos de
// un empleador que se armaron o se completaron.
public record Resultado(List<string> Creados, List<Nomina> Nominas, List<string> YaEstaban, List<string> Combinados);

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

    // La página «Ver Nómina» lleva «Contribuyente» en el encabezado, y la consulta de nómina que baja de AFIP dice
    // «NÓMINA», con acento. Ningún formato de recibo dice ninguna de las dos.
    static bool EsNomina(IReadOnlyList<Palabra> ws) =>
        ws.Any(w => Igual(w.Texto, "Contribuyente") || Clientes.Normal(w.Texto).Contains("NOMINA"));

    /// <summary>Período y empleados de una hoja de la nómina: una columna por empleado, con su CUIL y su nombre.</summary>
    public static (int Mes, string Anio, List<Empleado> Empleados) LeerNomina(IReadOnlyList<Palabra> ws, Action<string> avisar)
    {
        var m = Periodo.Match(string.Join(" ", ws.Select(w => w.Texto)));
        if (!m.Success) throw new FormatException("no encontré el período de la nómina");
        // La fila de los empleados; el CUIT del empleador está en el encabezado y así queda afuera.
        var rotulo = ws.Where(w => w.Texto.StartsWith("CUIL", StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.Izq).FirstOrDefault()
                     ?? throw new FormatException("no encontré la fila CUIL de la nómina");
        var columnas = new List<(double X, double Centro, string Cuil)>();
        foreach (var w in ws.Where(w => Math.Abs(w.CentroY - rotulo.CentroY) < 4 && w.Izq >= rotulo.Izq - 1))
        {
            var leidos = CuilRegex.Matches(w.Texto).Where(c => CuilValido(c.Value)).Select(c => (c.Index, c.Length, Cuil: Cuils(c.Value).First())).ToList();
            // La consulta de AFIP escribe los CUIL sin guiones: once dígitos. Solo acá, en la fila del CUIL; en el
            // resto del texto, once dígitos sueltos pueden ser cualquier otra cosa.
            if (leidos.Count == 0 && Regex.IsMatch(w.Texto, @"^\d{11}$") && CuilValido(w.Texto))
                leidos.Add((0, 11, $"{w.Texto[..2]}-{w.Texto[2..10]}-{w.Texto[10]}"));
            // El OCR a veces pega el rótulo al número («CUIL20-...»): la columna empieza donde empieza el número.
            double Punto(int i) => w.Izq + (w.Der - w.Izq) * i / w.Texto.Length;
            columnas.AddRange(leidos.Select(c => (Punto(c.Index), (Punto(c.Index) + Punto(c.Index + c.Length)) / 2, c.Cuil)));
            if (leidos.Count == 0 && w.Texto.Count(char.IsDigit) >= 9) avisar($"no pude leer bien el CUIL «{w.Texto}» de la nómina");
        }
        columnas.Sort();

        // Los nombres van debajo, hasta la fila «Obra Social».
        var piso = ws.Where(w => w.Texto.StartsWith("Obra", StringComparison.OrdinalIgnoreCase) && w.CentroY < rotulo.CentroY)
                     .Select(w => w.Arriba).DefaultIfEmpty(rotulo.Abajo - 40).Max();
        // Ancho de columna: la última hoja termina con «Suma Totales», que no es de ningún empleado.
        var paso = columnas.Count > 1 ? (columnas[^1].Centro - columnas[0].Centro) / (columnas.Count - 1) : 150;
        var nombres = columnas.Select(_ => new List<Palabra>()).ToList();
        foreach (var w in ws.Where(w => w.CentroY < rotulo.Abajo && w.CentroY > piso))
        {
            var texto = w;
            // El rótulo «Apellido y Nombre» termina justo donde empieza el primer nombre y el OCR los pega
            // («NombrePEREZ»): se le saca el rótulo.
            if (columnas.Count > 0 && w.Izq < columnas[0].X - 3 && w.Der > columnas[0].X && w.Texto.StartsWith("Nombre", StringComparison.OrdinalIgnoreCase))
                texto = w with { Texto = w.Texto[6..], Izq = columnas[0].X };
            if (texto.Texto.Length == 0 || columnas.Count == 0) continue;
            // ponytail-keep: por el centro y no por el borde izquierdo. La página «Ver Nómina» alinea el nombre a la
            // izquierda del CUIL, pero la consulta de AFIP lo centra: un nombre más ancho que el CUIL empieza antes
            // que su columna, y por el borde caía en la del vecino. Más lejos que media columna es otra cosa
            // («Suma Totales», el rótulo de la fila).
            var centro = (texto.Izq + texto.Der) / 2;
            var i = Enumerable.Range(0, columnas.Count).MinBy(k => Math.Abs(columnas[k].Centro - centro));
            if (Math.Abs(columnas[i].Centro - centro) < paso / 2) nombres[i].Add(texto);
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

    /// <summary>Cada empleado de la nómina con su recibo de ese período, o null si no está en ninguna carpeta. PorNombre: ese
    /// recibo lo separó la app encontrándolo por el nombre, porque su CUIL no se leía; hay que mirarlo.</summary>
    public static List<(Empleado Empleado, string? Recibo, bool PorNombre)> Revisar(Nomina n, IEnumerable<string> carpetas)
    {
        var recibos = new Dictionary<string, (string Archivo, bool PorNombre)>(); // CUIL -> archivo
        foreach (var carpeta in carpetas.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Directory.GetFiles(carpeta, "*.pdf"))
            {
                try
                {
                    using var d = PdfDocument.Open(f);
                    // Un recibo ocupa una o dos hojas: los PDF más largos (el de todos juntos, o cualquier otro
                    // documento de la carpeta) no se leen, así revisar una carpeta como Descargas no tarda.
                    if (d.NumberOfPages > 3) continue;
                    var hojas = string.Join(" ", d.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
                    var texto = d.Information.Keywords + " " + hojas;
                    // Del período por el nombre que le pone la app o por el contenido, para los recibos que tienen
                    // otro nombre. Periodo descarta las fechas: la de pago del recibo de junio (05/07/2026) no
                    // lo vuelve de julio.
                    var delPeriodo = f.EndsWith(Sufijo(n), StringComparison.OrdinalIgnoreCase)
                        || Periodo.Matches(texto).Any(m => int.Parse(m.Groups[1].Value) == n.Mes && m.Groups[2].Value == n.Anio);
                    // De quién es: el que separó la app lo dice en sus palabras clave. Su hoja puede traer también el CUIL de
                    // otro: en un escaneo de dos recibos por hoja, cada uno tiene la hoja entera, y por el texto el otro
                    // quedaba con este archivo (en uno real de octubre 2026, tres salían faltando y uno, repetido).
                    var claves = Cuils(d.Information.Keywords ?? "").ToHashSet();
                    var cs = claves.Count > 0 ? claves : Cuils(texto).ToHashSet();
                    // Como en Clientes: con la marca «por nombre», el CUIL que está en las palabras clave y no en la hoja lo
                    // puso la app, no se leyó. Si otro archivo trae el suyo leído, vale ese.
                    var supuestos = d.Information.Keywords?.Contains("por nombre") == true ? cs.Except(Cuils(hojas)).ToHashSet() : [];
                    // Un recibo separado tiene a lo sumo dos números: el CUIL del empleado y el CUIT del empleador.
                    // Uno con más es el PDF de todos juntos y no prueba que el recibo de cada uno se haya separado.
                    if (delPeriodo && cs.Count <= 2)
                        foreach (var c in cs)
                            if (!recibos.TryGetValue(c, out var ya) || ya.PorNombre && !supuestos.Contains(c)) recibos[c] = (f, supuestos.Contains(c));
                }
                catch { } // un PDF dañado o ajeno no frena la revisión
            }
        return n.Empleados.Select(e => recibos.TryGetValue(e.Cuil, out var r) ? (e, r.Archivo, r.PorNombre) : (e, (string?)null, false)).ToList();
    }

    public static string SinComa(string nombre) => Regex.Replace(nombre.Replace(",", " "), @"\s+", " ").Trim().ToUpperInvariant();

    // "PEREZ, JUAN CARLOS" -> "P.J.C.": en el orden del recibo, apellidos y después nombres.
    public static string Iniciales(string nombre) =>
        string.Concat(SinComa(nombre).Split(' ').Where(p => p.Length > 0 && p.All(char.IsLetter)).Select(p => p[0] + "."));

    public static string NombreArchivo(string nombre, int mes, string anio) =>
        $"{Limpiar(SinComa(nombre))} {mes:00} {anio}.pdf";

    // Los caracteres que Windows no acepta en un nombre de archivo.
    static string Limpiar(string s) => string.Concat(s.Where(c => !"\\/:*?\"<>|".Contains(c) && !char.IsControl(c)));

    internal static IReadOnlyList<Palabra> Palabras(UglyToad.PdfPig.Content.Page p) =>
        p.GetWords().Select(w => new Palabra(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToList();

    // Los recibos que genera el sistema traen texto; solo los escaneados pasan por OCR.
    static async Task<IReadOnlyList<Palabra>> PalabrasDe(UglyToad.PdfPig.Content.Page page, string ruta, Ocr? ocr, Action<string> avisar)
    {
        if (page.Letters.Count > 0) return Palabras(page);
        if (ocr is null) throw new FormatException("es una imagen y no hay OCR");
        avisar($"leyendo {Path.GetFileName(ruta)}, hoja {page.Number} con OCR…");
        return await ocr(ruta, page.Number);
    }

    /// <summary>Solo la nómina de un PDF, sin separar ni escribir nada: la revisión de clientes no escribe nada
    /// hasta que le dicen. null si ninguna hoja es de una nómina.</summary>
    public static async Task<Nomina?> LeerNominaPdf(string ruta, Ocr? ocr, Action<string> avisar)
    {
        using var doc = PdfDocument.Open(ruta);
        Nomina? nomina = null;
        foreach (var page in doc.GetPages())
        {
            var donde = $"{Path.GetFileName(ruta)}, hoja {page.Number}";
            try
            {
                var ws = await PalabrasDe(page, ruta, ocr, avisar);
                if (!EsNomina(ws)) continue;
                var (mes, anio, empleados) = LeerNomina(ws, msg => avisar($"⚠ {donde}: {msg}"));
                var n = nomina ??= new Nomina(ruta, mes, anio, []);
                n.Empleados.AddRange(empleados.Where(e => n.Empleados.All(x => x.Cuil != e.Cuil)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                avisar($"⚠ {donde}: {e.Message}");
            }
        }
        return nomina;
    }

    /// <summary>Separa los recibos y lee las nóminas. Sin carpetaSalida, cada recibo va junto a su PDF de origen.
    /// Las iniciales en la firma son a pedido: son un parche para los recibos que vuelven sin firmar. Nunca pisa un
    /// archivo: el recibo que ya estaba separado queda como está. «permiso» recibe los recibos y los combinados que se van a
    /// escribir, antes de escribir nada: si dice que no, no se guarda ninguno. «conocida» es la nómina que ya estaba cargada.
    /// En Nominas vuelven las que vinieron en la tanda y las que encontró en la carpeta del mes.</summary>
    public static async Task<Resultado> Procesar(IEnumerable<string> pdfs, Ocr? ocr, Action<string> avisar, string? carpetaSalida = null,
        bool iniciales = true, Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<bool>>? permiso = null, Nomina? conocida = null)
    {
        // Por cada nombre, una lista: casi siempre de uno solo, y de dos si hay dos empleados que se llaman igual. PorNombre: los
        // CUIL que no se leyeron en ninguna de sus hojas y se encontraron por el nombre; ese recibo queda para revisar.
        var grupos = new Dictionary<(string Carpeta, string Empleador, string Nombre, int Mes, string Anio),
            List<(List<(PdfDocument Doc, int Hoja, Palabra? Firma)> Hojas, HashSet<string> Cuils, HashSet<string> PorNombre)>>();
        var docs = new List<PdfDocument>();
        var creados = new List<string>();
        var yaEstaban = new List<string>();
        var nominas = new List<Nomina>();
        var halladas = new List<Nomina>();
        var hechos = new List<string>();
        // Las hojas que se leyeron como recibo, y las que no (sin el rótulo del nombre o sin el período): esas todavía pueden
        // ser de alguien de la nómina, y se ven cuando ya se sabe cuál es.
        var leidas = new List<(PdfDocument Doc, string Ruta, int Numero, string Carpeta, Hoja Recibo, string Texto)>();
        var sinLeer = new List<(PdfDocument Doc, string Ruta, int Numero, string Carpeta, string Texto, string Error)>();
        // Las hojas de un empleado, como en su archivo: con sus iniciales si se eligió estamparlas.
        void Copiar(PdfDocumentBuilder b, PdfDocumentBuilder.AddedFont? fuente, string nombre, List<(PdfDocument Doc, int Hoja, Palabra? Firma)> hojas,
            Action<PdfPageBuilder>? ademas = null, bool avisarSinFirma = true)
        {
            foreach (var (doc, n, firma) in hojas)
            {
                var p = b.AddPage(doc, n);
                if (fuente is not null)
                {
                    if (firma is not null) Estampar(p, fuente, firma, Iniciales(nombre));
                    else if (avisarSinFirma) avisar($"⚠ {nombre}: no encontré el espacio de firma, va sin iniciales");
                }
                ademas?.Invoke(p);
            }
        }
        // Dos empleados con el mismo nombre (un padre y su hijo) no son el mismo: antes salían juntos en un solo PDF. Los
        // distingue el CUIL. La hoja va con el último del mismo nombre cuyos números no contradicen los suyos: una segunda hoja
        // con un número mal leído, o sin ninguno, sigue con la primera; otra con el mismo CUIT del empleador y otro CUIL es de
        // otra persona.
        void Agregar((string, string, string, int, string) clave, (PdfDocument, int, Palabra?) hoja, IReadOnlyCollection<string> leidos,
            IReadOnlyCollection<string> porNombre)
        {
            var cuils = leidos.Concat(porNombre).ToHashSet();
            if (!grupos.TryGetValue(clave, out var tocayos)) grupos[clave] = tocayos = [];
            var i = tocayos.FindLastIndex(t => t.Cuils.IsSubsetOf(cuils) || cuils.IsSubsetOf(t.Cuils));
            if (i < 0)
            {
                tocayos.Add(([], [], []));
                i = tocayos.Count - 1;
            }
            var (hojas, todos, supuestos) = tocayos[i];
            hojas.Add(hoja);
            // Lo que se leyó en alguna de sus hojas ya no es una suposición.
            supuestos.UnionWith(porNombre.Where(c => !todos.Contains(c)));
            supuestos.ExceptWith(leidos);
            todos.UnionWith(cuils);
        }
        try
        {
            foreach (var ruta in pdfs)
            {
                var doc = PdfDocument.Open(ruta);
                docs.Add(doc);
                var carpeta = carpetaSalida ?? Path.GetDirectoryName(Path.GetFullPath(ruta))!;
                Nomina? nomina = null;
                foreach (var page in doc.GetPages())
                {
                    var donde = $"{Path.GetFileName(ruta)}, hoja {page.Number}";
                    try
                    {
                        var ws = await PalabrasDe(page, ruta, ocr, avisar);
                        if (EsNomina(ws))
                        {
                            var (mes, anio, empleados) = LeerNomina(ws, msg => avisar($"⚠ {donde}: {msg}"));
                            var n = nomina ??= new Nomina(ruta, mes, anio, []);
                            n.Empleados.AddRange(empleados.Where(e => n.Empleados.All(x => x.Cuil != e.Cuil)));
                            continue;
                        }
                        var texto = string.Join(" ", ws.Select(w => w.Texto));
                        try { leidas.Add((doc, ruta, page.Number, carpeta, LeerHoja(ws, page.Width), texto)); }
                        catch (FormatException e) { sinLeer.Add((doc, ruta, page.Number, carpeta, texto, e.Message)); }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        avisar($"⚠ {donde}: {e.Message}");
                    }
                }
                if (nomina is not null) nominas.Add(nomina);
            }

            // La nómina de cada hoja, para encontrar a quien la hoja no nombra bien: la que vino en la tanda; si no, la que ya
            // estaba cargada, si trae a alguien de estos recibos (la de otro cliente del mismo mes no sirve); y si no, la de la
            // carpeta del mes del PDF, como la busca Clientes.
            var textos = leidas.Select(l => l.Texto).Concat(sinLeer.Select(s => s.Texto)).ToList();
            var conocidaSirve = conocida is not null && textos.Any(t => Clientes.DeLaNomina(t, conocida.Empleados.Select(e => e.Cuil)).Count > 0);
            var delMes = new Dictionary<(string Carpeta, int Mes, string Anio), Nomina?>();
            async Task<IReadOnlyList<Empleado>?> NominaDe(string ruta, (int Mes, string Anio) p)
            {
                var traidas = nominas.Where(n => (n.Mes, n.Anio) == p).ToList();
                if (traidas.Count > 0) return traidas.SelectMany(n => n.Empleados).DistinctBy(e => e.Cuil).ToList();
                if (conocidaSirve && (conocida!.Mes, conocida.Anio) == p) return conocida.Empleados;
                if (CarpetaDelMes(ruta, p) is not { } carpeta) return null;
                if (!delMes.TryGetValue((carpeta, p.Mes, p.Anio), out var hallada))
                {
                    (hallada, var avisos) = await Clientes.NominaDelMes(carpeta, p.Mes, int.Parse(p.Anio), ocr, avisar, CancellationToken.None);
                    delMes[(carpeta, p.Mes, p.Anio)] = hallada;
                    if (hallada is not null)
                    {
                        halladas.Add(hallada);
                        foreach (var a in avisos) avisar($"⚠ {a}");
                    }
                }
                return hallada?.Empleados;
            }
            // Cómo escriben a cada uno los recibos sin firmar de la carpeta del mes, leídos una vez por carpeta.
            var sinFirmar = new Dictionary<string, Dictionary<string, Hoja>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Hoja> RecibosDe(string ruta, (int Mes, string Anio) p, IReadOnlyList<Empleado> nomina)
            {
                if (CarpetaDelMes(ruta, p) is not { } carpeta) return [];
                if (!sinFirmar.TryGetValue(carpeta, out var hs)) sinFirmar[carpeta] = hs = Clientes.RecibosSinFirmar(carpeta, nomina);
                return hs;
            }

            foreach (var (doc, ruta, numero, carpeta, recibo, texto) in leidas)
            {
                var leidos = Cuils(texto).ToHashSet();
                List<string> porNombre = [];
                if (await NominaDe(ruta, (recibo.Mes, recibo.Anio)) is { } nomina)
                {
                    // El CUIL como lo haya leído el OCR (sin guiones, con puntos, una O por un cero), como en Clientes.
                    var porCuil = Clientes.DeLaNomina(texto, nomina.Select(e => e.Cuil));
                    leidos.UnionWith(porCuil);
                    // Si no está, el nombre del recibo. Solo si no se leyó su CUIL: con dos números (el suyo y el del empleador)
                    // es alguien que no está en la nómina, y el que en ella se llama casi igual no es él.
                    if (porCuil.Count == 0 && leidos.Count < 2
                        && (Clientes.Nombrados(recibo.Nombre, nomina, [], "") is [var quien] ? quien : Clientes.PorCuilParecido(texto, nomina)) is { } q)
                        porNombre.Add(q.Cuil);
                }
                Agregar((carpeta, recibo.Empleador, recibo.Nombre, recibo.Mes, recibo.Anio), (doc, numero, recibo.Firma), leidos, porNombre);
            }

            foreach (var (doc, ruta, numero, carpeta, texto, error) in sinLeer)
            {
                // El período, de las otras hojas del mismo PDF. Si no se leyó ninguna, el de la hoja misma, y si tampoco se lee,
                // el de la carpeta del mes donde está, como en Clientes.
                var hermana = leidas.FirstOrDefault(l => l.Doc == doc).Recibo;
                IReadOnlyList<Empleado>? nomina = null;
                (int Mes, string Anio) periodo = default;
                foreach (var p in hermana is null ? Periodos(texto).Concat(MesesArriba(ruta).Take(1).Select(x => (x.Mes, x.Anio))) : [(hermana.Mes, hermana.Anio)])
                    if ((nomina = await NominaDe(ruta, p)) is not null)
                    {
                        periodo = p;
                        break;
                    }
                var quienes = new List<(Empleado Empleado, bool PorNombre)>();
                if (nomina is not null && !Clientes.EsConstancia(texto))
                {
                    // El empleador, como en las otras hojas; si no hay ninguna, como en los recibos sin firmar del mes. Y cada uno con
                    // su nombre como lo escribe su recibo: la nómina de AFIP a veces pone el nombre primero.
                    var comoEscriben = RecibosDe(ruta, periodo, nomina);
                    var empleador = hermana?.Empleador
                        ?? leidas.FirstOrDefault(l => l.Carpeta == carpeta && (l.Recibo.Mes, l.Recibo.Anio) == periodo).Recibo?.Empleador
                        ?? comoEscriben.Values.FirstOrDefault()?.Empleador ?? "EMPLEADOR";
                    var porCuil = Clientes.DeLaNomina(texto, nomina.Select(e => e.Cuil));
                    quienes.AddRange(nomina.Where(e => porCuil.Contains(e.Cuil)).Select(e => (e, false)));
                    // Una hoja puede traer dos recibos, escaneados de a dos: la tiene cada uno.
                    quienes.AddRange(Clientes.QuienesSinCuil(texto, ruta, doc.NumberOfPages, nomina, porCuil, empleador).Select(e => (e, true)));
                    foreach (var (e, supuesto) in quienes)
                    {
                        // Con el que ya tiene su recibo en esta tanda (es otra hoja suya); si no, con su nombre.
                        var clave = grupos.Where(g => g.Key.Carpeta == carpeta && (g.Key.Mes, g.Key.Anio) == periodo && g.Value.Any(t => t.Cuils.Contains(e.Cuil)))
                            .Select(g => ((string, string, string, int, string)?)g.Key).FirstOrDefault()
                            ?? (carpeta, empleador, SinComa(comoEscriben.GetValueOrDefault(e.Cuil)?.Nombre ?? e.Nombre), periodo.Mes, periodo.Anio);
                        string[] suyo = [e.Cuil];
                        Agregar(clave, (doc, numero, null), supuesto ? [] : suyo, supuesto ? suyo : []);
                    }
                }
                if (quienes.Count == 0) avisar($"⚠ {Path.GetFileName(ruta)}, hoja {numero}: {error}");
            }
            // Las hojas de cada uno como vinieron: la que se encontró recién con la nómina puede ser la primera de su recibo.
            foreach (var t in grupos.Values.SelectMany(x => x))
                t.Hojas.Sort((a, b) => (docs.IndexOf(a.Doc), a.Hoja).CompareTo((docs.IndexOf(b.Doc), b.Hoja)));

            // Primero, qué archivo le toca a cada uno; se escribe después, cuando ya se sabe todo lo que hay para guardar
            // y se pudo preguntar.
            var pendientes = new List<(string Ruta, Func<Task> Escribir)>();
            foreach (var ((carpeta, empleador, nombre, mes, anio), tocayos) in grupos)
                foreach (var (hojas, cuils, porNombre) in tocayos)
                {
                    // Con un tocayo, el archivo lleva además su CUIL: de sus números, el que no tiene el otro (el CUIT
                    // del empleador lo tienen los dos).
                    var propio = tocayos.Count == 1 ? "" : string.Concat(cuils.Where(c => tocayos.Count(t => t.Cuils.Contains(c)) == 1).Select(c => " " + c));
                    var destino = Path.Combine(carpeta, Limpiar(SinComa(empleador)));
                    var salida = Path.Combine(destino, NombreArchivo(nombre + propio, mes, anio));
                    if (File.Exists(salida))
                    {
                        yaEstaban.Add(salida);
                        continue;
                    }
                    pendientes.Add((salida, async () =>
                    {
                        var builder = new PdfDocumentBuilder();
                        // El CUIL y el período van también en las palabras clave: un recibo escaneado no tiene texto,
                        // y la revisión de la nómina los busca ahí. «iniciales» marca los que llevan las del estudio:
                        // la revisión de clientes no los cuenta como firmados por el empleado. «por nombre», los que se
                        // encontraron por el nombre: las dos revisiones los muestran para mirar, como hace Clientes con los suyos.
                        builder.DocumentInformation.Keywords = string.Join(" ", cuils) + $" {mes:00}/{anio}" + (iniciales ? " iniciales" : "")
                            + (porNombre.Count > 0 ? " por nombre" : "");
                        Copiar(builder, iniciales ? builder.AddTrueTypeFont(Manuscrita.Value) : null, nombre, hojas);
                        Directory.CreateDirectory(destino);
                        await GuardarNuevo(salida, builder.Build());
                        creados.Add(salida);
                        avisar($"✓ {Path.GetFileName(destino)}\\{Path.GetFileName(salida)}");
                    }));
                }

            // El combinado de cada empleador y mes, al lado de los separados: «EMPLEADOR RECIBOS FIRMADOS 10 2026.pdf» (sin
            // «FIRMADOS» si no llevan iniciales), con todos los recibos en el orden en que vinieron. Como el consolidado de
            // los clientes: si ya está con todos, no se toca; si le falta alguien, se le suma al final, sin cambiar lo que
            // tenía. Cada uno se reconoce por sus números (el CUIL y el CUIT), que van escritos en sus hojas.
            var combinados = new List<(string Ruta, Func<Task> Escribir)>();
            foreach (var g in grupos.GroupBy(kv => (kv.Key.Carpeta, kv.Key.Empleador, kv.Key.Mes, kv.Key.Anio)))
            {
                var (carpeta, empleador, mes, anio) = g.Key;
                var destino = Path.Combine(carpeta, Limpiar(SinComa(empleador)));
                var ruta = Path.Combine(destino, NombreArchivo($"{empleador} RECIBOS{(iniciales ? " FIRMADOS" : "")}", mes, anio));
                HashSet<string>? estan = null;
                if (File.Exists(ruta))
                    try
                    {
                        using var d = PdfDocument.Open(ruta);
                        estan = Cuils(d.Information.Keywords + " " + string.Join(" ", d.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text))).ToHashSet();
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        avisar($"⚠ {Path.GetFileName(ruta)}: no lo pude abrir, así que no lo completé ({e.Message})");
                        continue;
                    }
                var faltan = g.SelectMany(kv => kv.Value.Select(t => (kv.Key.Nombre, t.Hojas, t.Cuils, t.PorNombre)))
                    .Where(t => estan is null || !t.Cuils.IsSubsetOf(estan))
                    .OrderBy(t => docs.IndexOf(t.Hojas[0].Doc)).ThenBy(t => t.Hojas[0].Hoja).ToList();
                if (faltan.Count == 0) continue;
                combinados.Add((ruta, async () =>
                {
                    var b = new PdfDocumentBuilder();
                    // Sin los CUIL: en las palabras clave valdrían para todas las hojas.
                    b.DocumentInformation.Keywords = $"{mes:00}/{anio} combinado" + (iniciales ? " iniciales" : "");
                    var letra = b.AddStandard14Font(Standard14Font.Helvetica);
                    var fuente = iniciales ? b.AddTrueTypeFont(Manuscrita.Value) : null;
                    Directory.CreateDirectory(destino);
                    await Juntar(ruta, b, null, () =>
                    {
                        // Quién está en cada hoja; los encontrados por el nombre, con «POR NOMBRE», como en el consolidado. Una hoja
                        // con dos recibos va una sola vez, con los dos.
                        var quienes = faltan.SelectMany(t => t.Hojas.Select(h => (Clave: (h.Doc, h.Hoja),
                                Texto: string.Join(" ", t.Cuils.Select(c => $"{(t.PorNombre.Contains(c) ? "POR NOMBRE" : "CUIL")} {c}")))))
                            .GroupBy(x => x.Clave).ToDictionary(k => k.Key, k => string.Join(" ", k.Select(x => x.Texto).Distinct()));
                        var puestas = new HashSet<(PdfDocument, int)>();
                        foreach (var (nombre, hojas, _, _) in faltan)
                            foreach (var h in hojas.Where(x => puestas.Add((x.Doc, x.Hoja))))
                                Copiar(b, fuente, nombre, [h], p => Invisible(p, letra, quienes[(h.Doc, h.Hoja)]), avisarSinFirma: false);
                        return Task.FromResult(puestas.Count);
                    });
                    hechos.Add(ruta);
                    avisar($"▤ {Path.GetFileName(destino)}\\{Path.GetFileName(ruta)}");
                }));
            }

            if (pendientes.Count + combinados.Count > 0 && permiso is not null
                && !await permiso(pendientes.Select(p => p.Ruta).ToList(), combinados.Select(c => c.Ruta).ToList()))
            {
                pendientes.Clear();
                combinados.Clear();
            }
            // Los recibos primero y el combinado al final: llega último, con todos.
            foreach (var (_, escribir) in pendientes.Concat(combinados)) await escribir();
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
        return new Resultado(creados, [.. nominas, .. halladas], yaEstaban, hechos);
    }

    static IEnumerable<(int Mes, string Anio)> Periodos(string texto) =>
        Periodo.Matches(texto).Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value));

    // Las carpetas de un mes que tienen adentro al PDF, como las de los clientes («PEREZ SUELDOS OCTUBRE 2026», «10 2026»), con
    // su mes, de la más cercana para arriba. La de los firmados no cuenta aunque lo nombre («RECIBOS FIRMADOS 10 2026»): la
    // nómina y los recibos sin firmar están en la de arriba. El año, sin mirar lo que sigue: hay una carpeta real que dice 20266.
    static IEnumerable<(string Carpeta, int Mes, string Anio)> MesesArriba(string ruta)
    {
        for (var c = Path.GetDirectoryName(Path.GetFullPath(ruta)); !string.IsNullOrEmpty(c); c = Path.GetDirectoryName(c))
        {
            var nombre = Path.GetFileName(c);
            if (Clientes.EsDeFirmados(c)) continue;
            foreach (var anio in Regex.Matches(nombre, @"(?<!\d)(?:19|20)\d{2}").Select(m => int.Parse(m.Value)).Distinct())
                for (var mes = 1; mes <= 12; mes++)
                    if (Clientes.EsDelMes(nombre, mes, anio)) yield return (c, mes, anio.ToString());
        }
    }

    static string? CarpetaDelMes(string ruta, (int Mes, string Anio) p) =>
        MesesArriba(ruta).Where(x => (x.Mes, x.Anio) == p).Select(x => x.Carpeta).FirstOrDefault();

    /// <summary>Un PDF que junta recibos: nuevo, o el que ya estaba con lo que le falta al final. Las hojas que tenía quedan
    /// como estaban (cada una pasa por «vieja», por si hay que escribirle quién está). «agregar» suma lo nuevo al final y dice
    /// cuántas hojas son. Se arma aparte y recién entero ocupa el lugar del anterior: si algo falla, queda el que había.</summary>
    internal static async Task Juntar(string ruta, PdfDocumentBuilder b, Action<PdfPageBuilder, int>? vieja, Func<Task<int>> agregar)
    {
        var existe = File.Exists(ruta);
        var anterior = existe ? PdfDocument.Open(ruta) : null;
        byte[] pdf;
        try
        {
            var antes = anterior?.NumberOfPages ?? 0;
            for (var i = 1; i <= antes; i++)
            {
                var p = b.AddPage(anterior!, i);
                vieja?.Invoke(p, i);
            }
            var nuevas = await agregar();
            pdf = b.Build();
            using var hecho = PdfDocument.Open(pdf);
            if (hecho.NumberOfPages != antes + nuevas) throw new IOException($"{Path.GetFileName(ruta)} no salió entero");
        }
        finally
        {
            // Cerrado antes de reemplazarlo: es uno de los que se copian.
            anterior?.Dispose();
        }
        if (!existe)
        {
            await GuardarNuevo(ruta, pdf);
            return;
        }
        var aparte = ruta + ".nuevo";
        // Pisando el que haya: es el temporal de la app, no un archivo del cliente. Con GuardarNuevo, el «.nuevo» que dejaba
        // un corte de red o un apagón a mitad de escribir hacía fallar cada intento siguiente con «el archivo ya existe».
        await File.WriteAllBytesAsync(aparte, pdf);
        try { File.Move(aparte, ruta, overwrite: true); }
        catch
        {
            File.Delete(aparte);
            throw;
        }
    }

    /// <summary>Quién está en la hoja, en texto que no se ve pero se lee: la revisión siguiente lo encuentra sin OCR, y con
    /// Ctrl+F se busca a cualquiera.</summary>
    internal static void Invisible(PdfPageBuilder p, PdfDocumentBuilder.AddedFont fuente, string texto)
    {
        if (texto.Length == 0) return;
        p.SetTextRenderingMode(TextRenderingMode.Neither);
        p.AddText(texto, 4, new PdfPoint(20, 20), fuente);
        // El modo del texto sigue valiendo para lo que se escriba después en la hoja (es del estado gráfico, no de este
        // texto): vuelve a «pintar», para que nada de lo que venga atrás quede invisible.
        p.SetTextRenderingMode(TextRenderingMode.Fill);
    }

    /// <summary>Escribe un archivo que no existía. Con CreateNew: si mientras tanto apareció uno con ese nombre, falla
    /// en vez de pisarlo.</summary>
    internal static async Task GuardarNuevo(string ruta, byte[] bytes)
    {
        var salida = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write);
        try
        {
            await using (salida) await salida.WriteAsync(bytes);
        }
        catch
        {
            // Lo único que la app borra: el archivo que acaba de crear ella misma (CreateNew garantiza que no
            // existía) y que quedó a medias porque se cortó la red. Un PDF roto parecería un recibo.
            try { File.Delete(ruta); } catch { }
            throw;
        }
    }

    // Segoe Print, la letra manuscrita que trae Windows: se toma de la PC y no viaja en el .exe,
    // porque es de Microsoft. Si faltara, queda Caveat embebida (licencia OFL), igual de sencilla.
    internal static readonly Lazy<byte[]> Manuscrita = new(() =>
    {
        var segoe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoepr.ttf");
        if (File.Exists(segoe)) return File.ReadAllBytes(segoe);
        using var s = typeof(Recibos).Assembly.GetManifestResourceStream("Caveat-Regular.ttf")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    internal static void Estampar(PdfPageBuilder p, PdfDocumentBuilder.AddedFont fuente, Palabra firma, string texto)
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
