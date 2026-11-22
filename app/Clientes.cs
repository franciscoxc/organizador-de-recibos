using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

// Lo que dijo de una hoja (o de una foto) el texto del PDF o el OCR. Del OCR quedan también el giro y la
// inclinación con que se leyó: con eso se endereza después, sin volver a leerla. Nombre: el «APELLIDO Y NOMBRE»
// de un recibo con texto, y Empleador, su primera línea. AFondo: ya pasó por la lectura a fondo; si ahí tampoco apareció
// nadie, no se reintenta. Huella: la de la imagen que leyó el OCR; dos hojas con la misma son la misma imagen.
public record Lectura(string Texto, bool Ocr = false, int Giro = 0, double Angulo = 0, string? Nombre = null, bool AFondo = false,
    string? Empleador = null, string? Huella = null);

// Una hoja de un archivo; la hoja 0 es una foto.
public record Hallazgo(string Ruta, int Hoja, Lectura Lectura);

// Las fotos y los escaneos los lee el OCR (ver Lector.cs): el motor no sabe de OCR. «buscado» dice si el texto ya trae
// a alguien de la nómina; mientras no, la lectura a fondo prueba otros giros y más ampliación.
public delegate Task<Lectura> LeerImagen(string ruta, int hoja, Func<string, bool> buscado);

// La huella de una foto o de una hoja escaneada (ver WindowsOcr.Huella): con ella, la misma imagen guardada dos veces,
// o repetida en varios PDF, pasa por el OCR una sola vez.
public delegate Task<string?> SacarHuella(string ruta, int hoja);

// La versión que se guarda de una foto o de una hoja escaneada: girada, derecha y con contraste, en JPEG.
public delegate Task<(byte[] Jpeg, int Ancho, int Alto)> Corregir(Hallazgo h);

public enum Firma { Falta, Firmado, PorNombre, ConIniciales }

// Baja: si se fue en el mes, la fecha de cese («01/10/2026»), o vacío si se sabe que se fue pero no cuándo. Su recibo
// del mes es la liquidación final, que también tiene que volver firmada.
public record Renglon(Empleado Empleado, Firma Firma, List<Hallazgo> Firmado, List<Hallazgo> SinFirmar, string? Baja = null)
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
    /// <summary>Las fotos y las hojas escaneadas de los firmados que no se leyeron porque la revisión fue sin OCR (la
    /// primera pasada, la rápida): mientras queden, lo que falta puede estar ahí.</summary>
    public int PorLeer { get; init; }
    /// <summary>Los recibos de otro mes que están entre los firmados: no cuentan como firmados de nadie.</summary>
    public List<DeOtroMes> OtrosMeses { get; init; } = [];
    /// <summary>Los que todavía no están en su carpeta: los que hay que resolver.</summary>
    public IEnumerable<DeOtroMes> DeOtroMesPendientes => OtrosMeses.Where(o => !o.EnSuCarpeta);
    /// <summary>Lo que se puede llevar a su mes: un PDF por empleado y mes, con sus hojas.</summary>
    public List<ASuMes> ALlevar { get; init; } = [];
}

// Un recibo de otro mes entre los firmados del que se revisa (dice «PERIODO DE PAGO 09/2026» en la carpeta de octubre).
// Anio: el suyo (ver AnioDe). Cuils: de quién es, con la nómina de su mes si se encontró su carpeta (el que se fue antes
// no está en la del mes que se revisa), y si no con la de este. PorConfirmar: el período salió del texto que trae pegado
// un escaneo (el del escáner, que a veces lee mal) y todavía no lo leyó la app. EnSuCarpeta: ya está en los firmados de
// su mes, con su nombre.
public record DeOtroMes(Hallazgo Hoja, int Mes, int Anio, List<string> Cuils, bool PorConfirmar = false, bool EnSuCarpeta = false);

// Lo que se puede llevar a su mes: las hojas de otro mes de un empleado, y el PDF que se arma con ellas, derechas y con
// contraste como los corregidos. Archivo: su nombre («PEREZ JUAN 09 2026.pdf»). Carpeta: los firmados de ese mes, los que
// hay o los que se crearían; null si no está la carpeta de ese mes. PorNombre: en alguna hoja se lo encontró solo por el
// nombre.
public record ASuMes(Empleado Empleado, int Mes, int Anio, string Archivo, string? Carpeta, List<Hallazgo> Hojas, bool PorNombre, bool EnSuCarpeta)
{
    public string? Ruta => Carpeta is null ? null : Path.Combine(Carpeta, Archivo);
}

// Acomodar: el original de la carpeta de descargas pasa a «Acomodados», una vez guardados sus recibos (ver Descargas).
public enum Accion { Carpeta, Corregido, Iniciales, Consolidado, Lista, ASuMes, Acomodar }

public record Escritura(Accion Accion, string Ruta, Empleado? Empleado, List<Hallazgo> Hojas);

// Lo leído de cada archivo, por ruta, tamaño y fecha: la segunda revisión lee solo lo nuevo. El OCR de una
// foto tarda un segundo y la carpeta de los clientes suele venir por la red.
public sealed class Memoria
{
    public record Entrada(long Tamano, DateTime Fecha, string Claves, List<Lectura> Hojas);

    readonly string ruta;
    readonly Dictionary<string, Entrada> datos;

    /// <summary>«anterior» es la memoria de la versión 1.1.0, que era una sola para todos los meses. La primera vez que
    /// se abre un mes se traen de ahí sus lecturas con OCR (las de los archivos que acepta «deEsteMes»), para no leer
    /// de nuevo todas las fotos. Lo que era texto de un PDF no se trae: se relee en un momento, y así viene con el
    /// empleador, que antes no se guardaba.</summary>
    public Memoria(string ruta, string? anterior = null, Func<string, bool>? deEsteMes = null)
    {
        this.ruta = ruta;
        if (Leer(ruta) is { } guardadas) datos = guardadas;
        else
        {
            var heredadas = anterior is null ? null : Leer(anterior)?.Where(e => deEsteMes?.Invoke(e.Key) != false && e.Value.Hojas.Any(h => h.Ocr));
            datos = new(heredadas ?? [], StringComparer.OrdinalIgnoreCase);
        }
    }

    static Dictionary<string, Entrada>? Leer(string ruta)
    {
        try { return new(JsonSerializer.Deserialize<Dictionary<string, Entrada>>(File.ReadAllText(ruta))!, StringComparer.OrdinalIgnoreCase); }
        catch { return null; } // la primera vez no hay nada guardado
    }

    // Con candado: al cambiar de mes, la revisión anterior puede estar terminando su último archivo.
    public Entrada? Buscar(FileInfo f)
    {
        lock (datos) return datos.TryGetValue(f.FullName, out var e) && e.Tamano == f.Length && e.Fecha == f.LastWriteTimeUtc ? e : null;
    }

