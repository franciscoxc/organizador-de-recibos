using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

// Lo que dijo de una hoja (o de una foto) el texto del PDF o el OCR. Del OCR quedan también el giro y la
// inclinación con que se leyó: con eso se endereza después, sin volver a leerla. Nombre: el «APELLIDO Y NOMBRE»
// de un recibo con texto. AFondo: ya pasó por la lectura a fondo; si ahí tampoco apareció nadie, no se reintenta.
public record Lectura(string Texto, bool Ocr = false, int Giro = 0, double Angulo = 0, string? Nombre = null, bool AFondo = false);

// Una hoja de un archivo; la hoja 0 es una foto.
public record Hallazgo(string Ruta, int Hoja, Lectura Lectura);

// Las fotos y los escaneos los lee Windows: el motor no sabe de OCR. «buscado» dice si el texto ya trae a
// alguien de la nómina; mientras no, el OCR prueba otros giros.
public delegate Task<Lectura> LeerImagen(string ruta, int hoja, Func<string, bool> buscado);

// La versión que se guarda de una foto o de una hoja escaneada: girada, derecha y con contraste, en JPEG.
public delegate Task<(byte[] Jpeg, int Ancho, int Alto)> Corregir(Hallazgo h);

public enum Firma { Falta, Firmado, PorNombre, ConIniciales }

public record Renglon(Empleado Empleado, Firma Firma, List<Hallazgo> Firmado, List<Hallazgo> SinFirmar)
{
    // Como lo escribe su recibo, «APELLIDO NOMBRE»: la nómina de AFIP a veces pone el nombre primero, y no
    // siempre igual dentro del mismo PDF. Sin recibo con texto, el de la nómina.
    public string Nombre => Recibos.SinComa(SinFirmar.Select(h => h.Lectura.Nombre).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? Empleado.Nombre);
}

/// <summary>Un cliente en un mes. Sin carpeta del mes o sin nómina no hay renglones. Sin carpeta de firmados
/// (Firmados es null), a todos les falta: el cliente todavía no los mandó.</summary>
public record Revision(string Cliente, int Mes, int Anio, string? CarpetaMes, Nomina? Nomina, string? Firmados,
    List<Renglon> Renglones, List<Hallazgo> Ilegibles, List<string> Avisos)
{
    public int Faltan => Renglones.Count(r => r.Firma == Firma.Falta);
}

public enum Accion { Carpeta, Corregido, Iniciales, Lista }

public record Escritura(Accion Accion, string Ruta, Empleado? Empleado, List<Hallazgo> Hojas);

// Lo leído de cada archivo, por ruta, tamaño y fecha: la segunda revisión lee solo lo nuevo. El OCR de una
// foto tarda un segundo y la carpeta de los clientes suele venir por la red.
public sealed class Memoria
{
    public record Entrada(long Tamano, DateTime Fecha, string Claves, List<Lectura> Hojas);

    readonly string ruta;
    readonly Dictionary<string, Entrada> datos;

    public Memoria(string ruta)
    {
        this.ruta = ruta;
        try { datos = new(JsonSerializer.Deserialize<Dictionary<string, Entrada>>(File.ReadAllText(ruta))!, StringComparer.OrdinalIgnoreCase); }
        catch { datos = new(StringComparer.OrdinalIgnoreCase); } // la primera vez no hay nada guardado
    }

    // Con candado: al cambiar de mes, la revisión anterior puede estar terminando su último archivo.
    public Entrada? Buscar(FileInfo f)
    {
        lock (datos) return datos.TryGetValue(f.FullName, out var e) && e.Tamano == f.Length && e.Fecha == f.LastWriteTimeUtc ? e : null;
    }

    public void Anotar(FileInfo f, string claves, List<Lectura> hojas)
    {
        lock (datos) datos[f.FullName] = new(f.Length, f.LastWriteTimeUtc, claves, hojas);
    }

    // Una hoja releída a fondo reemplaza a la de la lectura rápida, si el archivo sigue siendo el mismo.
    public void Reemplazar(FileInfo f, int indice, Lectura hoja)
    {
        lock (datos)
            if (datos.TryGetValue(f.FullName, out var e) && e.Tamano == f.Length && e.Fecha == f.LastWriteTimeUtc && indice < e.Hojas.Count)
                e.Hojas[indice] = hoja;
    }

    public void Guardar()
    {
        try
        {
            string json;
            lock (datos) json = JsonSerializer.Serialize(datos);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            // A un archivo aparte y después a su lugar: si la app se cierra a mitad de escribir (la PC se apaga), queda
            // la memoria anterior entera y no un archivo cortado, que no se puede leer y obliga a leer todo de nuevo.
            lock (ruta)
            {
                File.WriteAllText(ruta + ".nuevo", json);
                File.Move(ruta + ".nuevo", ruta, overwrite: true);
            }
        }
        catch { } // es un atajo: sin la memoria, la próxima revisión vuelve a leer todo
    }
}

