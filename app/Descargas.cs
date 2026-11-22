using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// La carpeta de descargas: una carpeta de esta PC donde se bajan, como vengan, los recibos firmados de cualquier cliente
// (fotos de WhatsApp, PDF). La app los lee, encuentra de qué cliente, mes y empleado es cada uno por su CUIL en las nóminas
// de todos los clientes, y los guarda derechos y con su nombre en los firmados de su cliente y su mes. Los originales pasan a
// «Acomodados», adentro de la misma carpeta: nada se borra. Lo que no se pudo ubicar queda donde está.

/// <summary>Un recibo de la carpeta de descargas con adónde va: el cliente (su carpeta) y el PDF que se arma en su mes.</summary>
public record Acomodo(string Cliente, ASuMes Recibo);

/// <summary>Una hoja que no se pudo ubicar, y por qué.</summary>
public record SinUbicar(Hallazgo Hoja, string PorQue);

/// <summary>Lo que se encontró en la carpeta de descargas. Completos: los archivos con todas sus hojas ubicadas, y los
/// recibos (su ruta en Z) adonde van: cuando están todos guardados, el original pasa a «Acomodados». PorLeer: las fotos y
/// hojas escaneadas que esperan el OCR (en la pasada sin OCR).</summary>
public record Bandeja(string Carpeta, List<Acomodo> Acomodos, List<SinUbicar> SinUbicar, Dictionary<string, List<string>> Completos, int PorLeer,
    List<string> Avisos);

public static class Descargas
{
    public const string Acomodados = "Acomodados";

    // Las nóminas de todos los clientes, por mes, mientras la app está abierta: la pantalla revisa la carpeta cada pocos
    // segundos mientras lee las fotos, y recorrer cada vez las carpetas de 50 clientes en la red pesaría.
    static readonly ConcurrentDictionary<(string Raiz, int Mes, int Anio), List<(string Cliente, string CarpetaMes, Nomina Nomina)>> Indices = new();

    /// <summary>Olvida las nóminas leídas: «Buscar de nuevo» vuelve a recorrer las carpetas de los clientes (puede haber
    /// una carpeta o una nómina nueva).</summary>
    public static void Olvidar() => Indices.Clear();

    // Las nóminas de ese mes de todos los clientes de esa carpeta. Un cliente sin carpeta del mes o sin nómina no está.
    static async Task<List<(string Cliente, string CarpetaMes, Nomina Nomina)>> Indice(string raiz, int mes, int anio, Ocr? ocr, Action<string> avisar,
        CancellationToken corte)
    {
        if (Indices.TryGetValue((raiz, mes, anio), out var ya)) return ya;
        avisar($"Buscando las nóminas de {Clientes.NombreMes(mes).ToLowerInvariant()} de todos los clientes…");
        var lista = new List<(string, string, Nomina)>();
        foreach (var cliente in Clientes.ConSueldos(raiz))
        {
            corte.ThrowIfCancellationRequested();
            if (Clientes.CarpetaDelMes(cliente, mes, anio) is not { } carpetaMes) continue;
            try
            {
                if ((await Clientes.NominaDelMes(carpetaMes, mes, anio, ocr, _ => { }, corte)).Nomina is { } nomina) lista.Add((cliente, carpetaMes, nomina));
            }
            catch (Exception e) when (e is not OperationCanceledException) { } // su nómina no se pudo leer: ese cliente no cuenta
        }
        return Indices[(raiz, mes, anio)] = lista;
    }

    // Solo los de la carpeta misma: adentro está «Acomodados», y lo que haya en otras subcarpetas es de quien las armó.
    static List<FileInfo> Archivos(string carpeta)
    {
        try
        {
            return new DirectoryInfo(carpeta).EnumerateFiles()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && (Clientes.EsPdf(f) || Clientes.EsImagen(f)))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (IOException) { return []; } // la carpeta ya no está: como si estuviera vacía
    }

    /// <summary>El año de un recibo que se baja hoy: el más reciente de ese mes que no esté en el futuro (un recibo de
    /// diciembre bajado en noviembre es del año pasado).</summary>
    // El año que lee el OCR no se usa: en las fotos borrosas salió mal en 5 de 440 hojas, con el mes bien leído.
    public static int AnioDe(int mes, DateTime hoy) => mes <= hoy.Month ? hoy.Year : hoy.Year - 1;