    /// <summary>La lectura de una hoja igual (la misma imagen) que ya se leyó adentro de esa carpeta, en otro archivo o
    /// en otra hoja; si alguna ya pasó por la lectura a fondo, esa. Vale aunque ese archivo ya no esté: la huella es de la
    /// imagen, no del archivo.</summary>
    public Lectura? ConHuella(string carpeta, string huella)
    {
        var adentro = carpeta + Path.DirectorySeparatorChar;
        Lectura? igual = null;
        lock (datos)
            foreach (var (ruta, e) in datos)
                if (ruta.StartsWith(adentro, StringComparison.OrdinalIgnoreCase))
                    foreach (var h in e.Hojas)
                        if (h.Huella == huella && (igual is null || h.AFondo && !igual.AFondo)) igual = h;
        return igual;
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
    public static string RaizDelAnio(string elegida, int anio)
    {
        // El año de la ruta es el último que nombra, y se cambia en todos los lugares donde está («…\ESTUDIO 2026\CLIENTES
        // 2026»). Otro año más arriba («…\ESTUDIO 2019\CLIENTES 2026») es parte de un nombre: cambiándolo también, se
        // buscaba una carpeta que no existe.
        var ultimo = Regex.Match(elegida, @"(?<!\d)(19|20)\d{2}(?!\d)", RegexOptions.RightToLeft);
        return ultimo.Success ? Regex.Replace(elegida, $@"(?<!\d){ultimo.Value}(?!\d)", anio.ToString()) : elegida;
    }

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

    internal static bool EsPdf(FileInfo f) => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    internal static bool EsImagen(FileInfo f) => Imagenes.Contains(f.Extension.ToLowerInvariant());
    static bool EsNomina(FileInfo f) => EsPdf(f) && Normal(f.Name).Contains("NOMINA");

    // Sin los ocultos ni los del sistema (Thumbs.db), y sin frenarse en una subcarpeta sin permiso.
    internal static List<FileInfo> Archivos(string carpeta)
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

    /// <summary>Los empleados que aparecen en la hoja sin su CUIL entero: por el nombre (al menos dos de sus palabras, o
    /// todas si tiene menos), o por un CUIL casi igual con algo del nombre. Una hoja puede traer varios recibos,
    /// escaneados de a dos, y cada uno se busca por su cuenta; los que ya están por su CUIL no se repiten. «empleador»
    /// es como lo nombran los recibos.</summary>
    public static List<Empleado> Nombrados(string texto, IReadOnlyList<Empleado> nomina, ICollection<string> yaEstan, string empleador = "")
    {
        var palabras = Partes(texto).ToHashSet();
        // ponytail-keep: las palabras que ya tienen dueño en la hoja no nombran a nadie más. Son las del empleador, que
        // encabeza todos los recibos, y las de los que están por su CUIL. Sin esto, con «GOMEZ MARIA» por su CUIL, «GOMEZ
        // MARIA JOSE» figuraba en la hoja de ella; y a un empleado con el apellido y un nombre del empleador (su hijo), o
        // con el apellido del empleador y el nombre de un compañero, se lo encontraba en los recibos de todos: nunca le
        // faltaba el suyo.
        var conDueno = Partes(empleador + " " + string.Join(" ", nomina.Where(e => yaEstan.Contains(e.Cuil)).Select(e => e.Nombre))).ToHashSet();
        var presentes = nomina.Select(e => (Empleado: e, Total: Partes(e.Nombre).Length, Estan: Partes(e.Nombre).Where(palabras.Contains).ToHashSet())).ToList();
        // Si dos comparten palabras («PEREZ JUAN CARLOS» y «PEREZ JUAN MARTIN»), cuenta el que tiene más en la hoja: el
        // otro aparece solo por lo que comparten.
        var lista = presentes
            .Where(t => t.Total > 0 && t.Estan.Count >= Math.Min(2, t.Total) && !t.Estan.IsSubsetOf(conDueno)
                && !presentes.Any(o => t.Estan.IsProperSubsetOf(o.Estan)))
            .Select(t => t.Empleado).Where(e => !yaEstan.Contains(e.Cuil)).ToList();
        if (PorCuilParecido(texto, nomina) is { } parecido && !yaEstan.Contains(parecido.Cuil) && !lista.Contains(parecido)) lista.Add(parecido);
        return lista;
    }

    /// <summary>Los de la nómina que están en una hoja sin su CUIL entero, además de los que están por su CUIL («porCuil»):
    /// los de Nombrados y, si no hay nadie, el del nombre del archivo. Los que se encuentran así quedan para revisar. Lo usan
    /// la revisión de los firmados y Separar, con las hojas que no pudo leer como recibo.</summary>
    public static List<Empleado> QuienesSinCuil(string texto, string archivo, int hojas, IReadOnlyList<Empleado> nomina,
        ICollection<string> porCuil, string empleador)
    {
        var lista = Nombrados(texto, nomina, porCuil, empleador);
        // Lo último: el nombre del archivo. Las fotos se guardan con el del empleado («PEREZ JUAN RECIBO OCT 2026.jpeg»), y
        // en octubre las que el OCR no leía eran justo esas. Solo en archivos de una hoja (un PDF con varios recibos se llama
        // como uno solo, o como ninguno), y nunca en una baja, un alta o un DNI.
        if (porCuil.Count == 0 && lista.Count == 0 && hojas == 1 && !EsOtroDocumento(Path.GetFileName(archivo) + " " + texto)
            && PorNombre(Path.GetFileNameWithoutExtension(archivo), nomina) is { } q)
            lista.Add(q);
        return lista;
    }

    /// <summary>Una constancia de ARCA (alta, baja o modificación): nunca es un recibo, aunque traiga el CUIL.</summary>
    public static bool EsConstancia(string texto) => Normal(texto).Contains("CONSTANCIA DEL TRABAJADOR");

    // El período de un recibo, «mm/aaaa» suelto («PERIODO DE PAGO 09/2026»). Una fecha entera («01/09/2026») no cuenta.
    static readonly Regex Periodos = new(@"(?<![\d/.,])(0?[1-9]|1[0-2])\s?/\s?(20\d{2})(?![\d/])", RegexOptions.Compiled);

    /// <summary>El mes de un recibo: el período que más se repite en el texto. null si no trae ninguno.</summary>
    public static int? MesDelRecibo(string texto)
    {
        var meses = Periodos.Matches(texto).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return meses.Count == 0 ? null : meses.GroupBy(m => m).MaxBy(g => g.Count())!.Key;
    }

    /// <summary>Si el texto es de un recibo de otro mes, de cuál: trae algún período y ninguno es el del mes que se
    /// revisa. Sin ningún período a la vista devuelve null: no se sabe, y la hoja cuenta como siempre.</summary>
    public static int? OtroMes(string texto, int mes)
    {
        var meses = Periodos.Matches(texto).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        // ponytail-keep: se compara el mes y no el año. En las fotos borrosas de octubre de 2026 el año salió 2020, 2025 y
        // 2028 en 5 de 440 hojas, con el mes bien leído: comparándolo, esos cinco firmados habrían dejado de contar.
        return meses.Count == 0 || meses.Contains(mes) ? null : meses.GroupBy(m => m).MaxBy(g => g.Count())!.Key;
    }

    /// <summary>La nómina de un mes, donde la deja el estudio: en la carpeta del mes o en cualquiera de las que tiene adentro
    /// (suele estar en «SUSS Y SIND»), menos en la de los firmados. Puede venir partida en varios PDF («NOMINA 1 07 2026»,
    /// «NOMINA 2 07 2026»): se juntan. La usan la revisión de los clientes y Separar. «archivos»: los de la carpeta, si ya
    /// se recorrió. Los avisos vuelven aparte, para quien los muestre.</summary>
    public static async Task<(Nomina? Nomina, List<string> Avisos)> NominaDelMes(string carpetaMes, int mes, int anio, Ocr? ocr,
        Action<string> avisar, CancellationToken corte, List<FileInfo>? archivos = null)
    {
        var avisos = new List<string>();
        var firmados = Subcarpetas(carpetaMes).Where(EsDeFirmados).ToList();
        Nomina? nomina = null;
        foreach (var f in (archivos ?? Archivos(carpetaMes)).Where(f => EsNomina(f) && !Dentro(firmados, f)))
        {
            corte.ThrowIfCancellationRequested();
            try
            {
                // Cada nómina se lee una vez por archivo mientras la app está abierta: la revisión la pide dos veces por
                // cliente (la pasada sin OCR y la de las fotos) y otra en cada «Revisar de nuevo».
                // shortcut: no queda guardada al cerrar; las escaneadas (las de julio y agosto) se leen con OCR una vez
                // por sesión. Si revisar esos meses se vuelve habitual, guardarlas en la memoria como las fotos.
                var clave = (f.FullName.ToUpperInvariant(), f.Length, f.LastWriteTimeUtc, ocr is not null);
                if (!Nominas.TryGetValue(clave, out var leida))
                {
                    var deEsta = new List<string>();
                    var nueva = await Recibos.LeerNominaPdf(f.FullName, ocr, m =>
                    {
                        if (m.StartsWith('⚠')) deEsta.Add(m[2..]);
                        else avisar(m);
                    });
                    Nominas[clave] = leida = (nueva, deEsta);
                }
                avisos.AddRange(leida.Avisos);
                if (leida.Nomina is not { } n) continue;
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
        return (nomina, avisos);
    }

    static readonly ConcurrentDictionary<(string Ruta, long Tamano, DateTime Fecha, bool ConOcr), (Nomina? Nomina, List<string> Avisos)> Nominas = new();

    /// <summary>Cómo escriben a cada uno, por CUIL, los recibos sin firmar del mes, los que arma el estudio con texto: el
    /// «APELLIDO NOMBRE» y el empleador, como en Renglon.Nombre. La nómina de AFIP a veces pone el nombre primero: Separar
    /// nombra así lo que encontró con ella.</summary>
    public static Dictionary<string, Hoja> RecibosSinFirmar(string carpetaMes, IReadOnlyList<Empleado> nomina)
    {
        var firmados = Subcarpetas(carpetaMes).Where(EsDeFirmados).ToList();
        var recibos = new Dictionary<string, Hoja>();
        foreach (var f in Archivos(carpetaMes).Where(f => EsPdf(f) && !EsNomina(f) && !Dentro(firmados, f)))
            try
            {
                using var doc = PdfDocument.Open(f.FullName);
                foreach (var p in doc.GetPages())
                {
                    var texto = string.Join(" ", p.GetWords().Select(w => w.Text));
                    if (Recibo(p, texto) is { } h)
                        foreach (var c in DeLaNomina(texto, nomina.Select(e => e.Cuil))) recibos.TryAdd(c, h);
                }
            }
            catch { } // un PDF dañado o ajeno no frena: queda el nombre de la nómina
        return recibos;
    }

    static bool Dentro(IEnumerable<string> carpetas, FileInfo f) =>
        carpetas.Any(c => f.FullName.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

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
    // «unaVez» lee la hoja o devuelve lo ya leído de una imagen igual; «falta» avisa de cada hoja que queda sin leer por
    // no haber OCR en esta pasada.
    internal static async Task<Memoria.Entrada?> Leer(FileInfo f, bool firmado, LeerImagen? leer, Memoria memoria, Func<string, bool> buscado, Action<string> avisar, CancellationToken corte,
        Func<string, int, Func<Task<Lectura>>, Task<Lectura>>? unaVez = null, Action? falta = null)
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
                if (texto.Length > 0 || !firmado || porClaves)
                {
                    var recibo = Recibo(p, texto);
                    lecturas.Add(Task.FromResult(new Lectura(texto, Nombre: recibo?.Nombre, Empleador: recibo?.Empleador)));
                }
                else if (leer is null)
                {
                    completa = false;
                    falta?.Invoke();
                    lecturas.Add(Task.FromResult(new Lectura("")));
                }
                else
                {
                    var numero = p.Number;
                    Task<Lectura> Leerla() => ConTurno(() =>
                    {
                        avisar($"{f.Name}, hoja {numero}");
                        return leer(f.FullName, numero, buscado);
                    }, corte);
                    lecturas.Add(unaVez is null ? Leerla() : unaVez(f.FullName, numero, Leerla));
                }
            }
            hojas.AddRange(await Task.WhenAll(lecturas));
        }
        else if (firmado && EsImagen(f))
        {
            if (leer is null)
            {
                falta?.Invoke();
                return null;
            }
            Task<Lectura> Leerla() => ConTurno(() =>
            {
                avisar(f.Name);
                return leer(f.FullName, 0, buscado);
            }, corte);
            hojas.Add(await (unaVez is null ? Leerla() : unaVez(f.FullName, 0, Leerla)));
        }
        else return null;
        if (completa) memoria.Anotar(f, claves, hojas);
        return new(f.Length, f.LastWriteTimeUtc, claves, hojas);
    }

    // Una hoja de recibo con texto, leída como al separar a mano: de ahí salen el «APELLIDO Y NOMBRE» y el empleador.
    static Hoja? Recibo(Page p, string texto)
    {
        if (!Normal(texto).Contains("FIRMA DEL EMPLEADO")) return null;
        try { return Recibos.LeerHoja(Recibos.Palabras(p), p.Width); }
        catch (FormatException) { return null; } // otro formato: queda el nombre de la nómina
    }

    /// <summary>Revisa un cliente en un mes. Solo lee: lo que haya que guardar lo arma Planear.</summary>
    public static async Task<Revision> Revisar(string cliente, int mes, int anio, Ocr? ocr, LeerImagen? leer, Memoria memoria,
        Action<string> avisar, CancellationToken corte, SacarHuella? huella = null)
    {
        var nombre = Path.GetFileName(cliente);
        var avisos = new List<string>();
        var carpetaMes = CarpetaDelMes(cliente, mes, anio);
        if (carpetaMes is null) return new(nombre, mes, anio, null, null, null, [], [], avisos);

        var archivos = Archivos(carpetaMes);
        var firmados = Subcarpetas(carpetaMes).Where(EsDeFirmados).ToList();
        // Si hay más de una, se escribe en la que nombra el mes («RECIBOS FIRMADOS OCTUBRE»).
        var destino = firmados.FirstOrDefault(c => Contiene(c, Meses[mes - 1])) ?? firmados.FirstOrDefault();
        bool EnFirmados(FileInfo f) => Dentro(firmados, f);

        var (nomina, deLaNomina) = await NominaDelMes(carpetaMes, mes, anio, ocr, m => avisar($"{nombre}: {m}"), corte, archivos);
        avisos.AddRange(deLaNomina);
        if (nomina is null) return new(nombre, mes, anio, carpetaMes, null, destino, [], [], avisos);

        var empleados = nomina.Empleados;
        var porCuil = empleados.ToDictionary(e => e.Cuil);
        bool Buscado(string texto) => DeLaNomina(texto, porCuil.Keys).Count > 0;
        var firmado = porCuil.Keys.ToDictionary(c => c, _ => new List<Hallazgo>());
        var sinFirmar = porCuil.Keys.ToDictionary(c => c, _ => new List<Hallazgo>());
        var deLaApp = new HashSet<Hallazgo>(); // con las iniciales que estampó el estudio
        // Quién se encontró en cada hoja sin su CUIL entero, de a (hoja, CUIL): en una hoja con dos recibos, uno puede
        // estar por su CUIL y el otro solo por el nombre, y la marca de revisar es solo del segundo.
        var porNombre = new HashSet<(Hallazgo, string)>();
        var bajas = new Dictionary<string, string>(); // CUIL → fecha de cese, o vacío si no se sabe
        var ilegibles = new List<Hallazgo>();
        var ajenos = new List<(Hallazgo Hoja, int Mes, string Claves, int Hojas, bool PorConfirmar)>();

        var porLeer = 0;
        var unaVez = huella is null ? null : UnaVez(huella, memoria, carpetaMes, corte);

        // De a varios archivos a la vez, uno por núcleo: el OCR de una foto usa uno solo, y de a uno la primera
        // revisión dejaba ocioso el resto del procesador (25 % en una PC de cuatro). Después se recorren en orden.
        var lista = archivos.Where(f => !EsNomina(f) || EnFirmados(f)).ToList();
        var leidos = new Memoria.Entrada?[lista.Count];
        var errores = new string?[lista.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, lista.Count), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = corte },
            async (i, _) =>
            {
                try
                {
                    leidos[i] = await Leer(lista[i], EnFirmados(lista[i]), leer, memoria, Buscado, m => avisar($"{nombre}: {m}"), corte,
                        unaVez, () => Interlocked.Increment(ref porLeer));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { errores[i] = $"{lista[i].Name}: {ex.Message}"; }
            });
        avisos.AddRange(errores.OfType<string>());
        if (huella is not null) await CompletarHuellas(lista, leidos, memoria, huella, corte);
        // Cómo nombran al empleador los recibos con texto (los que arma el estudio): así está escrito también en las fotos.
        var empleador = string.Join(" ", leidos.SelectMany(e => e?.Hojas ?? []).Select(l => l.Empleador).Distinct());
        foreach (var (f, e) in lista.Zip(leidos))
        {
            if (e is null) continue;
            var enFirmados = EnFirmados(f);
            for (var i = 0; i < e.Hojas.Count; i++)
            {
                var h = new Hallazgo(f.FullName, EsPdf(f) ? i + 1 : 0, e.Hojas[i]);
                var cuils = DeLaNomina(e.Claves + " " + h.Lectura.Texto, porCuil.Keys);
                // Una constancia de ARCA (alta, baja o modificación) nunca es un recibo, aunque traiga el CUIL: entre los
                // firmados contaba como el firmado de esa persona, y una baja firmada tapaba que faltaba la liquidación
                // final. La de una baja, además, dice que se fue y cuándo.
                if (EsConstancia(h.Lectura.Texto))
                {
                    if (Regex.Match(Normal(h.Lectura.Texto), @"FECHA CESE:?\s*(\d{2}/\d{2}/\d{4})") is { Success: true } cese)
                        foreach (var c in cuils) bajas[c] = cese.Groups[1].Value;
                    continue;
                }
                if (!enFirmados)
                {
                    // Afuera de los firmados cuenta solo lo que es un recibo: el resumen y la nómina también traen
                    // los CUIL, pero no el espacio para la firma.
                    if (Normal(h.Lectura.Texto).Contains("FIRMA DEL EMPLEADO"))
                        foreach (var c in cuils) sinFirmar[c].Add(h);
                    continue;
                }
                // Un recibo de otro mes no es el firmado de nadie, y se avisa: en octubre de 2026 un cliente mandó cinco de
                // septiembre, y uno pasaba por el firmado de alguien que no había firmado su liquidación final. Si el
                // período sale del texto que trae pegado un escaneo, queda por confirmar: lo lee la app, a fondo.
                if (OtroMes(h.Lectura.Texto, mes) is { } mesAjeno)
                {
                    // De quién es y adónde va se ve al final, con la nómina de su mes.
                    ajenos.Add((h, mesAjeno, e.Claves, e.Hojas.Count, !h.Lectura.Ocr && !h.Lectura.AFondo && Escaneada(h)));
                    continue;
                }
                // ponytail-keep: a cada uno de los que faltan, aunque la hoja ya haya dado con alguien por su CUIL. Antes
                // se buscaba por el nombre solo en las hojas sin nadie: en un escaneo de dos recibos por hoja, el CUIL de
                // una empleada salió entero y el del otro no, la hoja quedó de ella y él figuraba sin firma aunque su
                // nombre estaba en el texto. Los que se encuentran así quedan para revisar.
                foreach (var otro in QuienesSinCuil(h.Lectura.Texto, f.FullName, e.Hojas.Count, empleados, cuils, empleador))
                {
                    cuils.Add(otro.Cuil);
                    porNombre.Add((h, otro.Cuil));
                }
                // Lo que la app guardó encontrado por el nombre sigue así: ese CUIL lo puso ella, no se leyó. Hasta que
                // aparezca en el texto (la lectura a fondo de la copia derecha puede leerlo).
                if (e.Claves.Contains("por nombre"))
                    foreach (var c in cuils.Except(DeLaNomina(h.Lectura.Texto, porCuil.Keys))) porNombre.Add((h, c));
                // En el consolidado que arma la app, los encontrados solo por el nombre llevan «POR NOMBRE» delante del
                // CUIL: también ese lo escribió ella.
                foreach (Match m in Regex.Matches(h.Lectura.Texto, @"POR NOMBRE (\S+)"))
                    foreach (var c in DeLaNomina(m.Groups[1].Value, cuils)) porNombre.Add((h, c));
                if (cuils.Count == 0)
                {
                    ilegibles.Add(h);
                    continue;
                }
                if (e.Claves.Contains("iniciales")) deLaApp.Add(h);
                foreach (var c in cuils) firmado[c].Add(h);
            }
        }

        // Sin constancia, la liquidación final se reconoce por el nombre del archivo del recibo sin firmar («LIQ FINAL
        // PEREZ 01 10 2026.PDF»): su texto es igual al de un recibo cualquiera.
        foreach (var (cuil, hs) in sinFirmar)
            if (!bajas.ContainsKey(cuil) && hs.Select(h => Normal(Path.GetFileNameWithoutExtension(h.Ruta)))
                    .FirstOrDefault(n => Regex.IsMatch(n, @"\bLIQ(UIDACION)?\b\.?\s*FINAL\b")) is { } archivo)
                bajas[cuil] = Regex.Match(archivo, @"\b(\d{2})[ ./-](\d{2})[ ./-](\d{4})\b") is { Success: true } m
                    ? $"{m.Groups[1]}/{m.Groups[2]}/{m.Groups[3]}" : "";

        var renglones = empleados.Select(x =>
        {
            var hs = firmado[x.Cuil];
            var firma = hs.Count == 0 ? Firma.Falta
                : hs.All(deLaApp.Contains) ? Firma.ConIniciales
                // Lo que estampó el estudio no dice si firmó: si lo demás es todo por el nombre, sigue para revisar. Con
                // la copia con iniciales al lado, una foto encontrada solo por el nombre pasaba por firmada.
                : hs.Where(h => !deLaApp.Contains(h)).All(h => porNombre.Contains((h, x.Cuil))) ? Firma.PorNombre
                : Firma.Firmado;
            return new Renglon(x, firma, hs, sinFirmar[x.Cuil], bajas.GetValueOrDefault(x.Cuil));
        }).ToList();
        var (otros, llevar) = await DeOtrosMeses(cliente, mes, anio, ajenos, empleados, renglones, empleador, ocr, corte);
        return new(nombre, mes, anio, carpetaMes, nomina, destino, renglones, ilegibles, avisos) { PorLeer = porLeer, OtrosMeses = otros, ALlevar = llevar };
    }

