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
    /// archivo: el recibo que ya estaba separado queda como está. «permiso» recibe lo que se va a escribir, antes de
    /// escribir nada: si dice que no, no se guarda ninguno.</summary>
    /// <summary>«permiso» recibe los recibos y los combinados que se van a escribir, y dice si se escriben.</summary>
    public static async Task<Resultado> Procesar(IEnumerable<string> pdfs, Ocr? ocr, Action<string> avisar, string? carpetaSalida = null,
        bool iniciales = true, Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<bool>>? permiso = null)
    {
        // Por cada nombre, una lista: casi siempre de uno solo, y de dos si hay dos empleados que se llaman igual.
        var grupos = new Dictionary<(string Carpeta, string Empleador, string Nombre, int Mes, string Anio), List<(List<(PdfDocument Doc, int Hoja, Palabra? Firma)> Hojas, HashSet<string> Cuils)>>();
        var docs = new List<PdfDocument>();
        var creados = new List<string>();
        var yaEstaban = new List<string>();
        var nominas = new List<Nomina>();
        var hechos = new List<string>();
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
                        var ws = await PalabrasDe(page, ruta, ocr, avisar);
                        if (EsNomina(ws))
                        {
                            var (mes, anio, empleados) = LeerNomina(ws, msg => avisar($"⚠ {donde}: {msg}"));
                            var n = nomina ??= new Nomina(ruta, mes, anio, []);
                            n.Empleados.AddRange(empleados.Where(e => n.Empleados.All(x => x.Cuil != e.Cuil)));
                            continue;
                        }

                        var h = LeerHoja(ws, page.Width);
                        var clave = (carpetaSalida ?? Path.GetDirectoryName(Path.GetFullPath(ruta))!, h.Empleador, h.Nombre, h.Mes, h.Anio);
                        var cuils = Cuils(string.Join(" ", ws.Select(w => w.Texto))).ToHashSet();
                        if (!grupos.TryGetValue(clave, out var tocayos)) grupos[clave] = tocayos = [];
                        // Dos empleados con el mismo nombre (un padre y su hijo) no son el mismo: antes salían juntos en un
                        // solo PDF. Los distingue el CUIL. La hoja va con el último del mismo nombre cuyos números no
                        // contradicen los suyos: una segunda hoja con un número mal leído, o sin ninguno, sigue con la
                        // primera; otra con el mismo CUIT del empleador y otro CUIL es de otra persona.
                        var i = tocayos.FindLastIndex(t => t.Cuils.IsSubsetOf(cuils) || cuils.IsSubsetOf(t.Cuils));
                        if (i < 0)
                        {
                            tocayos.Add(([], []));
                            i = tocayos.Count - 1;
                        }
                        tocayos[i].Hojas.Add((doc, page.Number, h.Firma));
                        tocayos[i].Cuils.UnionWith(cuils);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        avisar($"⚠ {donde}: {e.Message}");
                    }
                }
                if (nomina is not null) nominas.Add(nomina);
            }

            // Primero, qué archivo le toca a cada uno; se escribe después, cuando ya se sabe todo lo que hay para guardar
            // y se pudo preguntar.
            var pendientes = new List<(string Ruta, Func<Task> Escribir)>();
            foreach (var ((carpeta, empleador, nombre, mes, anio), tocayos) in grupos)
                foreach (var (hojas, cuils) in tocayos)
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
                        // la revisión de clientes no los cuenta como firmados por el empleado.
                        builder.DocumentInformation.Keywords = string.Join(" ", cuils) + $" {mes:00}/{anio}" + (iniciales ? " iniciales" : "");
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
                var faltan = g.SelectMany(kv => kv.Value.Select(t => (kv.Key.Nombre, t.Hojas, t.Cuils)))
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
                        foreach (var (nombre, hojas, cuils) in faltan)
                            Copiar(b, fuente, nombre, hojas, p => Invisible(p, letra, string.Join(" ", cuils.Select(c => "CUIL " + c))), avisarSinFirma: false);
                        return Task.FromResult(faltan.Sum(t => t.Hojas.Count));
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
        return new Resultado(creados, nominas, yaEstaban, hechos);
    }

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
        await GuardarNuevo(aparte, pdf);
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