    // Desde cuándo se cuenta el año de un recibo: hoy, o la fecha del archivo si es más vieja, porque un recibo no es
    // posterior a su archivo. Contando desde hoy, en una carpeta con cosas de otros años (la Descargas de Windows) un
    // recibo de septiembre de 2025 de alguien que sigue trabajando se guardaba en septiembre de 2026.
    static DateTime Desde(DateTime delArchivo, DateTime hoy) => delArchivo < hoy ? delArchivo : hoy;

    // La suma de los bytes de cada archivo, por ruta, tamaño y fecha: comparar es leerlos, y la carpeta se revisa cada pocos
    // segundos mientras se leen las fotos.
    static readonly ConcurrentDictionary<(string, long, DateTime), string> Sumas = new();
    static string Suma(FileInfo f) => Sumas.GetOrAdd((f.FullName.ToUpperInvariant(), f.Length, f.LastWriteTimeUtc), _ => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.FullName))));

    // Si ese mismo archivo (los mismos bytes, con cualquier nombre) ya está en la carpeta del mes del cliente: la foto bajada
    // de nuevo que ya se había guardado a mano, con su nombre de WhatsApp. Solo se leen los del mismo tamaño.
    static bool YaEstaEnElMes(string bajado, List<FileInfo> delMes)
    {
        var f = new FileInfo(bajado);
        return delMes.Any(m => m.Length == f.Length && Suma(m) == Suma(f));
    }

    // Un PDF que armó la app trae el CUIL en las palabras clave: sus hojas no pasan por el OCR.
    static bool TraeCuil(string texto) => Regex.IsMatch(texto, @"\b\d{2}-?\d{8}-?\d\b");

    // El recibo nombra al cliente: todas las palabras de su carpeta («PEREZ JUAN») están en el texto. Desempata cuando
    // el CUIL está en la nómina de dos clientes (alguien que trabaja para los dos).
    static bool NombraAlCliente(string texto, string cliente)
    {
        var palabras = Regex.Split(Clientes.Normal(texto), "[^A-Z]+").ToHashSet();
        var suyas = Regex.Split(Clientes.Normal(Path.GetFileName(cliente)), "[^A-Z]+").Where(p => p.Length >= 3).ToList();
        return suyas.Count > 0 && suyas.All(palabras.Contains);
    }

    /// <summary>Lee la carpeta y ubica cada hoja. «raizDelAnio»: la carpeta de los clientes de ese año (null si no está).
    /// Sin «leer» es la pasada rápida: solo lo que ya está en la memoria y los PDF con texto.</summary>
    public static async Task<Bandeja> Revisar(string carpeta, Func<int, string?> raizDelAnio, DateTime hoy, Ocr? ocr, LeerImagen? leer,
        Memoria memoria, Action<string> avisar, CancellationToken corte, SacarHuella? huella = null)
    {
        var archivos = Archivos(carpeta);
        var porLeer = 0;
        var unaVez = huella is null ? null : Clientes.UnaVez(huella, memoria, carpeta, corte);
        var leidos = new Memoria.Entrada?[archivos.Count];
        var avisos = new ConcurrentQueue<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, archivos.Count), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = corte },
            async (i, _) =>
            {
                try { leidos[i] = await Clientes.Leer(archivos[i], true, leer, memoria, TraeCuil, m => avisar($"Leyendo {m}"), corte, unaVez, () => Interlocked.Increment(ref porLeer)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { avisos.Enqueue($"{archivos[i].Name}: {ex.Message}"); }
            });

        var asignadas = new List<(Hallazgo Hoja, string Cliente, string CarpetaMes, Nomina Nomina, Empleado Empleado, int Mes, int Anio)>();
        var sinUbicar = new List<SinUbicar>();
        var hojas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, e) in archivos.Zip(leidos))
        {
            if (e is null) continue; // una foto que todavía no se leyó, o un archivo que no se pudo abrir
            hojas[f.FullName] = e.Hojas.Count;
            var desde = Desde(f.LastWriteTime, hoy);
            for (var i = 0; i < e.Hojas.Count; i++)
            {
                var h = new Hallazgo(f.FullName, Clientes.EsPdf(f) ? i + 1 : 0, e.Hojas[i]);
                var texto = h.Lectura.Texto;
                // Una hoja escaneada sin leer todavía, en la pasada sin OCR: ya se contó entre las que faltan leer.
                if (texto.Length == 0 && leer is null) continue;
                if (Clientes.EsConstancia(texto))
                {
                    sinUbicar.Add(new(h, "es una constancia de ARCA, no un recibo"));
                    continue;
                }
                if (Clientes.MesDelRecibo(texto) is not { } mes)
                {
                    sinUbicar.Add(new(h, texto.Trim().Length == 0 ? "no se pudo leer" : "no dice de qué mes es"));
                    continue;
                }
                var anio = AnioDe(mes, desde);
                var indice = raizDelAnio(anio) is { } raiz ? await Indice(raiz, mes, anio, ocr, avisar, corte) : [];
                // Solo por el CUIL: entre todos los clientes, un nombre puede estar en más de una nómina.
                var deQuien = indice.SelectMany(c => Clientes.DeLaNomina(e.Claves + " " + texto, c.Nomina.Empleados.Select(x => x.Cuil))
                    .Select(cuil => (Cliente: c, Empleado: c.Nomina.Empleados.First(x => x.Cuil == cuil)))).ToList();
                if (deQuien.Select(d => d.Cliente.Cliente).Distinct().Count() > 1
                    && deQuien.Select(d => d.Cliente.Cliente).Distinct().Where(c => NombraAlCliente(texto, c)).ToList() is [var elQueNombra])
                    deQuien = deQuien.Where(d => d.Cliente.Cliente == elQueNombra).ToList();
                var mesNombre = Clientes.NombreMes(mes).ToLowerInvariant();
                if (indice.Count == 0) sinUbicar.Add(new(h, $"no encontré las carpetas de {mesNombre} {anio} de los clientes"));
                else if (deQuien.Count == 0) sinUbicar.Add(new(h, $"no encontré su CUIL en las nóminas de {mesNombre}"));
                else if (deQuien.Select(d => d.Cliente.Cliente).Distinct().Count() > 1) sinUbicar.Add(new(h, "su CUIL está en la nómina de más de un cliente"));
                else foreach (var d in deQuien) asignadas.Add((h, d.Cliente.Cliente, d.Cliente.CarpetaMes, d.Cliente.Nomina, d.Empleado, mes, anio));
            }
        }

        if (huella is not null) await Clientes.CompletarHuellas(archivos, leidos, memoria, huella, corte);
        // Un PDF por empleado, cliente y mes, con el nombre que le pone la revisión de ese mes (el de sus recibos sin firmar).
        var acomodos = new List<Acomodo>();
        var adonde = new Dictionary<(string, int, int, string), string>(); // (cliente, mes, año, CUIL) → la ruta en Z
        foreach (var g in asignadas.GroupBy(a => (a.Cliente, a.Mes, a.Anio)))
        {
            var (cliente, mes, anio) = g.Key;
            var (carpetaMes, nomina) = (g.First().CarpetaMes, g.First().Nomina);
            var firmados = Clientes.FirmadosDelMes(carpetaMes, mes);
            var sinFirmar = Clientes.RecibosSinFirmar(carpetaMes, nomina.Empleados);
            var delMes = Clientes.Archivos(carpetaMes);
            foreach (var deUno in g.GroupBy(a => a.Empleado.Cuil))
            {
                var x = deUno.First().Empleado;
                var archivo = Recibos.NombreArchivo(Recibos.SinComa(sinFirmar.GetValueOrDefault(x.Cuil)?.Nombre ?? x.Nombre), mes, anio.ToString());
                // La misma imagen repetida va una sola vez (ver Clientes.Planear, que tiene el mismo límite).
                var suyas = deUno.Select(a => a.Hoja).DistinctBy(h => h.Lectura.Huella ?? $"{h.Ruta}|{h.Hoja}").ToList();
                // Ya está en su carpeta si está el PDF con su nombre (lo guardó la app), o si cada archivo bajado ya está ahí tal cual.
                var yaEsta = File.Exists(Path.Combine(firmados, archivo)) || deUno.Select(a => a.Hoja.Ruta).Distinct(StringComparer.OrdinalIgnoreCase).All(r => YaEstaEnElMes(r, delMes));
                acomodos.Add(new(cliente, new ASuMes(x, mes, anio, archivo, firmados, suyas, false, yaEsta)));
                adonde[(cliente, mes, anio, x.Cuil)] = Path.Combine(firmados, archivo);
            }
        }
        // Los archivos con todas sus hojas ubicadas, y adónde van. Se cuenta antes de sacar las imágenes repetidas: un PDF que
        // repite una foto suelta también queda completo.
        var completos = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (archivo, cuantas) in hojas)
        {
            var suyas = asignadas.Where(a => a.Hoja.Ruta.Equals(archivo, StringComparison.OrdinalIgnoreCase)).ToList();
            if (suyas.Select(a => a.Hoja.Hoja).Distinct().Count() == cuantas && !sinUbicar.Any(u => u.Hoja.Ruta.Equals(archivo, StringComparison.OrdinalIgnoreCase)))
                completos[archivo] = suyas.Select(a => adonde[(a.Cliente, a.Mes, a.Anio, a.Empleado.Cuil)]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new(carpeta, acomodos.OrderBy(a => Path.GetFileName(a.Cliente), StringComparer.CurrentCultureIgnoreCase).ThenByDescending(a => (a.Recibo.Anio, a.Recibo.Mes))
            .ThenBy(a => a.Recibo.Archivo).ToList(), sinUbicar, completos, porLeer, avisos.ToList());
    }

    /// <summary>Lo que no se ubicó de un archivo («suyas»: sus hojas sin ubicar), en pocas palabras: de una sola hoja, por
    /// qué; de varias, si ninguna se ubicó, solo cuántas; si otras sí se ubicaron, cuáles no y por qué.</summary>
    // Con la carpeta de descargas de Windows, llena de cualquier cosa, el panel listaba una por una las 170 hojas de cada extracto.
    public static string PorQueNo(Bandeja b, IReadOnlyList<SinUbicar> suyas)
    {
        var archivo = suyas[0].Hoja.Ruta;
        var porQue = string.Join("; ", suyas.Select(u => u.PorQue).Distinct());
        if (b.Acomodos.Any(a => a.Recibo.Hojas.Any(h => h.Ruta.Equals(archivo, StringComparison.OrdinalIgnoreCase))))
            return $"{QueHojas(suyas.Select(u => u.Hoja.Hoja))} · {porQue}";
        return suyas.Count == 1 ? porQue : $"ningún recibo identificado en {suyas.Count} hojas";
    }

    // «hoja 3», «hojas 3 y 5», «hojas 1 a 4 y 7»: tres seguidas o más van como tramo.
    static string QueHojas(IEnumerable<int> numeros)
    {
        var n = numeros.Distinct().Order().ToList();
        var partes = new List<string>();
        for (var i = 0; i < n.Count; i++)
        {
            var j = i;
            while (j + 1 < n.Count && n[j + 1] == n[j] + 1) j++;
            if (j - i >= 2)
            {
                partes.Add($"{n[i]} a {n[j]}");
                i = j;
            }
            else partes.Add($"{n[i]}");
        }
        var lista = partes.Count == 1 ? partes[0] : $"{string.Join(", ", partes.SkipLast(1))} y {partes[^1]}";
        return n.Count == 1 ? $"hoja {lista}" : $"hojas {lista}";
    }

    // Las palabras que trae un recibo de sueldo y casi ningún otro papel. «RECIBO» no: está en «recibida», y en las fotos el
    // título grande se lee poco (en 35 de 234 recibos). Tampoco «PERIODO», «NETO» o «DESCUENTO», que trae una factura.
    static readonly string[] DeRecibo = ["EMPLEAD", "OBRA SOC", "SUELDO", "REMUNER", "JUBIL", "APORTE", "SINDIC", "ANTIGUE", "LEGAJO", "CUIL"];

    /// <summary>Una hoja que claramente no es un recibo: se leyó bien (60 palabras o más) y no trae ninguna palabra de recibo.
    /// No vale la pena leerla a fondo.</summary>
    // Con la carpeta de descargas de Windows, llena de escaneos de cualquier cosa, la lectura a fondo pasaba horas en ellos (18 a
    // 34 s por hoja en la VM). Medido el 22/11/2026 con la lectura rápida de las 586 hojas de las muestras: no saltea ninguno de
    // los 234 recibos firmados leídos con OCR (el que menos palabras de recibo tiene trae tres; el más corto, 113 palabras), y sí
    // una carta, una factura y un extracto pasados a foto. Con menos de 60 palabras (una foto que casi no se leyó) va a fondo igual.
    internal static bool NoEsRecibo(string texto)
    {
        var n = Regex.Replace(Clientes.Normal(texto), @"\s+", " ");
        return Regex.Matches(n, "[A-Z]{3,}").Count >= 60 && !DeRecibo.Any(n.Contains);
    }

    /// <summary>La segunda pasada: lee a fondo las hojas que no se pudieron ubicar (las fotos y los escaneos), buscando los
    /// CUIL de las nóminas de su mes. Después hay que revisar de nuevo, que sale de la memoria. Devuelve cuántas leyó.</summary>
    public static async Task<int> Profundizar(Bandeja b, Func<int, string?> raizDelAnio, DateTime hoy, Ocr? ocr, LeerImagen aFondo, Memoria memoria,
        Action<string> avisar, CancellationToken corte, SacarHuella? huella = null)
    {
        // Las que una lectura mejor puede ubicar: sin el CUIL o sin el mes a la vista. Una constancia, o un CUIL que está en
        // dos nóminas, no se arreglan leyendo mejor; lo que claramente no es un recibo, tampoco.
        var hojas = b.SinUbicar.Where(s => (s.PorQue.StartsWith("no encontré su CUIL") || s.PorQue is "no dice de qué mes es" or "no se pudo leer")
            && !s.Hoja.Lectura.AFondo && (s.Hoja.Lectura.Ocr || s.Hoja.Lectura.Texto.Trim().Length == 0 || Clientes.SeCorrige(s.Hoja))
            && !NoEsRecibo(s.Hoja.Lectura.Texto)).Select(s => s.Hoja).ToList();
        if (hojas.Count == 0) return 0;
        // A quién se busca: los de la nómina de su mes, en todos los clientes; si no se leyó el mes, los del mes de su
        // archivo (ver Desde) y el anterior. Las nóminas se leen antes: adentro de la lectura a fondo las hojas van de a
        // varias a la vez.
        async Task<List<string>> DeLosClientes(int mes, DateTime desde) => raizDelAnio(AnioDe(mes, desde)) is { } raiz
            ? (await Indice(raiz, mes, AnioDe(mes, desde), ocr, avisar, corte)).SelectMany(c => c.Nomina.Empleados.Select(e => e.Cuil)).ToList() : [];
        var buscados = new Dictionary<(string, int), List<string>>();
        foreach (var h in hojas)
        {
            var desde = Desde(File.GetLastWriteTime(h.Ruta), hoy);
            buscados[(h.Ruta, h.Hoja)] = Clientes.MesDelRecibo(h.Lectura.Texto) is { } mes ? await DeLosClientes(mes, desde)
                : [.. await DeLosClientes(desde.Month, desde), .. await DeLosClientes(desde.AddMonths(-1).Month, desde)];
        }
        await Clientes.ProfundizarHojas(hojas, h => buscados[(h.Ruta, h.Hoja)], b.Carpeta, aFondo, memoria, avisar, corte, huella);
        return hojas.Count;
    }

    /// <summary>Lo que se guarda: los recibos que todavía no están en Z (y la carpeta de los firmados de su mes, si no
    /// está), y los originales que ya tienen todos sus recibos guardados, a «Acomodados». «cliente»: solo los de ese
    /// cliente (su carpeta); null, todos.</summary>
    public static List<Escritura> Planear(Bandeja b, string? cliente = null)
    {
        var plan = new List<Escritura>();
        var suyos = b.Acomodos.Where(a => cliente is null || a.Cliente.Equals(cliente, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in suyos.Where(a => !a.Recibo.EnSuCarpeta))
        {
            var l = a.Recibo;
            if (File.Exists(l.Ruta) || plan.Any(p => p.Ruta.Equals(l.Ruta, StringComparison.OrdinalIgnoreCase))) continue;
            if (!Directory.Exists(l.Carpeta) && !plan.Any(p => p.Accion == Accion.Carpeta && p.Ruta.Equals(l.Carpeta, StringComparison.OrdinalIgnoreCase)))
                plan.Add(new(Accion.Carpeta, l.Carpeta!, null, []));
            plan.Add(new(Accion.ASuMes, l.Ruta!, l.Empleado, l.Hojas));
        }
        // Un original pasa a «Acomodados» si todas sus hojas tienen adónde ir y cada recibo con hojas suyas ya está en Z o se
        // guarda en este mismo plan. Uno con alguna hoja sin ubicar queda en la carpeta, a la vista.
        var vanAhora = plan.Where(p => p.Accion == Accion.ASuMes).Select(p => p.Ruta).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deEste = suyos.Select(a => a.Recibo.Ruta!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (archivo, destinos) in b.Completos)
            if (destinos.Any(deEste.Contains) && destinos.All(d => vanAhora.Contains(d) || EnZ(b, d)))
                plan.Add(new(Accion.Acomodar, archivo, null, []));
        return plan;
    }

    // Ese recibo ya está en Z: el PDF con su nombre, o el mismo archivo bajado tal cual (ver YaEstaEnElMes).
    static bool EnZ(Bandeja b, string destino) =>
        File.Exists(destino) || b.Acomodos.Any(a => a.Recibo.EnSuCarpeta && string.Equals(a.Recibo.Ruta, destino, StringComparison.OrdinalIgnoreCase));

    /// <summary>Escribe una cosa del plan. Nunca pisa ni borra: el recibo va con CreateNew, y el original se mueve a
    /// «Acomodados» con otro nombre si ya hay uno igual, y solo si todos sus recibos ya están en Z.</summary>
    public static async Task Escribir(Escritura e, Bandeja b, Corregir corregir)
    {
        switch (e.Accion)
        {
            case Accion.Carpeta:
                if (!Directory.Exists(Path.GetDirectoryName(e.Ruta))) throw new IOException($"ya no está la carpeta {Path.GetFileName(Path.GetDirectoryName(e.Ruta))}");
                Directory.CreateDirectory(e.Ruta);
                return;
            case Accion.ASuMes:
                var l = b.Acomodos.First(a => string.Equals(a.Recibo.Ruta, e.Ruta, StringComparison.OrdinalIgnoreCase)).Recibo;
                if (!Directory.Exists(l.Carpeta)) throw new IOException($"no está la carpeta {Path.GetFileName(l.Carpeta)}");
                await Clientes.EscribirCorregido(e.Ruta, $"{l.Empleado.Cuil} {l.Mes:00}/{l.Anio}", e.Hojas, corregir);
                return;
            case Accion.Acomodar:
                // Se mira de nuevo: si un recibo suyo no se pudo guardar, el original no se mueve.
                if (b.Completos.GetValueOrDefault(e.Ruta)?.FirstOrDefault(d => !EnZ(b, d)) is { } falta)
                    throw new IOException($"no se guardó {Path.GetFileNameWithoutExtension(falta)}");
                if (!b.Completos.ContainsKey(e.Ruta)) throw new IOException("tiene hojas sin ubicar");
                var adonde = Directory.CreateDirectory(Path.Combine(b.Carpeta, Acomodados)).FullName;
                File.Move(e.Ruta, Libre(Path.Combine(adonde, Path.GetFileName(e.Ruta))));
                return;
            default:
                throw new InvalidOperationException($"{e.Accion} no es de la carpeta de descargas");
        }
    }

    // Si ya hay un archivo con ese nombre, el mismo con « (2)», « (3)»… como hace el Explorador.
    static string Libre(string ruta)
    {
        if (!File.Exists(ruta)) return ruta;
        var (carpeta, nombre, extension) = (Path.GetDirectoryName(ruta)!, Path.GetFileNameWithoutExtension(ruta), Path.GetExtension(ruta));
        for (var n = 2; ; n++)
            if (Path.Combine(carpeta, $"{nombre} ({n}){extension}") is var otra && !File.Exists(otra)) return otra;
    }
}