    /// <summary>Lee cada imagen una sola vez: si ya está leída adentro de «carpeta» (por su huella) se toma esa lectura, y
    /// si se está leyendo ahora, se la espera.</summary>
    // En octubre de 2026 un cliente tenía 125 hojas para el OCR y solo 38 imágenes distintas (las mismas fotos bajadas varias
    // veces y juntadas en PDF).
    internal static Func<string, int, Func<Task<Lectura>>, Task<Lectura>> UnaVez(SacarHuella huella, Memoria memoria, string carpeta, CancellationToken corte)
    {
        var enCurso = new ConcurrentDictionary<string, Lazy<Task<Lectura>>>();
        return async (ruta, hoja, leerla) =>
        {
            string? h = null;
            // Con Task.Run: sacar la huella es dibujar la hoja, y sin él cada una se dibujaba antes de lanzar la siguiente.
            try { h = await Task.Run(() => huella(ruta, hoja), corte); }
            catch (Exception e) when (e is not OperationCanceledException) { } // sin huella, se lee como siempre
            if (h is null) return await leerla();
            if (memoria.ConHuella(carpeta, h) is { } igual) return igual;
            return await enCurso.GetOrAdd(h, _ => new Lazy<Task<Lectura>>(async () => (await leerla()) with { Huella = h })).Value;
        };
    }