public static class Clientes
{
    static readonly string[] Meses = ["ENERO", "FEBRERO", "MARZO", "ABRIL", "MAYO", "JUNIO", "JULIO", "AGOSTO", "SEPTIEMBRE", "OCTUBRE", "NOVIEMBRE", "DICIEMBRE"];
    static readonly string[] Imagenes = [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".tif", ".tiff", ".heic", ".webp"];
    const string Encabezado = "Recibos firmados que faltan";

    /// <summary>La carpeta de los clientes elegida, llevada a otro año: el estudio arma una por año y la ruta lo
    /// nombra («…\CLIENTES 2026\…»). Sin año en la ruta, queda igual.</summary>
    public static string RaizDelAnio(string elegida, int anio) => Regex.Replace(elegida, @"(?<!\d)(19|20)\d{2}(?!\d)", anio.ToString());

    public static string NombreMes(int mes) => Meses[mes - 1];

    // Mayúsculas y sin acentos: «Nómina» y «NOMINA» son lo mismo.
    public static string Normal(string s) => string.Concat(s.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant();

    /// <summary>«PEREZ SUELDOS OCTUBRE 2026», «10 OCTUBRE 2026» o «10 2026». El nombre del mes tiene que ir
    /// pegado al año: «MAYO» suelto puede ser un apellido.</summary>
    public static bool EsDelMes(string carpeta, int mes, int anio)
    {
        var n = Normal(carpeta);
        var nombre = mes == 9 ? "(SEPTIEMBRE|SETIEMBRE)" : Meses[mes - 1];
        return Regex.IsMatch(n, $@"\b{nombre}\s+{anio}") || Regex.IsMatch(n, $@"(?<!\d)0?{mes}\s+{anio}");
    }

    // «RECIBOS FIRMADOS OCTUBRE», «PEREZ RECIBOS FIRMADOS», «RECIBOS FIRMADOS OCTUBE»: alcanzan las dos palabras.
    public static bool EsDeFirmados(string carpeta)
    {
        var n = Normal(Path.GetFileName(carpeta));
        return n.Contains("RECIBO") && n.Contains("FIRMAD");
    }

    static string[] Subcarpetas(string carpeta)
    {
        try { return Directory.GetDirectories(carpeta); }
        catch { return []; } // sin permiso o desconectada: como si estuviera vacía
    }

    static bool Contiene(string carpeta, string palabra) => Normal(Path.GetFileName(carpeta)).Contains(palabra);

    /// <summary>Los clientes con carpeta de sueldos, por orden alfabético.</summary>
    public static List<string> ConSueldos(string raiz) => Subcarpetas(raiz)
        .Where(c => Subcarpetas(c).Any(s => Contiene(s, "SUELDOS")))
        .OrderBy(c => Path.GetFileName(c), StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Cliente\…SUELDOS…\…LIQUIDACIONES…\(la del mes).</summary>
    public static string? CarpetaDelMes(string cliente, int mes, int anio) => Subcarpetas(cliente)
        .Where(s => Contiene(s, "SUELDOS")).SelectMany(Subcarpetas)
        .Where(l => Contiene(l, "LIQUIDACION")).SelectMany(Subcarpetas)
        .FirstOrDefault(m => EsDelMes(Path.GetFileName(m), mes, anio));

    static bool EsPdf(FileInfo f) => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    static bool EsImagen(FileInfo f) => Imagenes.Contains(f.Extension.ToLowerInvariant());
    static bool EsNomina(FileInfo f) => EsPdf(f) && Normal(f.Name).Contains("NOMINA");

    // Sin los ocultos ni los del sistema (Thumbs.db), y sin frenarse en una subcarpeta sin permiso.
    static List<FileInfo> Archivos(string carpeta)
    {
        try { return new DirectoryInfo(carpeta).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch { return []; }
    }

    // Palabras de tres letras o más: «DE» y «LA» no distinguen a nadie.
    static string[] Partes(string texto) => Regex.Split(Normal(texto), "[^A-Z]+").Where(p => p.Length >= 3).Distinct().ToArray();

    /// <summary>Si el CUIL no se pudo leer: el empleado de la nómina cuyo nombre aparece en el texto (dos de sus
    /// palabras, o la única que tenga), siempre que sea uno solo. El empleador también figura en el recibo, pero
    /// no está en la nómina.</summary>
    public static Empleado? PorNombre(string texto, IEnumerable<Empleado> nomina)
    {
        var palabras = Partes(texto).ToHashSet();
        var candidatos = nomina.Where(e => Partes(e.Nombre) is { Length: > 0 } p && p.Count(palabras.Contains) >= Math.Min(2, p.Length)).Take(2).ToList();
        return candidatos.Count == 1 ? candidatos[0] : null;
    }

    // Lo que se guarda en los firmados y no es un recibo: la constancia de una baja o un alta, un DNI, un certificado.
    static bool EsOtroDocumento(string texto) => Regex.IsMatch(Normal(texto), @"\b(BAJAS?|ALTAS?|CONSTANCIA|DNI|CERTIF\w*|LICENCIA)\b");

    /// <summary>Los CUIL de la nómina que aparecen en el texto, escritos como sea: con guiones, puntos o espacios, o
    /// sin nada, y con las letras que el OCR confunde con números.</summary>
    public static List<string> DeLaNomina(string texto, IEnumerable<string> cuils)
    {
        var digitos = Digitos(texto);
        // ponytail-keep: los diez primeros (tipo y DNI), sin el verificador. La cajita «C.U.I.L.» del recibo es más
        // angosta que el número y su borde tapa el último dígito: en la foto se lee «20-12345678-». Pidiendo los once,
        // 44 de las 58 fotos de octubre que no se leían quedaban sin leer. Con diez ya se sabe quién es.
        return cuils.Where(c => Diez(c) is { } d && digitos.Contains(d)).ToList();
    }

    /// <summary>El empleado cuyo CUIL se leyó con uno o dos dígitos cambiados, si alguna palabra de su nombre también
    /// está en la hoja y es el único así. Para las fotos borrosas, donde el OCR confunde un 3 con un 8: lo que se
    /// encuentra así queda para mirar, como lo que se encuentra por el nombre.</summary>
    public static Empleado? PorCuilParecido(string texto, IEnumerable<Empleado> nomina)
    {
        var tiras = Regex.Matches(Digitos(texto), @"\d{10,}").Select(m => m.Value).ToList();
        var palabras = Partes(texto).ToHashSet();
        // De a dos como mucho: dos hermanos con el DNI casi igual y el mismo apellido no se pueden distinguir así.
        var candidatos = nomina.Where(e => Diez(e.Cuil) is { } d && tiras.Any(t => Parecido(t, d)) && Partes(e.Nombre).Any(palabras.Contains))
            .Take(2).ToList();
        return candidatos.Count == 1 ? candidatos[0] : null;
    }

    // El tipo y el DNI: los diez primeros dígitos del CUIL, sin el verificador.
    static string? Diez(string cuil) => cuil.Replace("-", "") is { Length: >= 10 } d ? d[..10] : null;

    // Si en la tira hay diez dígitos seguidos que difieren de los buscados en dos lugares o menos.
    static bool Parecido(string tira, string diez)
    {
        for (var i = 0; i + diez.Length <= tira.Length; i++)
        {
            var distintos = 0;
            for (var j = 0; j < diez.Length && distintos <= 2; j++)
                if (tira[i + j] != diez[j]) distintos++;
            if (distintos <= 2) return true;
        }
        return false;
    }

    // Los números del texto, con lo que el OCR deja en el medio sacado.
    static string Digitos(string texto)
    {
        // Las confusiones del OCR, solo dentro de palabras que ya son casi todas números («2O-4S678901-4»).
        var arreglado = Regex.Replace(texto, @"[\w\-.]*\d[\w\-.]*", m => m.Value.Count(char.IsDigit) * 2 < m.Value.Length ? m.Value
            : new string(m.Value.Select(c => c switch { 'O' or 'o' => '0', 'S' or 's' => '5', 'I' or 'l' or '|' => '1', 'B' => '8', _ => c }).ToArray()));
        // ponytail-keep: búsqueda floja y no Recibos.Cuils. El OCR de Windows pierde los guiones («CUIL. 27234567891») o
        // los cambia por puntos («20.12345678-6»): con la búsqueda estricta, 47 de las fotos reales de octubre quedaban
        // sin leer. Es seguro porque solo se buscan los CUIL de la nómina, no cualquier número.
        return Regex.Replace(arreglado, @"(?<=\d)[\s.\-]{1,3}(?=\d)", "");
    }

    // Las lecturas con OCR que van a la vez, sumando todos los archivos y todas las hojas: una por núcleo, pero no más
    // de las que entran en la memoria.
    internal static readonly int ALaVez = CuantasALaVez();
    static readonly SemaphoreSlim Turnos = new(ALaVez);

    // ponytail-keep: el tope por memoria. Cada lectura ocupa unos 700 MB en su pico (un motor y la hoja a 3000 puntos):
    // en la VM de 4 GB, con cuatro a la vez la app llegó a 2,8 GB y Windows se quedó sin memoria para abrir otros
    // programas. Se dejan 2 GB para Windows y lo demás; así, 4 GB leen de a dos y 8 GB, de a uno por núcleo.
    static int CuantasALaVez()
    {
        var mb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576;
        return mb <= 0 ? Environment.ProcessorCount : (int)Math.Clamp((mb - 2048) / 700, 1, Environment.ProcessorCount);
    }

    static async Task<Lectura> ConTurno(Func<Task<Lectura>> leer, CancellationToken corte)
    {
        await Turnos.WaitAsync(corte);
        // ponytail-keep: Task.Run y no leer() directo. Dibujar una hoja de PDF y leerla con el OCR no tienen ningún
        // await de verdad: sin Task.Run, cada hoja se leía entera antes de lanzar la siguiente, y un PDF escaneado
        // usaba un núcleo (medido con un cliente real: cuatro hojas, 33 s de reloj y 34 de procesador).
        try { return await Task.Run(leer); }
        finally { Turnos.Release(); }
    }

    // Las fotos y los PDF escaneados pasan por el OCR solo entre los firmados: afuera hay DNI, altas y
    // certificados, que no son recibos y tardarían en leerse.
    static async Task<Memoria.Entrada?> Leer(FileInfo f, bool firmado, LeerImagen? leer, Memoria memoria, Func<string, bool> buscado, Action<string> avisar, CancellationToken corte)
    {
        if (memoria.Buscar(f) is { } guardada) return guardada;
        var hojas = new List<Lectura>();
        var claves = "";
        var completa = true; // lo que no se pudo leer por falta de OCR no se recuerda: la próxima vez puede haberlo
        if (EsPdf(f))
        {
            using var doc = PdfDocument.Open(f.FullName);
            claves = doc.Information.Keywords ?? "";
            // Los PDF que arma la app llevan el CUIL en las palabras clave: no hace falta el OCR.
            var porClaves = buscado(claves);
            // Las hojas escaneadas de un PDF se leen a la vez, cada una cuando le toca turno. De a una, un PDF de doce
            // hojas ocupaba un núcleo de cuatro (25 % del procesador) y los otros tres esperaban.
            var lecturas = new List<Task<Lectura>>();
            foreach (var p in doc.GetPages())
            {
                var texto = string.Join(" ", p.GetWords().Select(w => w.Text));
                if (texto.Length > 0 || !firmado || porClaves) lecturas.Add(Task.FromResult(new Lectura(texto, Nombre: NombreDelRecibo(p, texto))));
                else if (leer is null)
                {
                    completa = false;
                    lecturas.Add(Task.FromResult(new Lectura("")));
                }
                else
                {
                    var numero = p.Number;
                    lecturas.Add(ConTurno(() =>
                    {
                        avisar($"{f.Name}, hoja {numero}");
                        return leer(f.FullName, numero, buscado);
                    }, corte));
                }
            }
            hojas.AddRange(await Task.WhenAll(lecturas));
        }
        else if (firmado && EsImagen(f) && leer is not null)
        {
            avisar(f.Name);
            hojas.Add(await ConTurno(() => leer(f.FullName, 0, buscado), corte));
        }
        else return null;
        if (completa) memoria.Anotar(f, claves, hojas);
        return new(f.Length, f.LastWriteTimeUtc, claves, hojas);
    }

    // El «APELLIDO Y NOMBRE» de una hoja de recibo con texto, leído como al separar a mano.
    static string? NombreDelRecibo(Page p, string texto)
    {
        if (!Normal(texto).Contains("FIRMA DEL EMPLEADO")) return null;
        try { return Recibos.LeerHoja(Recibos.Palabras(p), p.Width).Nombre; }
        catch (FormatException) { return null; } // otro formato: queda el nombre de la nómina
    }

    /// <summary>Revisa un cliente en un mes. Solo lee: lo que haya que guardar lo arma Planear.</summary>
    public static async Task<Revision> Revisar(string cliente, int mes, int anio, Ocr? ocr, LeerImagen? leer, Memoria memoria,
        Action<string> avisar, CancellationToken corte)
    {
        var nombre = Path.GetFileName(cliente);
        var avisos = new List<string>();
        var carpetaMes = CarpetaDelMes(cliente, mes, anio);
        if (carpetaMes is null) return new(nombre, mes, anio, null, null, null, [], [], avisos);

        var archivos = Archivos(carpetaMes);
        var firmados = Subcarpetas(carpetaMes).Where(EsDeFirmados).ToList();
        // Si hay más de una, se escribe en la que nombra el mes («RECIBOS FIRMADOS OCTUBRE»).
        var destino = firmados.FirstOrDefault(c => Contiene(c, Meses[mes - 1])) ?? firmados.FirstOrDefault();
        bool EnFirmados(FileInfo f) => firmados.Any(c => f.FullName.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        // La nómina puede venir partida en varios PDF («NOMINA 1 07 2026», «NOMINA 2 07 2026»): se juntan.
        Nomina? nomina = null;
        foreach (var f in archivos.Where(f => EsNomina(f) && !EnFirmados(f)))
        {
            corte.ThrowIfCancellationRequested();
            try
            {
                // shortcut: la nómina no pasa por la memoria y las escaneadas (las de julio y agosto) se leen
                // con OCR cada vez; si revisar esos meses se vuelve habitual, guardarlas como las fotos.
                var n = await Recibos.LeerNominaPdf(f.FullName, ocr, m =>
                {
                    if (m.StartsWith('⚠')) avisos.Add(m[2..]);
                    else avisar($"{nombre}: {m}");
                });
                if (n is null) continue;
                if (n.Mes != mes || n.Anio != anio.ToString())
                {
                    avisos.Add($"{f.Name} es la nómina de {n.Mes:00}/{n.Anio}");
                    continue;
                }
                var junta = nomina ??= new Nomina(f.FullName, mes, n.Anio, []);
                junta.Empleados.AddRange(n.Empleados.Where(e => junta.Empleados.All(x => x.Cuil != e.Cuil)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                avisos.Add($"{f.Name}: {e.Message}");
            }
        }
        if (nomina is null) return new(nombre, mes, anio, carpetaMes, null, destino, [], [], avisos);

        var empleados = nomina.Empleados;
        var porCuil = empleados.ToDictionary(e => e.Cuil);
        bool Buscado(string texto) => DeLaNomina(texto, porCuil.Keys).Count > 0;
        var firmado = porCuil.Keys.ToDictionary(c => c, _ => new List<Hallazgo>());
        var sinFirmar = porCuil.Keys.ToDictionary(c => c, _ => new List<Hallazgo>());
        var deLaApp = new HashSet<Hallazgo>(); // con las iniciales que estampó el estudio
        var porNombre = new HashSet<Hallazgo>();
        var ilegibles = new List<Hallazgo>();

        // De a varios archivos a la vez, uno por núcleo: el OCR de una foto usa uno solo, y de a uno la primera
        // revisión dejaba ocioso el resto del procesador (25 % en una PC de cuatro). Después se recorren en orden.
        var lista = archivos.Where(f => !EsNomina(f) || EnFirmados(f)).ToList();
        var leidos = new Memoria.Entrada?[lista.Count];
        var errores = new string?[lista.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, lista.Count), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = corte },
            async (i, _) =>
            {
                try { leidos[i] = await Leer(lista[i], EnFirmados(lista[i]), leer, memoria, Buscado, m => avisar($"{nombre}: {m}"), corte); }
                catch (Exception ex) when (ex is not OperationCanceledException) { errores[i] = $"{lista[i].Name}: {ex.Message}"; }
            });
        avisos.AddRange(errores.OfType<string>());
        foreach (var (f, e) in lista.Zip(leidos))
        {
            if (e is null) continue;
            var enFirmados = EnFirmados(f);
            for (var i = 0; i < e.Hojas.Count; i++)
            {
                var h = new Hallazgo(f.FullName, EsPdf(f) ? i + 1 : 0, e.Hojas[i]);
                var cuils = DeLaNomina(e.Claves + " " + h.Lectura.Texto, porCuil.Keys);
                if (!enFirmados)
                {
                    // Afuera de los firmados cuenta solo lo que es un recibo: el resumen y la nómina también traen
                    // los CUIL, pero no el espacio para la firma.
                    if (Normal(h.Lectura.Texto).Contains("FIRMA DEL EMPLEADO"))
                        foreach (var c in cuils) sinFirmar[c].Add(h);
                    continue;
                }
                if (cuils.Count == 0 && PorNombre(h.Lectura.Texto, empleados) is { } p)
                {
                    cuils.Add(p.Cuil);
                    porNombre.Add(h);
                }
                // Si no, el CUIL leído con uno o dos dígitos cambiados y algo del nombre de esa persona en la hoja.
                // También queda para mirar.
                if (cuils.Count == 0 && PorCuilParecido(h.Lectura.Texto, empleados) is { } parecido)
                {
                    cuils.Add(parecido.Cuil);
                    porNombre.Add(h);
                }
                // Lo último: el nombre del archivo. Las fotos se guardan con el del empleado («PEREZ JUAN RECIBO OCT
                // 2026.jpeg»), y en octubre las que el OCR no leía eran justo esas. Solo en archivos de una hoja (un PDF
                // con varios recibos se llama como uno solo, o como ninguno), y nunca en una baja, un alta o un DNI.
                if (cuils.Count == 0 && e.Hojas.Count == 1 && !EsOtroDocumento(f.Name + " " + h.Lectura.Texto)
                    && PorNombre(Path.GetFileNameWithoutExtension(f.Name), empleados) is { } q)
                {
                    cuils.Add(q.Cuil);
                    porNombre.Add(h);
                }
                if (cuils.Count == 0)
                {
                    ilegibles.Add(h);
                    continue;
                }
                if (e.Claves.Contains("iniciales")) deLaApp.Add(h);
                foreach (var c in cuils) firmado[c].Add(h);
            }
        }

        var renglones = empleados.Select(x =>
        {
            var hs = firmado[x.Cuil];
            var firma = hs.Count == 0 ? Firma.Falta
                : hs.All(deLaApp.Contains) ? Firma.ConIniciales
                : hs.All(porNombre.Contains) ? Firma.PorNombre
                : Firma.Firmado;
            return new Renglon(x, firma, hs, sinFirmar[x.Cuil]);
        }).ToList();
        return new(nombre, mes, anio, carpetaMes, nomina, destino, renglones, ilegibles, avisos);
    }

    /// <summary>Las hojas de los firmados que la lectura rápida dejó sin un CUIL de la nómina (las sin nadie, y las que
    /// se encontraron solo por el nombre) y que todavía no se leyeron a fondo.</summary>
    public static List<Hallazgo> Dificiles(Revision r) => r.Ilegibles
        .Concat(r.Renglones.Where(x => x.Firma == Firma.PorNombre).SelectMany(x => x.Firmado))
        .Where(h => h.Lectura.Ocr && !h.Lectura.AFondo).DistinctBy(h => (h.Ruta, h.Hoja)).ToList();

    /// <summary>La segunda pasada: lee a fondo las hojas difíciles del cliente y deja lo leído en la memoria. Después
    /// hay que revisarlo de nuevo, que sale de la memoria. Devuelve cuántas leyó.</summary>
    public static async Task<int> Profundizar(Revision r, LeerImagen aFondo, Memoria memoria, Action<string> avisar, CancellationToken corte)
    {
        if (r.Nomina is null) return 0;
        var cuils = r.Nomina.Empleados.Select(e => e.Cuil).ToList();
        bool Buscado(string texto) => DeLaNomina(texto, cuils).Count > 0;
        var hojas = Dificiles(r);
        await Parallel.ForEachAsync(hojas, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = corte },
            async (h, _) =>
            {
                avisar(Path.GetFileName(h.Ruta));
                try
                {
                    var l = await ConTurno(() => aFondo(h.Ruta, h.Hoja, Buscado), corte);
                    memoria.Reemplazar(new FileInfo(h.Ruta), Math.Max(0, h.Hoja - 1), l with { AFondo = true });
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Una hoja que no se pudo leer queda como estaba, marcada para no volver a intentar.
                    memoria.Reemplazar(new FileInfo(h.Ruta), Math.Max(0, h.Hoja - 1), h.Lectura with { AFondo = true });
                }
            });
        return hojas.Count;
    }

    static string Archivo(string carpeta, Renglon x, Revision r) => Path.Combine(carpeta, Recibos.NombreArchivo(x.Nombre, r.Mes, r.Anio.ToString()));

    static string RutaLista(string firmados, Revision r) => Path.Combine(firmados, $"FALTAN RECIBOS FIRMADOS {r.Mes:00} {r.Anio}.txt");

    /// <summary>Lo que se guardaría en la carpeta del cliente. Nunca un archivo que ya existe. La única carpeta que
    /// puede crear es la de los firmados del mes, y solo si hay recibos para poner adentro. Las iniciales, solo
    /// para los que eligió el usuario entre los que no tienen el firmado.</summary>
    public static List<Escritura> Planear(Revision r, IReadOnlySet<string> conIniciales)
    {
        var plan = new List<Escritura>();
        if (r.Nomina is null) return plan;
        bool Libre(string ruta) => !File.Exists(ruta) && !plan.Any(p => p.Ruta.Equals(ruta, StringComparison.OrdinalIgnoreCase));
        // El nombre que usan casi todas: «RECIBOS FIRMADOS OCTUBRE».
        var firmados = r.Firmados ?? Path.Combine(r.CarpetaMes!, $"RECIBOS FIRMADOS {NombreMes(r.Mes)}");

        // Cuántos empleados trae cada PDF sin firmar: el de uno solo es su recibo suelto; el de varios, el de todos juntos.
        var empleadosPorPdf = r.Renglones.SelectMany(x => x.SinFirmar.Select(h => (h.Ruta, x.Empleado.Cuil))).Distinct()
            .GroupBy(t => t.Ruta, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var x in r.Renglones)
        {
            // Los firmados que llegaron en foto o escaneados: un PDF por empleado, derecho y con contraste.
            var imagenes = x.Firmado.Where(h => h.Lectura.Ocr).ToList();
            if (imagenes.Count > 0 && Libre(Archivo(firmados, x, r)))
                plan.Add(new(Accion.Corregido, Archivo(firmados, x, r), x.Empleado, imagenes));

            // Las iniciales del estudio, sobre el recibo sin firmar del PDF con menos empleados (el suelto, si está).
            if (x.Firma == Firma.Falta && conIniciales.Contains(x.Empleado.Cuil) && x.SinFirmar.Count > 0 && Libre(Archivo(firmados, x, r)))
            {
                var pdf = x.SinFirmar.GroupBy(h => h.Ruta, StringComparer.OrdinalIgnoreCase).MinBy(g => empleadosPorPdf[g.Key])!;
                plan.Add(new(Accion.Iniciales, Archivo(firmados, x, r), x.Empleado, pdf.ToList()));
            }
            // Los PDF sin firmar, el de todos juntos y los sueltos, son los que el estudio arma para mandarle al cliente:
            // no se separan ni se tocan, solo se leen (a quién le falta la firma, y la base de la copia con iniciales).
            // Todo lo que se guarda va a la carpeta de los firmados, que es lo que manda el cliente.
        }

        // La carpeta de los firmados se crea solo si hay recibos para guardar en ella, y antes que ellos.
        var crear = r.Firmados is null && plan.Any(p => p.Ruta.StartsWith(firmados + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (crear) plan.Insert(0, new(Accion.Carpeta, firmados, null, []));

        // La lista de los que faltan, en la carpeta de los firmados: nueva si falta alguien o si la carpeta es
        // nueva, o la vieja de la app cuando ya no dice lo mismo. Su texto se arma al escribirla, después de
        // los recibos: así ya cuenta los que llevan iniciales.
        if (r.Firmados is not null || crear)
        {
            var ruta = RutaLista(firmados, r);
            var falta = r.Faltan > 0 || r.Ilegibles.Count > 0 || crear;
            if (File.Exists(ruta) ? EsNuestra(ruta) && SinFecha(File.ReadAllText(ruta)) != SinFecha(Lista(r)) : falta)
                plan.Add(new(Accion.Lista, ruta, null, []));
        }
        return plan;
    }

    static bool EsNuestra(string ruta)
    {
        try { return File.ReadLines(ruta).FirstOrDefault()?.StartsWith(Encabezado) == true; }
        catch { return false; } // si no se puede leer, no se toca
    }

    static string SinFecha(string lista) => string.Join("\n", lista.Split('\n').Where(l => !l.StartsWith("Revisado el")).Select(l => l.TrimEnd('\r')));

    /// <summary>El texto de FALTAN RECIBOS FIRMADOS MM AAAA.txt.</summary>
    public static string Lista(Revision r)
    {
        var s = new StringBuilder();
        void Personas(string titulo, Firma firma)
        {
            var quienes = r.Renglones.Where(x => x.Firma == firma).ToList();
            if (quienes.Count == 0) return;
            s.AppendLine().AppendLine(titulo);
            foreach (var x in quienes) s.AppendLine($"    {x.Nombre} · CUIL {x.Empleado.Cuil}");
        }
        s.AppendLine($"{Encabezado} · {r.Cliente} · {r.Mes:00}/{r.Anio}");
        s.AppendLine($"Revisado el {DateTime.Now:dd/MM/yyyy HH:mm} contra la nómina de AFIP ({r.Renglones.Count} empleados).");
        if (r.Faltan == 0) s.AppendLine().AppendLine("No falta ninguno.");
        Personas(r.Faltan == 1 ? "Falta 1:" : $"Faltan {r.Faltan}:", Firma.Falta);
        Personas("Con las iniciales que estampó el estudio, sin la firma del empleado:", Firma.ConIniciales);
        Personas("Encontrados por el nombre, porque el CUIL no se leía bien (ilegibles, revisar):", Firma.PorNombre);
        if (r.Ilegibles.Count > 0)
        {
            s.AppendLine().AppendLine("No encontré a nadie de la nómina en estos (hay que mirarlos: puede ser una baja, un DNI o una foto ilegible):");
            foreach (var g in r.Ilegibles.GroupBy(h => h.Ruta))
                s.AppendLine($"    {Path.GetFileName(g.Key)}" + (g.First().Hoja > 0 ? $", hoja {string.Join(", ", g.Select(h => h.Hoja))}" : ""));
        }
        return s.ToString();
    }

    /// <summary>La hoja del recibo sin firmar con las iniciales del estudio, sola, en un PDF en memoria, y dónde está el
    /// espacio de la firma: para ver cómo quedarían antes de guardar. Estampa igual que Escribir.</summary>
    public static (byte[] Pdf, Palabra Firma, double Ancho, double Alto) ConIniciales(Hallazgo h)
    {
        using var doc = PdfDocument.Open(h.Ruta);
        var b = new PdfDocumentBuilder();
        var copia = b.AddPage(doc, h.Hoja);
        var original = doc.GetPage(h.Hoja);
        var hoja = Recibos.LeerHoja(Recibos.Palabras(original), original.Width);
        if (hoja.Firma is null) throw new FormatException("no encontré el espacio de la firma");
        Recibos.Estampar(copia, b.AddTrueTypeFont(Recibos.Manuscrita.Value), hoja.Firma, Recibos.Iniciales(hoja.Nombre));
        return (b.Build(), hoja.Firma, original.Width, original.Height);
    }

    /// <summary>Escribe una cosa del plan. Con CreateNew: si mientras tanto apareció un archivo con ese nombre,
    /// falla en vez de pisarlo. La lista es lo único que se reemplaza, y solo si la escribió la app.</summary>
    public static async Task Escribir(Escritura e, Revision r, Corregir corregir)
    {
        var carpeta = Path.GetDirectoryName(e.Ruta)!;
        if (!Directory.Exists(carpeta)) throw new IOException($"ya no está la carpeta {Path.GetFileName(carpeta)}");
        if (e.Accion == Accion.Carpeta)
        {
            // Una sola, adentro de la del mes, que ya existe: CreateDirectory crearía también las intermedias.
            Directory.CreateDirectory(e.Ruta);
            return;
        }
        if (e.Accion == Accion.Lista)
        {
            if (File.Exists(e.Ruta) && !EsNuestra(e.Ruta)) throw new IOException("ya hay otro archivo con ese nombre");
            // Con BOM: el Bloc de notas viejo muestra bien los acentos.
            await File.WriteAllTextAsync(e.Ruta, Lista(r), Encoding.UTF8);
            return;
        }

        var b = new PdfDocumentBuilder();
        // El CUIL y el período en las palabras clave: la próxima revisión lo encuentra sin OCR. «iniciales» marca
        // los que llevan las del estudio, para no contarlos como firmados por el empleado.
        b.DocumentInformation.Keywords = $"{e.Empleado!.Cuil} {r.Mes:00}/{r.Anio}" + (e.Accion == Accion.Iniciales ? " iniciales" : "");
        var docs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            PdfDocumentBuilder.AddedFont? fuente = null;
            foreach (var h in e.Hojas)
            {
                if (e.Accion == Accion.Corregido)
                {
                    var (jpeg, ancho, alto) = await corregir(h);
                    // Entera, centrada y con un margen chico, en una hoja A4 parada o acostada como la foto.
                    var parada = alto >= ancho;
                    var pagina = b.AddPage(PageSize.A4, parada);
                    double pw = parada ? 595 : 842, ph = parada ? 842 : 595, margen = 18;
                    var escala = Math.Min((pw - 2 * margen) / ancho, (ph - 2 * margen) / alto);
                    double w = ancho * escala, hh = alto * escala, x = (pw - w) / 2, y = (ph - hh) / 2;
                    pagina.AddJpeg(jpeg, new PdfRectangle(x, y, x + w, y + hh));
                    continue;
                }
                // Las iniciales: una copia de la hoja sin firmar, con las del estudio en el espacio de la firma. El
                // original se abre solo para leer.
                if (!docs.TryGetValue(h.Ruta, out var doc)) docs[h.Ruta] = doc = PdfDocument.Open(h.Ruta);
                var copia = b.AddPage(doc, h.Hoja);
                var original = doc.GetPage(h.Hoja);
                // Las iniciales salen del nombre como lo escribe el recibo, igual que al separar a mano.
                var hoja = Recibos.LeerHoja(Recibos.Palabras(original), original.Width);
                if (hoja.Firma is null) throw new FormatException("no encontré el espacio de la firma");
                fuente ??= b.AddTrueTypeFont(Recibos.Manuscrita.Value);
                Recibos.Estampar(copia, fuente, hoja.Firma, Recibos.Iniciales(hoja.Nombre));
            }
            var bytes = b.Build();
            var salida = new FileStream(e.Ruta, FileMode.CreateNew, FileAccess.Write);
            try
            {
                await using (salida) await salida.WriteAsync(bytes);
            }
            catch
            {
                // Lo único que la app borra: el archivo que acaba de crear ella misma (CreateNew garantiza que no
                // existía) y que quedó a medias porque se cortó la red. Un PDF roto parecería un recibo.
                try { File.Delete(e.Ruta); } catch { }
                throw;
            }
        }
        finally
        {
            foreach (var d in docs.Values) d.Dispose();
        }
    }
}