    // Lo leído antes de que existiera la huella no la tiene: se le saca ahora, una vez (unos 15 ms por hoja), y queda en la
    // memoria. Sin ella, las copias de esas hojas no se reconocían: en un cliente de octubre de 2026, el PDF corregido de una
    // empleada llevaba 9 hojas para una liquidación de 2.
    internal static async Task CompletarHuellas(List<FileInfo> archivos, Memoria.Entrada?[] leidos, Memoria memoria, SacarHuella huella, CancellationToken corte)
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, archivos.Count), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = corte },
            async (i, _) =>
            {
                if (leidos[i] is not { } e) return;
                for (var hoja = 0; hoja < e.Hojas.Count; hoja++)
                {
                    if (e.Hojas[hoja] is not { Ocr: true, Huella: null } l) continue;
                    string? h = null;
                    var numero = EsPdf(archivos[i]) ? hoja + 1 : 0;
                    try { h = await Task.Run(() => huella(archivos[i].FullName, numero), corte); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { } // sin huella, sigue como estaba
                    if (h is null) continue;
                    e.Hojas[hoja] = l with { Huella = h };
                    memoria.Reemplazar(archivos[i], hoja, e.Hojas[hoja]);
                }
            });
    }

    /// <summary>El año de un recibo de otro mes: el que lo deja más cerca del mes que se revisa (septiembre, revisando
    /// octubre de 2026, es de 2026; diciembre, revisando enero de 2027, es de 2026). A igual distancia, el pasado.</summary>
    // El año que lee el OCR no se usa: en las fotos borrosas salió mal en 5 de 440 hojas, con el mes bien leído.
    public static int AnioDe(int suMes, int mes, int anio)
    {
        var antes = (mes - suMes + 12) % 12; // cuántos meses atrás queda
        return antes <= 6 ? (suMes <= mes ? anio : anio - 1) : (suMes > mes ? anio : anio + 1);
    }

    /// <summary>Los firmados de un mes: si hay más de una carpeta, la que nombra el mes; si no hay ninguna, la que se
    /// crearía («RECIBOS FIRMADOS SEPTIEMBRE»).</summary>
    internal static string FirmadosDelMes(string carpetaMes, int mes)
    {
        var hay = Subcarpetas(carpetaMes).Where(EsDeFirmados).ToList();
        return hay.FirstOrDefault(c => Contiene(c, Meses[mes - 1])) ?? hay.FirstOrDefault() ?? Path.Combine(carpetaMes, $"RECIBOS FIRMADOS {Meses[mes - 1]}");
    }

    // De quién es cada recibo de otro mes y adónde iría: los firmados de su mes, con el nombre que les pone la revisión de
    // ese mes (el de sus recibos sin firmar). La nómina de su mes encuentra también al que ya no está en la de este.
    static async Task<(List<DeOtroMes>, List<ASuMes>)> DeOtrosMeses(string cliente, int mes, int anio,
        List<(Hallazgo Hoja, int Mes, string Claves, int Hojas, bool PorConfirmar)> ajenos, List<Empleado> empleados, List<Renglon> renglones,
        string empleador, Ocr? ocr, CancellationToken corte)
    {
        var otros = new List<DeOtroMes>();
        var llevar = new List<ASuMes>();
        foreach (var delMes in ajenos.GroupBy(a => a.Mes))
        {
            var suMes = delMes.Key;
            var suAnio = AnioDe(suMes, mes, anio);
            var suCarpeta = CarpetaDelMes(suAnio == anio ? cliente : RaizDelAnio(cliente, suAnio), suMes, suAnio);
            Nomina? suNomina = null;
            if (suCarpeta is not null)
                try { suNomina = (await NominaDelMes(suCarpeta, suMes, suAnio, ocr, _ => { }, corte)).Nomina; }
                catch (Exception e) when (e is not OperationCanceledException) { } // sin su nómina, con la de este mes
            var quienes = suNomina?.Empleados ?? empleados;
            var sinFirmar = suNomina is null ? [] : RecibosSinFirmar(suCarpeta!, quienes);
            string Nombre(Empleado x) => Recibos.SinComa(sinFirmar.GetValueOrDefault(x.Cuil)?.Nombre
                ?? renglones.FirstOrDefault(y => y.Empleado.Cuil == x.Cuil)?.Nombre ?? x.Nombre);
            var firmados = suCarpeta is null ? null : FirmadosDelMes(suCarpeta, suMes);
            var hojas = new Dictionary<string, (Empleado Empleado, List<Hallazgo> Hojas, bool PorNombre)>();
            var paginas = new List<(Hallazgo Hoja, List<string> Cuils, bool PorConfirmar)>();
            foreach (var a in delMes)
            {
                var porCuil = DeLaNomina(a.Claves + " " + a.Hoja.Lectura.Texto, quienes.Select(q => q.Cuil));
                var porSuNombre = QuienesSinCuil(a.Hoja.Lectura.Texto, a.Hoja.Ruta, a.Hojas, quienes, porCuil, empleador);
                paginas.Add((a.Hoja, porCuil.Concat(porSuNombre.Select(q => q.Cuil)).ToList(), a.PorConfirmar));
                // Lo que falta confirmar (el período del texto del escáner) no se lleva a ningún lado.
                if (a.PorConfirmar) continue;
                foreach (var x in quienes.Where(q => porCuil.Contains(q.Cuil) || porSuNombre.Contains(q)))
                {
                    var (_, suyas, porNombre) = hojas.GetValueOrDefault(x.Cuil, (x, new List<Hallazgo>(), false));
                    suyas.Add(a.Hoja);
                    hojas[x.Cuil] = (x, suyas, porNombre || !porCuil.Contains(x.Cuil));
                }
            }
            foreach (var (x, suyas, porNombre) in hojas.Values.OrderBy(t => Nombre(t.Empleado)))
            {
                var archivo = Recibos.NombreArchivo(Nombre(x), suMes, suAnio.ToString());
                // La misma imagen repetida va una sola vez, como en los corregidos (y con el mismo límite: ver Planear).
                llevar.Add(new(x, suMes, suAnio, archivo, firmados, suyas.DistinctBy(h => h.Lectura.Huella ?? $"{h.Ruta}|{h.Hoja}").ToList(), porNombre,
                    firmados is not null && File.Exists(Path.Combine(firmados, archivo))));
            }
            foreach (var (h, cuils, porConfirmar) in paginas)
                otros.Add(new(h, suMes, suAnio, cuils, porConfirmar,
                    EnSuCarpeta: !porConfirmar && cuils.Count > 0 && cuils.All(c => llevar.Any(l => l.Empleado.Cuil == c && l.Mes == suMes && l.EnSuCarpeta))));
        }
        return (otros, llevar);
    }

    /// <summary>Las hojas de los firmados que la lectura rápida dejó sin un CUIL de la nómina (las sin nadie, y las que
    /// se encontraron solo por el nombre) y que todavía no se leyeron a fondo.</summary>
    /// <summary>Si el cliente no mandó ningún firmado (ni foto ni escaneo de nadie) y hay recibos originales para armarlos
    /// con las iniciales del estudio: el caso de último recurso, cuando ya no hay tiempo de volver a pedirlos.</summary>
    public static bool NoMandoNada(Revision r) =>
        r.Nomina is not null && !r.Renglones.Any(x => x.Firma is Firma.Firmado or Firma.PorNombre)
        && r.Renglones.Any(x => x.Firma == Firma.Falta && x.SinFirmar.Count > 0);

    public static List<Hallazgo> Dificiles(Revision r) => r.Ilegibles
        .Concat(r.Renglones.Where(x => x.Firma == Firma.PorNombre).SelectMany(x => x.Firmado))
        // Y las que serían de otro mes según el texto del escáner: que lo confirme la lectura de la app.
        .Concat(r.OtrosMeses.Where(o => o.PorConfirmar).Select(o => o.Hoja))
        // También las hojas con texto propio: un escaneo puede traer el OCR del escáner, peor que el de la app.
        .Where(h => !h.Lectura.AFondo).DistinctBy(h => (h.Ruta, h.Hoja)).ToList();

    /// <summary>La segunda pasada: lee a fondo las hojas difíciles del cliente y deja lo leído en la memoria. Después
    /// hay que revisarlo de nuevo, que sale de la memoria. Devuelve cuántas leyó.</summary>
    public static async Task<int> Profundizar(Revision r, LeerImagen aFondo, Memoria memoria, Action<string> avisar, CancellationToken corte,
        SacarHuella? huella = null)
    {
        if (r.Nomina is null) return 0;
        var cuils = r.Nomina.Empleados.Select(e => e.Cuil).ToList();
        // En una hoja donde alguien se encontró solo por el nombre se busca su CUIL, no cualquiera: en una de dos recibos,
        // el del otro ya se leía, y con él la lectura a fondo terminaba antes de probar nada.
        var porNombre = r.Renglones.Where(x => x.Firma == Firma.PorNombre)
            .SelectMany(x => x.Firmado.Select(h => (Clave: (h.Ruta, h.Hoja), x.Empleado.Cuil))).ToLookup(t => t.Clave, t => t.Cuil);
        var hojas = Dificiles(r);
        await ProfundizarHojas(hojas, h => porNombre.Contains((h.Ruta, h.Hoja)) ? porNombre[(h.Ruta, h.Hoja)].ToList() : cuils, r.CarpetaMes,
            aFondo, memoria, avisar, corte, huella);
        return hojas.Count;
    }

    /// <summary>Lee a fondo esas hojas, cada imagen una sola vez, buscando a los de «buscados», y deja lo leído en la
    /// memoria. «carpeta»: donde se busca si la misma imagen ya se leyó a fondo en otro archivo.</summary>
    internal static async Task ProfundizarHojas(List<Hallazgo> hojas, Func<Hallazgo, List<string>> Buscados, string? carpeta, LeerImagen aFondo,
        Memoria memoria, Action<string> avisar, CancellationToken corte, SacarHuella? huella)
    {
        // Las hojas con texto propio llegan sin huella (la lectura rápida no las dibuja): se saca acá, para que la copia de
        // un PDF que ya se leyó a fondo se reconozca y no se lea otra vez.
        var sacadas = new ConcurrentDictionary<(string, int), string>();
        if (huella is not null)
            await Parallel.ForEachAsync(hojas.Where(h => h.Lectura.Huella is null), new ParallelOptions { CancellationToken = corte }, async (h, _) =>
            {
                try { if (await Task.Run(() => huella(h.Ruta, h.Hoja), corte) is { } suya) sacadas[(h.Ruta, h.Hoja)] = suya; }
                catch (Exception e) when (e is not OperationCanceledException) { } // sin huella, se lee como siempre
            });
        string? HuellaDe(Hallazgo h) => h.Lectura.Huella ?? sacadas.GetValueOrDefault((h.Ruta, h.Hoja));
        // La misma imagen repetida (la misma huella, buscando a los mismos) se lee a fondo una vez y vale para todas sus
        // copias: en octubre de 2026, de las 33 hojas difíciles de un cliente, 10 eran distintas.
        var iguales = hojas.GroupBy(h => (HuellaDe(h) ?? $"{h.Ruta}|{h.Hoja}", string.Join(" ", Buscados(h)))).ToList();
        await Parallel.ForEachAsync(iguales, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = corte },
            async (grupo, _) =>
            {
                var h = grupo.First();
                var buscados = Buscados(h);
                // La que ya se leyó a fondo en otro archivo de la carpeta (la misma imagen) no se lee de nuevo: lo que salió
                // de ahí es lo mejor que se pudo sacar.
                var yaLeida = HuellaDe(h) is { } suHuella && carpeta is not null && memoria.ConHuella(carpeta, suHuella) is { AFondo: true } igual ? igual : null;
                var l = yaLeida;
                if (l is null)
                {
                    avisar(Path.GetFileName(h.Ruta));
                    try { l = await ConTurno(() => aFondo(h.Ruta, h.Hoja, t => DeLaNomina(t, buscados).Count > 0), corte); }
                    // Una hoja que no se pudo leer queda como estaba, marcada para no volver a intentar.
                    catch (Exception e) when (e is not OperationCanceledException) { }
                }
                foreach (var copia in grupo)
                {
                    var suya = l is null ? copia.Lectura
                        : yaLeida is not null || copia.Lectura.Ocr ? l
                        // Una hoja con texto propio se queda con los dos textos: lo que el escáner leyó bien de alguien no
                        // se pierde si la lectura de la app lo lee peor.
                        : l with { Texto = copia.Lectura.Texto + "\n" + l.Texto, Nombre = copia.Lectura.Nombre ?? l.Nombre };
                    memoria.Reemplazar(new FileInfo(copia.Ruta), Math.Max(0, copia.Hoja - 1), suya with { AFondo = true, Huella = HuellaDe(copia) });
                }
            });
    }

    static string Archivo(string carpeta, Renglon x, Revision r) => Path.Combine(carpeta, Recibos.NombreArchivo(x.Nombre, r.Mes, r.Anio.ToString()));

    static string RutaLista(string firmados, Revision r) => Path.Combine(firmados, $"FALTAN RECIBOS FIRMADOS {r.Mes:00} {r.Anio}.txt");

    /// <summary>Lo que se guardaría en la carpeta del cliente. Nunca un archivo que ya existe, salvo el consolidado, que se
    /// completa con los que le faltan. La única carpeta que
    /// puede crear es la de los firmados del mes, y solo si hay recibos para poner adentro. Las iniciales, solo
    /// para los que eligió el usuario entre los que no tienen el firmado.</summary>
    public static List<Escritura> Planear(Revision r, IReadOnlySet<string> conIniciales, bool aSuMes = false)
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
            // Los firmados que llegaron en foto o escaneados: un PDF por empleado, derecho y con contraste. También los
            // encontrados solo por el nombre: su PDF lleva la marca «por nombre» y sigue para revisar (ver Escribir).
            // ponytail-keep: también las hojas escaneadas que traen texto propio. Con solo las del OCR de la app, de un
            // PDF de Adobe Scan con siete recibos (octubre 2026) se guardaron dos, los de la hoja que hubo que leer a
            // fondo, y los otros cinco quedaron en verde con «nada para guardar».
            // La misma imagen repetida (por su huella) va una sola vez: con las mismas hojas en varios PDF, el de cada
            // empleado las llevaba todas.
            // shortcut: la huella es de los puntos exactos. La misma foto adentro de un PDF que la recomprimió (el que arma
            // WhatsApp) tiene otra, y el recibo va dos veces; si molesta, comparar los importes que leyó el OCR.
            var imagenes = x.Firmado.Where(SeCorrige).DistinctBy(h => h.Lectura.Huella ?? $"{h.Ruta}|{h.Hoja}").ToList();
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

        // Los recibos de otro mes, si se pidió llevarlos: cada uno a los firmados de su mes, derecho, con contraste y con su
        // nombre, como los corregidos. Si esa carpeta todavía no está, se crea antes. Lo que ya está ahí no se toca. Los
        // originales quedan donde están, como todos: la app no borra ni mueve los archivos del cliente.
        if (aSuMes)
            foreach (var l in r.ALlevar.Where(l => l.Ruta is not null && !l.EnSuCarpeta))
            {
                if (!Libre(l.Ruta!)) continue;
                var suCarpeta = l.Carpeta!;
                if (!Directory.Exists(suCarpeta) && !plan.Any(p => p.Accion == Accion.Carpeta && p.Ruta.Equals(suCarpeta, StringComparison.OrdinalIgnoreCase)))
                    plan.Add(new(Accion.Carpeta, suCarpeta, null, []));
                plan.Add(new(Accion.ASuMes, l.Ruta!, l.Empleado, l.Hojas));
            }

        // El consolidado: los recibos que firmó cada empleado, juntos en «CLIENTE RECIBOS FIRMADOS 10 2026.pdf» (las
        // copias con las iniciales del estudio no van). Si ya está y tiene a todos, no se toca. Si le falta alguien, se
        // completa: lo que tenía queda igual y lo que faltaba va al final.
        var consolidado = Consolidado(firmados, r);
        bool EnElConsolidado(Hallazgo h) => h.Ruta.Equals(consolidado, StringComparison.OrdinalIgnoreCase);
        var empleadosPorArchivo = r.Renglones.SelectMany(x => x.Firmado.Select(h => (h.Ruta, x.Empleado.Cuil))).Distinct()
            .GroupBy(t => t.Ruta, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // En el orden de los recibos sin firmar de todos juntos, que es el de la hoja que firmaron.
        var todos = empleadosPorPdf.Count > 0 ? empleadosPorPdf.MaxBy(p => p.Value).Key : null;
        var nuevas = r.Renglones
            .Where(x => x.Firma is Firma.Firmado or Firma.PorNombre && !x.Firmado.Any(EnElConsolidado))
            .OrderBy(x => x.SinFirmar.FirstOrDefault(h => h.Ruta == todos)?.Hoja ?? int.MaxValue)
            // De cada uno, la hoja del archivo con más empleados (el escaneo de todos juntos) antes que su archivo suelto,
            // que suele ser una copia de esa hoja: así una hoja con dos recibos va una sola vez.
            .Select(x => x.Firmado.Where(h => !EnElConsolidado(h)).OrderByDescending(h => empleadosPorArchivo[h.Ruta])
                .ThenBy(h => h.Ruta.Equals(Archivo(firmados, x, r), StringComparison.OrdinalIgnoreCase)).First())
            // Pero un escaneo, con sus hojas como vinieron: el cliente no las escanea en el orden de los sin firmar, y
            // con el de los sin firmar, el de un cliente de octubre 2026 salía con las hojas 1, 4, 2 y 3.
            .GroupBy(h => h.Ruta, StringComparer.OrdinalIgnoreCase).SelectMany(g => g.OrderBy(h => h.Hoja))
            .DistinctBy(h => (h.Ruta, h.Hoja)).ToList();
        if (nuevas.Count > 0) plan.Add(new(Accion.Consolidado, consolidado, null, nuevas));

        // La carpeta de los firmados se crea solo si hay recibos para guardar en ella, y antes que ellos.
        var crear = r.Firmados is null && plan.Any(p => p.Ruta.StartsWith(firmados + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (crear) plan.Insert(0, new(Accion.Carpeta, firmados, null, []));

        // La lista de los que faltan, en la carpeta de los firmados: nueva si falta alguien o si la carpeta es
        // nueva, o la vieja de la app cuando ya no dice lo mismo. Su texto se arma al escribirla, después de
        // los recibos: así ya cuenta los que llevan iniciales.
        if (r.Firmados is not null || crear)
        {
            var ruta = RutaLista(firmados, r);
            var falta = r.Faltan > 0 || r.Ilegibles.Count > 0 || r.DeOtroMesPendientes.Any() || crear;
            if (File.Exists(ruta) ? EsNuestra(ruta) && SinFecha(File.ReadAllText(ruta)) != SinFecha(Lista(r)) : falta)
                plan.Add(new(Accion.Lista, ruta, null, []));
        }
        return plan;
    }

    public static string Consolidado(string firmados, Revision r) => Path.Combine(firmados, $"{r.Cliente} RECIBOS FIRMADOS {r.Mes:00} {r.Anio}.pdf");

    /// <summary>Si la hoja se guarda derecha y con contraste (una foto o un escaneo) o tal cual (un PDF hecho en la
    /// computadora, o la copia con las iniciales).</summary>
    public static bool SeCorrige(Hallazgo h) => h.Lectura.Ocr || Escaneada(h);

    // Una hoja de PDF que es una foto con el texto por encima (lo que arman Adobe Scan y los escáneres): las imágenes
    // cubren más de la mitad de la hoja. Un recibo hecho en la computadora, a lo sumo trae un logo.
    static bool Escaneada(Hallazgo h)
    {
        if (h.Hoja == 0) return false;
        try
        {
            using var doc = PdfDocument.Open(h.Ruta);
            var p = doc.GetPage(h.Hoja);
            return p.GetImages().Sum(i => i.BoundingBox.Area) > 0.5 * p.Width * p.Height;
        }
        catch { return false; } // si no se puede abrir, se queda como está
    }

    static bool EsNuestra(string ruta)
    {
        try { return File.ReadLines(ruta).FirstOrDefault()?.StartsWith(Encabezado) == true; }
        catch { return false; } // si no se puede leer, no se toca
    }

    static string SinFecha(string lista) => string.Join("\n", lista.Split('\n').Where(l => !l.StartsWith("Revisado el")).Select(l => l.TrimEnd('\r')));

    // Lo que se agrega al nombre de alguien que se fue en el mes, en la lista y en el panel.
    public static string DeBaja(Renglon x) => x.Baja is not { } cese ? ""
        : (cese.Length > 0 ? $" · baja el {cese}" : " · baja") + (x.Firma == Firma.Falta ? ": falta la liquidación final firmada" : "");

    // Si lo que se guarda lleva otro nombre que su original (la foto «IMG_2034.jpg» se guarda como «PEREZ JUAN 10
    // 2026.pdf»), cómo decirlo; null si ya se llamaba así. El original no se renombra: queda como está.
    public static string? NombreCorregido(Escritura e)
    {
        var nuevo = Path.GetFileName(e.Ruta);
        var originales = e.Hojas.Select(h => Path.GetFileName(h.Ruta)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (originales.All(o => Path.GetFileNameWithoutExtension(o).Equals(Path.GetFileNameWithoutExtension(nuevo), StringComparison.OrdinalIgnoreCase)))
            return null;
        var de = originales.Count == 1 ? $"«{originales[0]}»"
            : string.Join(", ", originales.SkipLast(1).Select(o => $"«{o}»")) + $" y «{originales[^1]}»";
        return $"Nombre corregido de {de}: se guarda como «{nuevo}», y {(originales.Count == 1 ? "el original queda como está" : "los originales quedan como están")}.";
    }

    /// <summary>El texto de FALTAN RECIBOS FIRMADOS MM AAAA.txt.</summary>
    public static string Lista(Revision r)
    {
        var s = new StringBuilder();
        void Personas(string titulo, Firma firma)
        {
            var quienes = r.Renglones.Where(x => x.Firma == firma).ToList();
            if (quienes.Count == 0) return;
            s.AppendLine().AppendLine(titulo);
            foreach (var x in quienes) s.AppendLine($"    {x.Nombre} · CUIL {x.Empleado.Cuil}{DeBaja(x)}");
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
        if (r.DeOtroMesPendientes.Any())
        {
            s.AppendLine().AppendLine("Estos son recibos de otro mes y no cuentan como firmados (hay que llevarlos a su carpeta):");
            foreach (var g in r.DeOtroMesPendientes.GroupBy(o => (o.Hoja.Ruta, o.Mes)))
                s.AppendLine($"    {Path.GetFileName(g.Key.Ruta)}" + (g.First().Hoja.Hoja > 0 ? $", hoja {string.Join(", ", g.Select(o => o.Hoja.Hoja))}" : "")
                    + $" · de {NombreMes(g.Key.Mes).ToLowerInvariant()}");
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
    /// falla en vez de pisarlo. Lo único que se reemplaza es la lista, si la escribió la app, y el consolidado, que se
    /// completa sin cambiar lo que tenía.</summary>
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

        if (e.Accion == Accion.Consolidado)
        {
            await Consolidar(e, r, corregir);
            return;
        }

        var b = new PdfDocumentBuilder();
        // El CUIL y el período en las palabras clave: la próxima revisión lo encuentra sin OCR. «iniciales» marca
        // los que llevan las del estudio, para no contarlos como firmados por el empleado. «por nombre», los que se
        // encontraron solo por el nombre: sin la marca, el CUIL de las palabras clave lo volvía firmado de verdad en
        // la revisión siguiente, sin que nadie hubiera mirado la suposición.
        // Lo que se lleva a otro mes lleva el período de ese mes: así lo encuentra la revisión de ese mes.
        var llevado = e.Accion == Accion.ASuMes ? r.ALlevar.First(l => string.Equals(l.Ruta, e.Ruta, StringComparison.OrdinalIgnoreCase)) : null;
        var porNombre = llevado?.PorNombre ?? r.Renglones.Any(x => x.Empleado == e.Empleado && x.Firma == Firma.PorNombre);
        b.DocumentInformation.Keywords = $"{e.Empleado!.Cuil} {llevado?.Mes ?? r.Mes:00}/{llevado?.Anio ?? r.Anio}" + (e.Accion == Accion.Iniciales ? " iniciales" : "")
            + (porNombre ? " por nombre" : "");
        if (e.Accion is Accion.Corregido or Accion.ASuMes)
        {
            await EscribirCorregido(e.Ruta, b.DocumentInformation.Keywords, e.Hojas, corregir);
            return;
        }
        var docs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            PdfDocumentBuilder.AddedFont? fuente = null;
            foreach (var h in e.Hojas)
            {
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
            await Recibos.GuardarNuevo(e.Ruta, b.Build());
        }
        finally
        {
            foreach (var d in docs.Values) d.Dispose();
        }
    }

    /// <summary>Un PDF nuevo con esas hojas derechas y con contraste, y esas palabras clave (el CUIL y el período, para que la
    /// revisión lo encuentre sin OCR). Nunca pisa: si ya hay un archivo con ese nombre, falla.</summary>
    internal static async Task EscribirCorregido(string ruta, string claves, IEnumerable<Hallazgo> hojas, Corregir corregir)
    {
        var b = new PdfDocumentBuilder();
        b.DocumentInformation.Keywords = claves;
        foreach (var h in hojas) await HojaCorregida(b, h, corregir);
        await Recibos.GuardarNuevo(ruta, b.Build());
    }

    // Entera, centrada y con un margen chico, en una hoja A4 parada o acostada como la foto.
    static async Task<PdfPageBuilder> HojaCorregida(PdfDocumentBuilder b, Hallazgo h, Corregir corregir)
    {
        var (jpeg, ancho, alto) = await corregir(h);
        var parada = alto >= ancho;
        var pagina = b.AddPage(PageSize.A4, parada);
        double pw = parada ? 595 : 842, ph = parada ? 842 : 595, margen = 18;
        var escala = Math.Min((pw - 2 * margen) / ancho, (ph - 2 * margen) / alto);
        double w = ancho * escala, hh = alto * escala, x = (pw - w) / 2, y = (ph - hh) / 2;
        pagina.AddJpeg(jpeg, new PdfRectangle(x, y, x + w, y + hh));
        return pagina;
    }

    // El consolidado: lo que ya tenía va primero, hoja por hoja tal cual, y lo que faltaba al final (ver Recibos.Juntar).
    static async Task Consolidar(Escritura e, Revision r, Corregir corregir)
    {
        var b = new PdfDocumentBuilder();
        // Sin los CUIL: en las palabras clave valdrían para todas las hojas. Quién está en cada una va en la hoja misma.
        b.DocumentInformation.Keywords = $"{r.Mes:00}/{r.Anio} consolidado";
        var fuente = b.AddStandard14Font(Standard14Font.Helvetica);
        var docs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        PdfDocument Abrir(string ruta) => docs.TryGetValue(ruta, out var d) ? d : docs[ruta] = PdfDocument.Open(ruta);
        // Quién está en la hoja, en texto invisible. Sin el CUIL de todos, al que el OCR no encontrara en la hoja se lo
        // volvía a agregar en cada guardado. Los encontrados solo por el nombre llevan «POR NOMBRE»: siguen para revisar.
        void Quienes(PdfPageBuilder p, string ruta, int hoja) => Recibos.Invisible(p, fuente, string.Join(" ", r.Renglones
            .Where(x => x.Firmado.Any(h => h.Hoja == hoja && h.Ruta.Equals(ruta, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{(x.Firma == Firma.PorNombre ? "POR NOMBRE" : "CUIL")} {x.Empleado.Cuil} {Regex.Replace(Normal(x.Empleado.Nombre), "[^A-Z ]", "")}")));
        try
        {
            await Recibos.Juntar(e.Ruta, b, (p, i) => Quienes(p, e.Ruta, i), async () =>
            {
                foreach (var h in e.Hojas)
                    Quienes(SeCorrige(h) ? await HojaCorregida(b, h, corregir) : b.AddPage(Abrir(h.Ruta), h.Hoja), h.Ruta, h.Hoja);
                return e.Hojas.Count;
            });
        }
        finally
        {
            foreach (var d in docs.Values) d.Dispose();
        }
    }
}
