using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using Xunit.Abstractions;

// Nombres y CUIL inventados (con dígito verificador válido): el repo es público y los recibos reales tienen
// datos personales.
public class ClientesTests(ITestOutputHelper salida)
{
    const string Empleador = "30-71234567-1";
    static readonly Empleado Gomez = new("27-23456789-1", "GOMEZ LUCIA"), Rojas = new("20-45678901-4", "ROJAS PABLO"),
        Lopez = new("27-34567890-0", "LOPEZ ANA"), Ruiz = new("20-11222333-9", "RUIZ LUIS");

    [Theory]
    [InlineData("PEREZ SUELDOS OCTUBRE 2026", 10, true)]
    [InlineData("SUELDOS OCTUBRE 2026", 10, true)]
    [InlineData("10 OCTUBRE 2026", 10, true)]
    [InlineData("10 2026", 10, true)]
    [InlineData("PEREZ SUELDOS OCTUBRE 20266", 10, true)] // así está escrita una carpeta real
    [InlineData("SUELDOS SETIEMBRE 2026", 9, true)]
    [InlineData("SUELDOS NOVIEMBRE 2026", 10, false)]
    [InlineData("MAYO SUELDOS OCTUBRE 2026", 5, false)] // Mayo es el apellido del cliente
    [InlineData("12 2026", 2, false)]
    [InlineData("SUELDOS OCTUBRE 2025", 10, false)]
    public void Reconoce_la_carpeta_del_mes(string carpeta, int mes, bool es) => Assert.Equal(es, Clientes.EsDelMes(carpeta, mes, 2026));

    // La ruta del estudio no está en el código: es la que se eligió en cada PC, llevada al año del mes elegido.
    [Fact]
    public void La_carpeta_de_otro_anio()
    {
        Assert.Equal(@"D:\ESTUDIO 2027\CLIENTES 2027", Clientes.RaizDelAnio(@"D:\ESTUDIO 2026\CLIENTES 2026", 2027));
        Assert.Equal(@"D:\CLIENTES", Clientes.RaizDelAnio(@"D:\CLIENTES", 2027));
        // Un número más largo que termina en un año no es un año.
        Assert.Equal(@"D:\0 - ESTUDIO 2027\LEGAJO 12026", Clientes.RaizDelAnio(@"D:\0 - ESTUDIO 2026\LEGAJO 12026", 2027));
        // Otro año más arriba en la ruta es parte de un nombre: se cambia solo el de la carpeta de los clientes.
        Assert.Equal(@"D:\ESTUDIO 2019\CLIENTES 2027", Clientes.RaizDelAnio(@"D:\ESTUDIO 2019\CLIENTES 2026", 2027));
    }

    [Fact]
    public void Por_el_nombre_solo_si_es_uno()
    {
        Empleado[] nomina = [Gomez, Rojas, new("20-12345678-6", "GOMEZ PABLO")];
        Assert.Equal(Rojas, Clientes.PorNombre("Apellido y nombre: Rojas, Pablo. Legajo 3", nomina));
        // «GOMEZ» solo no alcanza: hay dos. Y «PABLO» suelto tampoco.
        Assert.Null(Clientes.PorNombre("GOMEZ legajo 3", nomina));
        Assert.Null(Clientes.PorNombre("PABLO", nomina));
        Assert.Equal(Gomez, Clientes.PorNombre("GÓMEZ LUCÍA", nomina));
    }

    // Las palabras que ya tienen dueño en la hoja (el empleador del encabezado, y quien está por su CUIL) no alcanzan
    // para decir que también está otro.
    [Fact]
    public void Las_palabras_que_ya_tienen_dueno_no_nombran_a_otro()
    {
        Empleado maria = new("27-23456789-1", "GOMEZ MARIA"), jose = new("27-34567890-0", "GOMEZ MARIA JOSE");
        // El recibo de María, leído con su CUIL: María José no está ahí, aunque sus dos primeras palabras sí.
        Assert.Empty(Clientes.Nombrados("GOMEZ, MARIA CUIL 27-23456789-1", [maria, jose], [maria.Cuil]));
        // Las dos en la misma hoja, escaneadas de a dos: «JOSE» no es de María, así que María José también está.
        Assert.Equal([jose], Clientes.Nombrados("GOMEZ, MARIA CUIL 27-23456789-1 GOMEZ, MARIA JOSE CUIL 27-3?5?7890-", [maria, jose], [maria.Cuil]));

        // El hijo del empleador, con su apellido y uno de sus nombres: en el recibo de López está López, no él...
        Empleado hijo = new("20-45678901-4", "PEREZ JUAN PABLO");
        Assert.Equal([Lopez], Clientes.Nombrados("PEREZ JUAN CARLOS CUIT 30-71234567-1 LOPEZ, ANA", [hijo, Lopez], [], "PEREZ JUAN CARLOS"));
        // ...pero sí en el suyo, que trae además «PABLO».
        Assert.Equal([hijo], Clientes.Nombrados("PEREZ JUAN CARLOS CUIT 30-71234567-1 PEREZ, JUAN PABLO", [hijo, Lopez], [], "PEREZ JUAN CARLOS"));
    }

    [Fact]
    public void Con_un_cuil_casi_igual_si_tambien_esta_el_nombre()
    {
        Empleado[] nomina = [Gomez, Rojas, Lopez];
        // Un 6 leído como 8, y el verificador tapado por la cajita.
        Assert.Equal(Rojas, Clientes.PorCuilParecido("ROJAS PABLO C.U.I.L. 20-45878901-", nomina));
        // Dos dígitos cambiados, todavía; tres, ya no.
        Assert.Equal(Rojas, Clientes.PorCuilParecido("ROJAS 20-46878901-4", nomina));
        Assert.Null(Clientes.PorCuilParecido("ROJAS 20-46878991-4", nomina));
        // Sin el nombre no alcanza.
        Assert.Null(Clientes.PorCuilParecido("C.U.I.L. 20-45878901-", nomina));
        // Dos hermanos con el DNI casi igual y el mismo apellido: no decide.
        Empleado[] hermanos = [new("20-30111222-3", "PEREZ JUAN"), new("20-30111223-1", "PEREZ ANA")];
        Assert.Null(Clientes.PorCuilParecido("PEREZ 20-30111228-5", hermanos));
    }

    [Fact]
    public void Encuentra_el_cuil_de_la_nomina_como_lo_lea_el_ocr()
    {
        string[] nomina = [Gomez.Cuil, Rojas.Cuil, Lopez.Cuil];
        // Sin guiones, con puntos, con espacios y con letras en vez de números: así lo devuelve el OCR de Windows.
        Assert.Equal([Gomez.Cuil], Clientes.DeLaNomina("CUIL. 27234567891 0 Años", nomina));
        Assert.Equal([Rojas.Cuil], Clientes.DeLaNomina("PEREZ, JUAN 20.45678901-4 VIAMONTE", nomina));
        Assert.Equal([Lopez.Cuil], Clientes.DeLaNomina("CUIL 27 - 3456789O - 0", nomina));
        Assert.Equal([Rojas.Cuil], Clientes.DeLaNomina("cuil 2O-4S678901-4", nomina));
        // Sin el dígito verificador, como queda en la cajita del recibo, que lo tapa con el borde.
        Assert.Equal([Rojas.Cuil], Clientes.DeLaNomina("C.U.I.L. 20-45678901- BANCO", nomina));
        // Un CUIL que no es de la nómina, o uno al que le falta un dígito del DNI, no cuenta.
        Assert.Empty(Clientes.DeLaNomina("20-12345678-6 y 27-2345678-1", nomina));
    }

    // Una imagen de 2×1: A a la izquierda y B a la derecha.
    [Fact]
    public void Gira_en_sentido_horario()
    {
        byte[] ab = [1, 1, 1, 255, 2, 2, 2, 255], ba = [2, 2, 2, 255, 1, 1, 1, 255];
        var (g, w, h) = Retoque.Girar(ab, 2, 1, 90);
        Assert.Equal((1, 2), (w, h));
        Assert.Equal(ab, g); // A arriba, B abajo
        Assert.Equal(ba, Retoque.Girar(ab, 2, 1, 270).Px); // B arriba
        Assert.Equal(ba, Retoque.Girar(ab, 2, 1, 180).Px);
    }

    [Fact]
    public void Endereza_en_sentido_horario()
    {
        const int w = 200, h = 100;
        var px = Enumerable.Repeat((byte)255, w * h * 4).ToArray();
        for (var x = 20; x < 180; x++)
            for (var c = 0; c < 3; c++) px[(50 * w + x) * 4 + c] = 0;
        var r = Retoque.Enderezar(px, w, h, 5);
        int Fila(int x) => Enumerable.Range(0, h).MinBy(y => r[(y * w + x) * 4]);
        // Girada 5° a la derecha, la raya baja por el lado derecho (tan 5° × 70 ≈ 6) y sube por el izquierdo.
        Assert.InRange(Fila(170), 54, 58);
        Assert.InRange(Fila(30), 42, 46);
    }

    [Fact]
    public void Estira_el_contraste_de_una_foto_lavada()
    {
        // La mitad gris oscuro (la letra) y la mitad gris claro (el papel): una foto lavada.
        var px = new byte[100 * 4];
        for (var i = 0; i < 100; i++)
        {
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = (byte)(i < 50 ? 120 : 190);
            px[i * 4 + 3] = 255;
        }
        var r = Retoque.Contraste(px);
        Assert.Equal(255, r[99 * 4]); // el papel queda blanco
        Assert.InRange(r[0], 30, 60); // la letra, mucho más oscura, sin pasar del triple de contraste
    }

    [Fact]
    public void Achica_promediando_y_agranda_interpolando()
    {
        var px = new byte[4 * 4 * 4];
        for (var i = 0; i < 16; i++) px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = (byte)(i % 2 == 0 ? 0 : 200);
        var (chica, w, h) = Retoque.Ajustar(px, 4, 4, 2, agrandar: false);
        Assert.Equal((2, 2), (w, h));
        Assert.Equal(100, chica[0]); // el promedio de negro y gris, no uno de los dos
        Assert.Equal((8, 8), (Retoque.Ajustar(px, 4, 4, 100, agrandar: true).Ancho, Retoque.Ajustar(px, 4, 4, 100, agrandar: true).Alto)); // hasta el doble
        Assert.Equal((4, 4), (Retoque.Ajustar(px, 4, 4, 100, agrandar: false).Ancho, Retoque.Ajustar(px, 4, 4, 100, agrandar: false).Alto));
    }

    // ---- Un cliente armado como los del estudio ----

    static void Pdf(string ruta, params (string Texto, double X, double Y)[][] hojas)
    {
        var b = new PdfDocumentBuilder();
        var fuente = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var hoja in hojas)
        {
            var p = b.AddPage(PageSize.A4);
            foreach (var (texto, x, y) in hoja) p.AddText(texto, 8, new PdfPoint(x, y), fuente);
        }
        File.WriteAllBytes(ruta, b.Build());
    }

    // Como la nómina de AFIP: una columna por empleado, con el CUIL y el nombre en dos renglones.
    static (string, double, double)[] Nomina(params Empleado[] empleados) =>
    [
        ("CUIT:", 40, 800), (Empleador, 100, 800), ("Periodo:", 300, 800), ("10/2026", 350, 800),
        ("Contribuyente:", 40, 788),
        ("CUIL", 40, 760), ("Suma Totales", 500, 760), ("Apellido y Nombre", 10, 748), ("Obra Social", 10, 725),
        .. empleados.SelectMany((e, i) => new (string, double, double)[]
        {
            (e.Cuil, 100 + i * 100, 760), (e.Nombre.Split(' ')[0], 100 + i * 100, 748), (e.Nombre.Split(' ')[1], 100 + i * 100, 738),
        }),
    ];

    // Como el recibo del sistema de sueldos, con el espacio para la firma.
    static (string, double, double)[] Recibo(Empleado e) =>
    [
        ("PEREZ JUAN", 40, 800), ($"CUIT {Empleador}", 300, 800),
        ("APELLIDO Y NOMBRE", 40, 760), ("CUIL", 250, 760), ("PERIODO", 400, 760),
        (e.Nombre.Replace(" ", ", "), 40, 748), (e.Cuil, 250, 748), ("10/2026", 400, 748),
        ("SUELDO NETO", 40, 400), ("FIRMA DEL EMPLEADOR", 60, 200), ("FIRMA DEL EMPLEADO", 350, 200),
    ];

    static string Cliente(string raiz, string nombre, bool conFirmados)
    {
        var mes = Path.Combine(raiz, nombre, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026", $"{nombre} SUELDOS OCTUBRE 2026");
        Directory.CreateDirectory(Path.Combine(mes, "SUSS Y SIND"));
        Pdf(Path.Combine(mes, "SUSS Y SIND", $"{nombre} SUSS NOMINA 10 2026.pdf"), Nomina(Gomez, Rojas, Lopez, Ruiz));
        Pdf(Path.Combine(mes, $"{nombre} RECIBOS 10 2026.PDF"), Recibo(Gomez), Recibo(Rojas), Recibo(Lopez));
        // El resumen trae los CUIL pero no es un recibo.
        (string, double, double)[] resumen = [("APELLIDO Y NOMBRE", 40, 760), .. new[] { Gomez, Rojas, Lopez, Ruiz }.Select((e, i) => (e.Cuil, 40.0, 740.0 - i * 12))];
        Pdf(Path.Combine(mes, $"{nombre} RESUMEN 10 2026.pdf"), resumen);
        if (conFirmados)
        {
            var firmados = Directory.CreateDirectory(Path.Combine(mes, "RECIBOS FIRMADOS OCTUBRE")).FullName;
            foreach (var foto in new[] { "WhatsApp Image 1.jpeg", "WhatsApp Image 2.jpeg", "WhatsApp Image 3.jpeg" })
                File.WriteAllBytes(Path.Combine(firmados, foto), [0xFF, 0xD8]); // el OCR de la prueba no los abre
        }
        Directory.CreateDirectory(Path.Combine(raiz, "SIN EMPLEADOS", "1. IMPUESTOS"));
        return Path.Combine(raiz, nombre);
    }

    // El OCR de la prueba: la primera foto trae el CUIL, la segunda solo el nombre y la tercera, nada legible.
    static readonly Dictionary<string, string> Fotos = new()
    {
        ["WhatsApp Image 1.jpeg"] = $"APELLIDO Y NOMBRE GOMEZ, LUCIA CUIL {Gomez.Cuil} FIRMA DEL EMPLEADO",
        ["WhatsApp Image 2.jpeg"] = "APELLIDO Y NOMBRE ROJAS, PABLO CUIL 2?-4??78901-4 PERIODO 10/2026",
        ["WhatsApp Image 3.jpeg"] = "a o 1 / . ;",
        // Ilegibles, pero con el nombre del empleado en el archivo; la baja no es un recibo aunque lo nombre.
        ["RUIZ LUIS RECIBO OCT 2026.jpeg"] = "x 1 / ;",
        ["BAJA LOPEZ ANA.jpeg"] = "CONSTANCIA DEL TRABAJADOR Baja",
    };

    static readonly LeerImagen Leer = (ruta, hoja, buscado) => Task.FromResult(new Lectura(Fotos[Path.GetFileName(ruta)], true, 180, 1.5));

    [Fact]
    public async Task Lee_las_hojas_escaneadas_a_la_vez_con_un_tope()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var mes = Path.Combine(cliente, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026", "PEREZ JUAN SUELDOS OCTUBRE 2026");
            var firmados = Directory.CreateDirectory(Path.Combine(mes, "RECIBOS FIRMADOS OCTUBRE")).FullName;
            // Un PDF escaneado de ocho hojas sin texto, dos de cada empleado.
            Pdf(Path.Combine(firmados, "ESCANEO.pdf"), Enumerable.Range(0, 8).Select(_ => Array.Empty<(string, double, double)>()).ToArray());
            Empleado[] orden = [Gomez, Rojas, Lopez, Ruiz];
            int ahora = 0, maximo = 0;
            // Sincrónico, como el de verdad: dibujar la hoja y leerla no tienen ningún await. Con un Task.Delay acá la
            // prueba pasaba aunque la app leyera de a una hoja (medido con un cliente real: un núcleo de cuatro).
            LeerImagen lento = (ruta, hoja, buscado) =>
            {
                var n = Interlocked.Increment(ref ahora);
                lock (orden) maximo = Math.Max(maximo, n);
                Thread.Sleep(50);
                Interlocked.Decrement(ref ahora);
                return Task.FromResult(new Lectura($"CUIL {orden[(hoja - 1) % 4].Cuil}", Ocr: true));
            };
            var r = await Clientes.Revisar(cliente, 10, 2026, null, lento, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            // Cada hoja con su lectura, en orden: a Gómez le tocan la 1 y la 5.
            Assert.Equal(["1,5", "2,6", "3,7", "4,8"], r.Renglones.Select(x => string.Join(",", x.Firmado.Select(h => h.Hoja).Order())));
            // Varias a la vez, pero nunca más que el tope (núcleos, y lo que entra en la memoria).
            Assert.InRange(maximo, Math.Min(2, Clientes.ALaVez), Clientes.ALaVez);
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    // La carpeta del mes, y la de firmados (vacía), de un cliente armado como los del estudio.
    static string Mes(string cliente) => Path.Combine(cliente, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026",
        $"{Path.GetFileName(cliente)} SUELDOS OCTUBRE 2026");
    static string Firmados(string cliente) => Directory.CreateDirectory(Path.Combine(Mes(cliente), "RECIBOS FIRMADOS OCTUBRE")).FullName;

    [Fact]
    public void Dice_cuando_lo_que_se_guarda_cambia_de_nombre()
    {
        var juan = new Empleado("20-12345678-6", "PEREZ JUAN");
        Hallazgo Foto(string ruta, int hoja = 1) => new(ruta, hoja, new Lectura("", Ocr: true));
        Escritura Corregido(params Hallazgo[] hojas) => new(Accion.Corregido, Path.Combine("firmados", "PEREZ JUAN 10 2026.pdf"), juan, [.. hojas]);

        Assert.Equal("Nombre corregido de «IMG_2034.jpg»: se guarda como «PEREZ JUAN 10 2026.pdf», y el original queda como está.",
            Clientes.NombreCorregido(Corregido(Foto(Path.Combine("fotos", "IMG_2034.jpg")))));
        // Dos hojas del mismo escaneo son un solo original; dos fotos, dos.
        Assert.Contains("de «escaneo.pdf»:", Clientes.NombreCorregido(Corregido(Foto("escaneo.pdf", 1), Foto("escaneo.pdf", 2))));
        Assert.Contains("de «a.jpg» y «b.jpg»: se guarda como «PEREZ JUAN 10 2026.pdf», y los originales quedan como están.",
            Clientes.NombreCorregido(Corregido(Foto("a.jpg"), Foto("b.jpg"))));
        // Si la foto ya se llamaba así, el nombre no cambia: no hay lápiz.
        Assert.Null(Clientes.NombreCorregido(Corregido(Foto("PEREZ JUAN 10 2026.jpeg"))));
    }

    [Fact]
    public async Task Reconoce_las_bajas_y_su_liquidacion_final()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var bajas = Directory.CreateDirectory(Path.Combine(Mes(cliente), "BAJAS")).FullName;
            // López, con la constancia de baja de ARCA, que dice cuándo se fue.
            Pdf(Path.Combine(bajas, "BAJA LOPEZ 15 10 2026.pdf"),
                [("CONSTANCIA DEL TRABAJADOR", 40, 800), ("Baja", 40, 785), ("CUIL:", 40, 700), (Lopez.Cuil, 80, 700),
                 ("Fecha Inicio: 01/01/2026", 40, 680), ("Fecha Cese: 15/10/2026", 250, 680)]);
            // Ruiz, solo con su liquidación final sin firmar: se reconoce por el nombre del archivo.
            Pdf(Path.Combine(bajas, "LIQ FINAL RUIZ 20 10 2026.PDF"), Recibo(Ruiz));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, null, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            Assert.Equal([null, null, "15/10/2026", "20/10/2026"], r.Renglones.Select(x => x.Baja));
            // La constancia no es el recibo de nadie, y la lista dice qué falta de los que se fueron.
            Assert.Equal(Firma.Falta, r.Renglones[3].Firma);
            Assert.Contains($"CUIL {Ruiz.Cuil} · baja el 20/10/2026: falta la liquidación final firmada", Clientes.Lista(r));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Una_constancia_entre_los_firmados_no_es_un_recibo()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            File.WriteAllBytes(Path.Combine(Firmados(cliente), "ALTA FIRMADA.jpeg"), [0xFF, 0xD8]);
            // El alta firmada de Gómez trae su CUIL, pero no es su recibo.
            LeerImagen alta = (_, _, _) => Task.FromResult(new Lectura(
                $"Simplificación Registral CONSTANCIA DEL TRABAJADOR Alta CUIL: {Gomez.Cuil} Fecha Inicio: 21/10/2026 Fecha Cese:", Ocr: true));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, alta, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            Assert.Equal(Firma.Falta, r.Renglones[0].Firma);
            Assert.Null(r.Renglones[0].Baja);
            Assert.Empty(r.Ilegibles);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Una_hoja_con_dos_recibos_cuenta_para_los_dos()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            File.WriteAllBytes(Path.Combine(Firmados(cliente), "ESCANEO.jpeg"), [0xFF, 0xD8]);
            // Escaneados de a dos: el CUIL de Gómez salió entero; el de Rojas no, pero está su nombre.
            LeerImagen deADos = (_, _, _) => Task.FromResult(new Lectura(
                $"APELLIDO Y NOMBRE GOMEZ, LUCIA CUIL {Gomez.Cuil} APELLIDO Y NOMBRE ROJAS, PABLO CUIL 2?-4??78901-4", Ocr: true));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, deADos, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            // Los dos están; la marca de revisar es solo de Rojas, no de Gómez, aunque compartan la hoja.
            Assert.Equal([Firma.Firmado, Firma.PorNombre, Firma.Falta, Firma.Falta], r.Renglones.Select(x => x.Firma));
            Assert.Empty(r.Ilegibles);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Un_escaneo_con_texto_propio_pasa_por_la_lectura_a_fondo()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            // Un PDF con el texto que le puso el escáner: el CUIL de Gómez salió bien; de Rojas, solo el nombre.
            Pdf(Path.Combine(Firmados(cliente), "ESCANEO.pdf"),
                [("GOMEZ LUCIA", 40, 800), (Gomez.Cuil, 250, 800), ("ROJAS PABLO", 40, 400), ("2?-4??78901-4", 250, 400)]);
            var r = await Clientes.Revisar(cliente, 10, 2026, null, null, memoria, _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.PorNombre], r.Renglones.Take(2).Select(x => x.Firma));
            // Tiene texto, pero queda alguien por revisar: va a la lectura a fondo, que busca el CUIL de Rojas y no cualquiera.
            Assert.Single(Clientes.Dificiles(r));
            Func<string, bool>? buscado = null;
            LeerImagen aFondo = (_, _, b) =>
            {
                buscado = b;
                return Task.FromResult(new Lectura($"ROJAS PABLO CUIL {Rojas.Cuil} GOMEZ LUC1A 27-2345", Ocr: true)); // al de Gómez lo lee peor
            };
            Assert.Equal(1, await Clientes.Profundizar(r, aFondo, memoria, _ => { }, CancellationToken.None));
            Assert.False(buscado!($"CUIL {Gomez.Cuil}"));
            Assert.True(buscado!($"CUIL {Rojas.Cuil}"));
            // Con los dos textos, los dos quedan firmados, y la hoja no se vuelve a leer.
            var otra = await Clientes.Revisar(cliente, 10, 2026, null, null, memoria, _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.Firmado], otra.Renglones.Take(2).Select(x => x.Firma));
            Assert.Empty(Clientes.Dificiles(otra));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Sabe_si_el_cliente_no_mando_nada()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            // Sin carpeta de firmados: no mandó nada, y hay originales para armarlos con iniciales.
            var nada = await Clientes.Revisar(Cliente(raiz, "PEREZ JUAN", conFirmados: false), 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.True(Clientes.NoMandoNada(nada));
            // Con la foto firmada de Gómez ya mandó algo, aunque falten otros.
            var algo = await Clientes.Revisar(Cliente(raiz, "PEREZ ANA", conFirmados: true), 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.False(Clientes.NoMandoNada(algo));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Revisa_y_guarda_sin_pisar_nada()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: true);
            Assert.Equal([cliente], Clientes.ConSueldos(raiz));

            var r = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.Empty(r.Avisos);
            Assert.Equal([Gomez, Rojas, Lopez, Ruiz], r.Nomina!.Empleados);
            Assert.Equal([Firma.Firmado, Firma.PorNombre, Firma.Falta, Firma.Falta], r.Renglones.Select(x => x.Firma));
            Assert.Equal("WhatsApp Image 3.jpeg", Path.GetFileName(Assert.Single(r.Ilegibles).Ruta));
            // El recibo sin firmar de cada uno sale del PDF de todos juntos; el resumen no cuenta, y Ruiz no tiene.
            Assert.Equal([1, 2, 3], r.Renglones.Take(3).Select(x => Assert.Single(x.SinFirmar).Hoja));
            Assert.Empty(r.Renglones[3].SinFirmar);
            // De esos recibos sale también cómo se llama el empleador, que está en el encabezado de todos.
            Assert.All(r.Renglones.Take(3), x => Assert.Equal("PEREZ JUAN", x.SinFirmar[0].Lectura.Empleador));

            // La segunda revisión sale de la memoria: un OCR que falla no se llega a usar.
            LeerImagen roto = (_, _, _) => throw new InvalidOperationException("no debería leer de nuevo");
            var otra = await Clientes.Revisar(cliente, 10, 2026, null, roto, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(r.Renglones.Select(x => x.Firma), otra.Renglones.Select(x => x.Firma));

            // Un archivo que ya existe con el nombre de destino no se pisa: ni se propone.
            var firmados = r.Firmados!;
            var ocupado = Path.Combine(firmados, "GOMEZ LUCIA 10 2026.pdf");
            File.WriteAllText(ocupado, "de otro");
            Assert.DoesNotContain(Clientes.Planear(r, new HashSet<string>()), e => e.Ruta == ocupado);
            File.Delete(ocupado);
            var plan = Clientes.Planear(r, new HashSet<string> { Lopez.Cuil, Ruiz.Cuil });
            Assert.All(plan, e => Assert.False(File.Exists(e.Ruta)));
            Assert.Equal(
            [
                (Accion.Corregido, "GOMEZ LUCIA 10 2026.pdf"),
                (Accion.Corregido, "ROJAS PABLO 10 2026.pdf"), // encontrado solo por el nombre: se guarda igual
                (Accion.Iniciales, "LOPEZ ANA 10 2026.pdf"), // Ruiz no tiene recibo sin firmar: no hay sobre qué estampar
                (Accion.Consolidado, "PEREZ JUAN RECIBOS FIRMADOS 10 2026.pdf"), // los dos firmados, sin la de las iniciales
                (Accion.Lista, "FALTAN RECIBOS FIRMADOS 10 2026.txt"),
            ], plan.Select(e => (e.Accion, Path.GetFileName(e.Ruta))));
            // Todo va a los firmados: el PDF sin firmar de todos juntos es el que el estudio le manda al cliente, y no se
            // separa ni se toca.
            Assert.All(plan, e => Assert.Equal(firmados, Path.GetDirectoryName(e.Ruta)));

            // Una foto «corregida» de mentira: lo que importa acá es que el PDF se arme con ella.
            var jpeg = await WindowsOcr.Jpeg(Enumerable.Repeat((byte)200, 8 * 6 * 4).ToArray(), 8, 6);
            Corregir corregir = h => Task.FromResult((jpeg, 8, 6));
            foreach (var e in plan.Where(e => e.Accion != Accion.Lista)) await Clientes.Escribir(e, r, corregir);

            var iniciales = plan.Single(e => e.Accion == Accion.Iniciales);
            using (var d = PdfDocument.Open(iniciales.Ruta))
            {
                Assert.Contains("iniciales", d.Information.Keywords);
                Assert.Contains("L.A.", string.Join(" ", d.GetPage(1).GetWords().Select(w => w.Text)));
            }
            using (var d = PdfDocument.Open(plan[0].Ruta)) Assert.Equal(1, d.NumberOfPages);
            using (var d = PdfDocument.Open(plan.Single(e => e.Accion == Accion.Consolidado).Ruta))
            {
                Assert.Equal(2, d.NumberOfPages);
                Assert.Contains($"CUIL {Gomez.Cuil}", Palabras(d.GetPage(1)));
                Assert.Contains($"POR NOMBRE {Rojas.Cuil}", Palabras(d.GetPage(2)));
            }

            // Si mientras tanto apareció el archivo, falla en vez de pisarlo.
            await Assert.ThrowsAsync<IOException>(() => Clientes.Escribir(iniciales, r, corregir));

            // La lista se escribe con el cliente revisado de nuevo: López ya tiene las iniciales del estudio, y Rojas
            // sigue para revisar, también en la lista. Su PDF guardado lleva su CUIL en las palabras clave, pero lo puso
            // la app: no lo vuelve firmado.
            var nueva = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.PorNombre, Firma.ConIniciales, Firma.Falta], nueva.Renglones.Select(x => x.Firma));
            await Clientes.Escribir(plan[^1], nueva, corregir);
            var lista = File.ReadAllText(plan[^1].Ruta);
            salida.WriteLine(lista);
            Assert.Contains("Falta 1:", lista);
            Assert.Contains(Ruiz.Cuil, lista);
            Assert.Contains($"ROJAS PABLO · CUIL {Rojas.Cuil}", lista[lista.IndexOf("Encontrados por el nombre")..]);
            Assert.Contains("WhatsApp Image 3.jpeg", lista);

            // Con todo guardado, una revisión nueva no propone nada más que lo que sigue faltando: ni un archivo repetido.
            var despues = Clientes.Planear(nueva, new HashSet<string> { Lopez.Cuil, Ruiz.Cuil });
            Assert.Empty(despues);
        }
        finally
        {
            Directory.Delete(raiz, true);
        }
    }

    [Fact]
    public async Task Sin_nada_legible_usa_el_nombre_del_archivo()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: true);
            var firmados = Path.Combine(cliente, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026", "PEREZ JUAN SUELDOS OCTUBRE 2026", "RECIBOS FIRMADOS OCTUBRE");
            File.WriteAllBytes(Path.Combine(firmados, "RUIZ LUIS RECIBO OCT 2026.jpeg"), [0xFF, 0xD8]);
            File.WriteAllBytes(Path.Combine(firmados, "BAJA LOPEZ ANA.jpeg"), [0xFF, 0xD8]);
            var r = await Clientes.Revisar(cliente, 10, 2026, null, Leer, new Memoria(Path.Combine(raiz, "m.json")), _ => { }, CancellationToken.None);
            Assert.Equal(Firma.PorNombre, r.Renglones.Single(x => x.Empleado == Ruiz).Firma);
            Assert.Equal(Firma.Falta, r.Renglones.Single(x => x.Empleado == Lopez).Firma);
            // La constancia de una baja se reconoce como tal: no es el recibo de nadie, ni queda «sin identificar».
            Assert.DoesNotContain(r.Ilegibles, h => Path.GetFileName(h.Ruta) == "BAJA LOPEZ ANA.jpeg");
            Assert.Contains(r.Ilegibles, h => Path.GetFileName(h.Ruta) == "WhatsApp Image 3.jpeg");
        }
        finally
        {
            Directory.Delete(raiz, true);
        }
    }

    // La lectura rápida no encuentra a nadie en la tercera foto; la lectura a fondo sí. Después no se vuelve a intentar.
    [Fact]
    public async Task Las_dificiles_se_leen_a_fondo_una_sola_vez()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: true);
            var r = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            var dificiles = Clientes.Dificiles(r);
            // La tercera foto (sin nadie) y la segunda (solo por el nombre).
            Assert.Equal(["WhatsApp Image 2.jpeg", "WhatsApp Image 3.jpeg"], dificiles.Select(h => Path.GetFileName(h.Ruta)).Order());

            var leidas = new List<string>();
            LeerImagen aFondo = (ruta, hoja, buscado) =>
            {
                lock (leidas) leidas.Add(Path.GetFileName(ruta));
                var texto = Path.GetFileName(ruta) == "WhatsApp Image 3.jpeg" ? $"CUIL {Ruiz.Cuil}" : $"CUIL {Rojas.Cuil}";
                Assert.True(buscado(texto)); // el lector a fondo sabe cuándo parar
                return Task.FromResult(new Lectura(texto, true, 90, 0));
            };
            Assert.Equal(2, await Clientes.Profundizar(r, aFondo, memoria, _ => { }, CancellationToken.None));
            var nueva = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.Firmado, Firma.Falta, Firma.Firmado], nueva.Renglones.Select(x => x.Firma));
            Assert.Empty(nueva.Ilegibles);
            Assert.Empty(Clientes.Dificiles(nueva));
            Assert.Equal(90, nueva.Renglones[3].Firmado.Single().Lectura.Giro); // el giro con que se leyó, para enderezarla

            // Una segunda vuelta no lee nada: ya están leídas a fondo.
            Assert.Equal(0, await Clientes.Profundizar(nueva, (_, _, _) => throw new InvalidOperationException("no debería leer"), memoria, _ => { }, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(raiz, true);
        }
    }

    // La versión 1.1.0 guardaba todo en una sola memoria. La de un mes arranca con lo que esa tenía leído con OCR de ese
    // mes: las fotos no se leen de nuevo. Lo demás se relee.
    [Fact]
    public void La_memoria_de_un_mes_hereda_las_lecturas_con_ocr_de_la_anterior()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string octubre = Directory.CreateDirectory(Path.Combine(raiz, "PEREZ SUELDOS OCTUBRE 2026")).FullName,
                noviembre = Directory.CreateDirectory(Path.Combine(raiz, "PEREZ SUELDOS NOVIEMBRE 2026")).FullName;
            FileInfo Archivo(string carpeta, string nombre)
            {
                File.WriteAllText(Path.Combine(carpeta, nombre), "x");
                return new FileInfo(Path.Combine(carpeta, nombre));
            }
            FileInfo foto = Archivo(octubre, "foto.jpeg"), recibos = Archivo(octubre, "RECIBOS.pdf"), otra = Archivo(noviembre, "foto.jpeg");
            var anterior = new Memoria(Path.Combine(raiz, "lecturas-3.json"));
            anterior.Anotar(foto, "", [new Lectura("CUIL 20-12345678-6", Ocr: true, Giro: 90)]);
            anterior.Anotar(recibos, "", [new Lectura("FIRMA DEL EMPLEADO", Nombre: "PEREZ JUAN")]);
            anterior.Anotar(otra, "", [new Lectura("CUIL 20-45678901-4", Ocr: true)]);
            anterior.Guardar();

            var delMes = new Memoria(Path.Combine(raiz, "lecturas-4-2026-10.json"), Path.Combine(raiz, "lecturas-3.json"),
                ruta => Path.GetDirectoryName(ruta)!.Split('\\', '/').Any(c => Clientes.EsDelMes(c, 10, 2026)));
            // La foto de octubre viene con su giro; el PDF con texto se relee (así trae el empleador), y noviembre no es de acá.
            Assert.Equal(90, delMes.Buscar(foto)!.Hojas[0].Giro);
            Assert.Null(delMes.Buscar(recibos));
            Assert.Null(delMes.Buscar(otra));

            // Una vez guardada la del mes, manda ella: lo que se anote después en la anterior ya no entra.
            delMes.Guardar();
            anterior.Anotar(recibos, "", [new Lectura("otra cosa", Ocr: true)]);
            anterior.Guardar();
            Assert.Null(new Memoria(Path.Combine(raiz, "lecturas-4-2026-10.json"), Path.Combine(raiz, "lecturas-3.json"), _ => true).Buscar(recibos));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Separa_tambien_las_hojas_escaneadas_que_traen_texto_propio()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var mes = Path.Combine(cliente, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026", "PEREZ JUAN SUELDOS OCTUBRE 2026");
            var firmados = Directory.CreateDirectory(Path.Combine(mes, "RECIBOS FIRMADOS OCTUBRE")).FullName;
            // Dos recibos por hoja.
            Escaneo(Path.Combine(firmados, "CLIENTE RECIBOS 10 2026.pdf"), $"FIRMA DEL EMPLEADO {Gomez.Cuil} {Rojas.Cuil}");
            // Y uno hecho en la computadora: ese ya está bien como está.
            Pdf(Path.Combine(firmados, "LOPEZ DIGITAL.pdf"), Recibo(Lopez));

            var r = await Clientes.Revisar(cliente, 10, 2026, null, null, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.Firmado, Firma.Firmado], r.Renglones.Where(x => x.Empleado != Ruiz).Select(x => x.Firma));
            var corregidos = Clientes.Planear(r, new HashSet<string>()).Where(e => e.Accion == Accion.Corregido).Select(e => e.Empleado).ToList();
            Assert.Equal([Gomez, Rojas], corregidos);
        }
        finally { Directory.Delete(raiz, true); }
    }

    // Como Adobe Scan: en cada hoja, la foto de la hoja entera con el texto que leyó por encima.
    static void Escaneo(string ruta, params string[] hojas)
    {
        var b = new PdfDocumentBuilder();
        var fuente = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var texto in hojas)
        {
            var p = b.AddPage(PageSize.A4);
            p.AddJpeg(Convert.FromBase64String(Jpeg), new PdfRectangle(0, 0, p.PageSize.Width, p.PageSize.Height));
            p.AddText(texto, 8, new PdfPoint(40, 400), fuente);
        }
        File.WriteAllBytes(ruta, b.Build());
    }

    [Fact]
    public async Task El_consolidado_se_completa_sin_tocar_lo_que_tenia()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var mes = Path.Combine(cliente, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026", "PEREZ JUAN SUELDOS OCTUBRE 2026");
            var firmados = Directory.CreateDirectory(Path.Combine(mes, "RECIBOS FIRMADOS OCTUBRE")).FullName;
            // El consolidado que armó el estudio, con el nombre de siempre, trae a dos. López llega después en un PDF hecho
            // en la computadora, y Ruiz en una foto donde se lo encuentra solo por el nombre.
            var consolidado = Path.Combine(firmados, "PEREZ JUAN RECIBOS FIRMADOS 10 2026.pdf");
            Escaneo(consolidado, $"FIRMA DEL EMPLEADO {Gomez.Cuil} {Rojas.Cuil}");
            Pdf(Path.Combine(firmados, "LOPEZ DIGITAL.pdf"), Recibo(Lopez));
            File.WriteAllBytes(Path.Combine(firmados, "foto.jpeg"), [0xFF, 0xD8]);
            LeerImagen foto = (ruta, _, _) => Path.GetFileName(ruta) == "foto.jpeg"
                ? Task.FromResult(new Lectura("APELLIDO Y NOMBRE RUIZ, LUIS", Ocr: true))
                : throw new InvalidOperationException($"no debería leer {Path.GetFileName(ruta)} con OCR");
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, foto, memoria, _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.Firmado, Firma.Firmado, Firma.PorNombre], r.Renglones.Select(x => x.Firma));

            // Se completa con los dos que le faltan, en el orden de los recibos sin firmar.
            var e = Clientes.Planear(r, new HashSet<string>()).Single(e => e.Accion == Accion.Consolidado);
            Assert.Equal(consolidado, e.Ruta);
            Assert.Equal(["LOPEZ DIGITAL.pdf", "foto.jpeg"], e.Hojas.Select(h => Path.GetFileName(h.Ruta)));
            await Clientes.Escribir(e, r, _ => Task.FromResult((Convert.FromBase64String(Jpeg), 2, 2)));
            Assert.False(File.Exists(consolidado + ".nuevo"));
            using (var d = PdfDocument.Open(consolidado))
            {
                Assert.Equal(3, d.NumberOfPages);
                // La hoja que tenía sigue con su imagen y su texto; la de López, tal cual; la foto, derecha y con quién es.
                Assert.Single(d.GetPage(1).GetImages());
                Assert.Contains("FIRMA DEL EMPLEADO", Palabras(d.GetPage(1)));
                Assert.Contains("SUELDO NETO", Palabras(d.GetPage(2)));
                Assert.Contains($"POR NOMBRE {Ruiz.Cuil} RUIZ LUIS", Palabras(d.GetPage(3)));
            }

            // La revisión siguiente lo lee sin OCR, Ruiz sigue para revisar y ya no hay nada que sumarle.
            var despues = await Clientes.Revisar(cliente, 10, 2026, null, foto, memoria, _ => { }, CancellationToken.None);
            Assert.Empty(despues.Avisos);
            Assert.Equal(r.Renglones.Select(x => x.Firma), despues.Renglones.Select(x => x.Firma));
            Assert.All(despues.Renglones.Where(x => x.Firma != Firma.Falta), x => Assert.Contains(x.Firmado, h => h.Ruta == consolidado));
            Assert.DoesNotContain(Clientes.Planear(despues, new HashSet<string>()), e => e.Accion == Accion.Consolidado);
        }
        finally { Directory.Delete(raiz, true); }
    }

    static string Palabras(Page p) => string.Join(" ", p.GetWords().Select(w => w.Text));

    // Un JPEG blanco de 2 por 2: alcanza para que el PDF tenga una imagen.
    const string Jpeg = "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAAAqADAAQAAAABAAAAAgAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgAAgACAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMFBgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwMDA4ODg4ODw8PDw8PDw8PD//bAEMBAgICBAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEP/dAAQAAf/aAAwDAQACEQMRAD8A/fyiiigD/9k=";

    [Fact]
    public async Task Crea_la_carpeta_de_firmados_solo_si_hay_que_guardar_algo()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var r = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.Null(r.Firmados);
            Assert.All(r.Renglones, x => Assert.Equal(Firma.Falta, x.Firma));

            // Sin iniciales elegidas no hay nada para la carpeta de firmados: no se crea.
            Assert.DoesNotContain(Clientes.Planear(r, new HashSet<string>()), e => e.Accion is Accion.Carpeta or Accion.Lista);

            var plan = Clientes.Planear(r, new HashSet<string> { Gomez.Cuil });
            Assert.Equal(Accion.Carpeta, plan[0].Accion);
            Assert.Equal("RECIBOS FIRMADOS OCTUBRE", Path.GetFileName(plan[0].Ruta));
            Assert.Equal(plan[0].Ruta, Path.GetDirectoryName(plan.Single(e => e.Accion == Accion.Iniciales).Ruta));
            Assert.Equal(Accion.Lista, plan[^1].Accion);
            foreach (var e in plan.Where(e => e.Accion != Accion.Lista)) await Clientes.Escribir(e, r, _ => throw new InvalidOperationException());
            var nueva = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(plan[0].Ruta, nueva.Firmados);
            Assert.Equal(Firma.ConIniciales, nueva.Renglones[0].Firma);

            // Si después llega una foto donde se la encuentra solo por el nombre, queda para revisar: las iniciales del
            // estudio no la vuelven firmada.
            File.WriteAllBytes(Path.Combine(nueva.Firmados!, "foto.jpeg"), [0xFF, 0xD8]);
            LeerImagen sinCuil = (_, _, _) => Task.FromResult(new Lectura("APELLIDO Y NOMBRE GOMEZ, LUCIA", Ocr: true));
            var conFoto = await Clientes.Revisar(cliente, 10, 2026, null, sinCuil, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(Firma.PorNombre, conFoto.Renglones[0].Firma);
            // Su nombre de archivo ya lo tiene la copia con iniciales, y eso no se pisa.
            Assert.DoesNotContain(Clientes.Planear(conFoto, new HashSet<string>()), e => e.Accion == Accion.Corregido);
        }
        finally
        {
            Directory.Delete(raiz, true);
        }
    }

    [Fact]
    public async Task El_apellido_del_empleador_y_el_nombre_de_un_companero_no_son_otro_empleado()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            // En la nómina hay además un Pérez Luis: el apellido del empleador (Pérez Juan) y el nombre de Ruiz.
            var perez = new Empleado("20-12345678-6", "PEREZ LUIS");
            Pdf(Path.Combine(Mes(cliente), "SUSS Y SIND", "PEREZ JUAN SUSS NOMINA 10 2026.pdf"), Nomina(Gomez, Rojas, Ruiz, perez));
            File.WriteAllBytes(Path.Combine(Firmados(cliente), "RUIZ.jpeg"), [0xFF, 0xD8]);
            // El recibo firmado de Ruiz, con el empleador en el encabezado, como todos.
            LeerImagen foto = (_, _, _) => Task.FromResult(new Lectura($"PEREZ JUAN CUIT {Empleador} APELLIDO Y NOMBRE RUIZ, LUIS CUIL {Ruiz.Cuil}", Ocr: true));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, foto, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            Assert.Equal(Firma.Firmado, r.Renglones.Single(x => x.Empleado == Ruiz).Firma);
            // A Pérez Luis le falta su recibo: en el de Ruiz, «PEREZ» es del empleador y «LUIS» es de Ruiz.
            Assert.Equal(Firma.Falta, r.Renglones.Single(x => x.Empleado == perez).Firma);
        }
        finally { Directory.Delete(raiz, true); }
    }

    // ---- Separar ----

    [Fact]
    public async Task Separar_no_junta_a_dos_que_se_llaman_igual()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // Dos Gómez Lucía con distinto CUIL, y López, que no tiene tocayo.
            var otra = Rojas with { Nombre = Gomez.Nombre };
            var pdf = Path.Combine(raiz, "RECIBOS 10 2026.pdf");
            Pdf(pdf, Recibo(Gomez), Recibo(otra), Recibo(Lopez));
            var r = await Recibos.Procesar([pdf], null, _ => { }, iniciales: false);
            // Cada una en su archivo, con el CUIL en el nombre para distinguirlas; López, como siempre.
            Assert.Equal(new[] { $"GOMEZ LUCIA {Gomez.Cuil} 10 2026.pdf", $"GOMEZ LUCIA {otra.Cuil} 10 2026.pdf", "LOPEZ ANA 10 2026.pdf" }.Order(),
                r.Creados.Select(Path.GetFileName).Order());
            foreach (var creado in r.Creados)
            {
                using var d = PdfDocument.Open(creado);
                Assert.Equal(1, d.NumberOfPages);
            }
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Separar_pregunta_antes_de_escribir_y_no_pisa_lo_que_ya_estaba()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var pdf = Path.Combine(raiz, "RECIBOS 10 2026.pdf");
            Pdf(pdf, Recibo(Gomez), Recibo(Lopez));
            var carpeta = Path.Combine(raiz, "PEREZ JUAN");
            IReadOnlyList<string> pedidos = [], combinados = [];
            Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<bool>> Responde(bool si) => (rutas, juntos) =>
            {
                pedidos = rutas;
                combinados = juntos;
                return Task.FromResult(si);
            };

            // Si dicen que no, no se escribe nada: ni la carpeta del empleador. El combinado se pregunta aparte.
            var nada = await Recibos.Procesar([pdf], null, _ => { }, iniciales: false, permiso: Responde(false));
            Assert.Equal(["GOMEZ LUCIA 10 2026.pdf", "LOPEZ ANA 10 2026.pdf"], pedidos.Select(Path.GetFileName));
            Assert.Equal(["PEREZ JUAN RECIBOS 10 2026.pdf"], combinados.Select(Path.GetFileName));
            Assert.Empty(nada.Creados);
            Assert.Empty(nada.Combinados);
            Assert.False(Directory.Exists(carpeta));

            // El de Gómez ya estaba, puesto por otro: queda como está, y el permiso se pide solo por el de López.
            Directory.CreateDirectory(carpeta);
            var deOtro = Path.Combine(carpeta, "GOMEZ LUCIA 10 2026.pdf");
            File.WriteAllText(deOtro, "de otro");
            var r = await Recibos.Procesar([pdf], null, _ => { }, iniciales: false, permiso: Responde(true));
            Assert.Equal(["LOPEZ ANA 10 2026.pdf"], pedidos.Select(Path.GetFileName));
            Assert.Equal([deOtro], r.YaEstaban);
            Assert.Equal("de otro", File.ReadAllText(deOtro));
            var deLopez = Assert.Single(r.Creados);
            Assert.Equal(Path.Combine(carpeta, "LOPEZ ANA 10 2026.pdf"), deLopez);

            // El combinado lleva a los dos, también a la de Gómez que ya estaba.
            using (var d = PdfDocument.Open(Assert.Single(r.Combinados))) Assert.Equal(2, d.NumberOfPages);

            // Otra vez: los dos recibos y el combinado ya están, así que no hay nada que escribir ni que preguntar.
            var antes = File.ReadAllBytes(deLopez);
            var otra = await Recibos.Procesar([pdf], null, _ => { }, iniciales: false,
                permiso: (_, _) => throw new InvalidOperationException("no hay nada que preguntar"));
            Assert.Empty(otra.Creados);
            Assert.Empty(otra.Combinados);
            Assert.Equal(2, otra.YaEstaban.Count);
            Assert.Equal(antes, File.ReadAllBytes(deLopez));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Separar_arma_el_combinado_y_lo_completa_sin_tocar_lo_que_tenia()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var pdf = Path.Combine(raiz, "RECIBOS 10 2026.pdf");
            Pdf(pdf, Recibo(Gomez), Recibo(Lopez));
            var r = await Recibos.Procesar([pdf], null, _ => { });
            var combinado = Path.Combine(raiz, "PEREZ JUAN", "PEREZ JUAN RECIBOS FIRMADOS 10 2026.pdf");
            Assert.Equal([combinado], r.Combinados);
            Assert.Equal(2, r.Creados.Count); // el combinado no cuenta como recibo
            string antes;
            using (var d = PdfDocument.Open(combinado))
            {
                Assert.Equal(2, d.NumberOfPages);
                // Las iniciales se ven, y quién está en la hoja también va escrito, sin verse.
                var letras = d.GetPage(1).Letters;
                Assert.Contains("G.L.", string.Concat(letras.Where(l => l.RenderingMode == TextRenderingMode.Fill).Select(l => l.Value)));
                Assert.Contains($"CUIL{Gomez.Cuil}", string.Concat(letras.Where(l => l.RenderingMode == TextRenderingMode.Neither).Select(l => l.Value)).Replace(" ", ""));
                antes = Palabras(d.GetPage(1)) + Palabras(d.GetPage(2));
            }

            // Con todos adentro, otra vez no se toca.
            var bytes = File.ReadAllBytes(combinado);
            Assert.Empty((await Recibos.Procesar([pdf], null, _ => { })).Combinados);
            Assert.Equal(bytes, File.ReadAllBytes(combinado));

            // Llega el recibo de Ruiz en otro PDF: se le suma al final, y lo que tenía queda igual.
            var otro = Path.Combine(raiz, "RECIBO RUIZ 10 2026.pdf");
            Pdf(otro, Recibo(Ruiz));
            Assert.Equal([combinado], (await Recibos.Procesar([otro], null, _ => { })).Combinados);
            Assert.False(File.Exists(combinado + ".nuevo"));
            using (var d = PdfDocument.Open(combinado))
            {
                Assert.Equal(3, d.NumberOfPages);
                Assert.Equal(antes, Palabras(d.GetPage(1)) + Palabras(d.GetPage(2)));
                Assert.Contains(Ruiz.Cuil, Palabras(d.GetPage(3)));
            }
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Separar_busca_la_nomina_en_la_carpeta_del_mes_como_Clientes()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // Los recibos en la carpeta del mes y la nómina adentro, en «SUSS Y SIND», como los deja el estudio.
            var mes = Mes(Cliente(raiz, "PEREZ JUAN", conFirmados: false));
            var recibos = Path.Combine(mes, "PEREZ JUAN RECIBOS 10 2026.PDF");
            var n = Assert.Single((await Recibos.Procesar([recibos], null, _ => { }, iniciales: false)).Nominas);
            Assert.EndsWith("PEREZ JUAN SUSS NOMINA 10 2026.pdf", n.Ruta);
            Assert.Equal([Gomez, Rojas, Lopez, Ruiz], n.Empleados);
            var revision = Recibos.Revisar(n, [Path.Combine(mes, "PEREZ JUAN")]);
            Assert.Equal([Ruiz], revision.Where(x => x.Recibo is null).Select(x => x.Empleado));
            Assert.DoesNotContain(revision, x => x.PorNombre);

            // Con esa misma ya cargada, no se vuelve a leer. La de otro cliente del mismo mes no la reemplaza: no trae a nadie
            // de estos recibos.
            Assert.Empty((await Recibos.Procesar([recibos], null, _ => { }, iniciales: false, conocida: n)).Nominas);
            var ajena = new Nomina("ajena.pdf", 10, "2026", [new("20-22333444-0", "QUIROGA ZOE")]);
            Assert.Single((await Recibos.Procesar([recibos], null, _ => { }, iniciales: false, conocida: ajena)).Nominas);

            // Una foto sin rótulo, sin período y sin CUIL, entre los firmados que nombran el mes («RECIBOS FIRMADOS 10 2026», como
            // los de un cliente real): el mes y la nómina salen de la carpeta del mes, y el empleador, de los recibos sin firmar.
            var firmados = Directory.CreateDirectory(Path.Combine(mes, "RECIBOS FIRMADOS 10 2026")).FullName;
            var foto = Path.Combine(firmados, "FOTO.pdf");
            Pdf(foto, [("RUIZ LUIS", 40, 760), ("FIRMA DEL EMPLEADO", 350, 200)]);
            Assert.Equal(Path.Combine(firmados, "PEREZ JUAN", "RUIZ LUIS 10 2026.pdf"),
                Assert.Single((await Recibos.Procesar([foto], null, _ => { }, iniciales: false)).Creados));

            // Fuera de una carpeta del mes no se busca, como en Clientes: Descargas puede tener las de otros clientes.
            var descargas = Directory.CreateDirectory(Path.Combine(raiz, "Descargas")).FullName;
            File.Copy(recibos, Path.Combine(descargas, "RECIBOS.pdf"));
            Pdf(Path.Combine(descargas, "NOMINA 10 2026.pdf"), Nomina(Gomez, Rojas, Lopez, Ruiz));
            Assert.Empty((await Recibos.Procesar([Path.Combine(descargas, "RECIBOS.pdf")], null, _ => { }, iniciales: false)).Nominas);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Separar_guarda_lo_que_encuentra_por_el_nombre()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mes = Mes(Cliente(raiz, "PEREZ JUAN", conFirmados: false));
            // La nómina pone a López con el nombre primero; su recibo sin firmar, como siempre.
            Pdf(Path.Combine(mes, "SUSS Y SIND", "PEREZ JUAN SUSS NOMINA 10 2026.pdf"), Nomina(Gomez, Rojas, Lopez with { Nombre = "ANA LOPEZ" }, Ruiz));
            // Un escaneo con lo que dejó el OCR: Gómez se lee entera; de Rojas, el recibo pero no el CUIL; la tercera hoja trae
            // dos recibos sin el rótulo del nombre, el de Ruiz con su CUIL y el de López solo con su nombre (que va primero en
            // la carpeta: así, si la revisión mirara el texto de la hoja, le daría a Ruiz el archivo de López); la cuarta no se
            // lee.
            var escaneo = Path.Combine(mes, "ESCANEO 10 2026.pdf");
            Pdf(escaneo, Recibo(Gomez),
                Recibo(Rojas).Select(t => t.Item1 == Rojas.Cuil ? ("2?-4??78901-4", t.Item2, t.Item3) : t).ToArray(),
                [("CUIL 20-11222333-9 RUIZ LUIS", 40, 760), ("LOPEZ ANA", 300, 760), ("FIRMA DEL EMPLEADO", 350, 200)],
                [("a o 1 / . ;", 40, 400)]);
            var avisos = new List<string>();
            var r = await Recibos.Procesar([escaneo], null, avisos.Add, iniciales: false);

            // López, encontrado con la nómina, se llama como en su recibo sin firmar; Ruiz no tiene, y queda como en la nómina.
            var carpeta = Path.Combine(mes, "PEREZ JUAN");
            Assert.Equal(new[] { "GOMEZ LUCIA", "ROJAS PABLO", "LOPEZ ANA", "RUIZ LUIS" }.Select(x => $"{x} 10 2026.pdf").Order(),
                r.Creados.Select(Path.GetFileName).Order());
            // Los encontrados por el nombre llevan la marca, como los que guarda Clientes; Ruiz, por su CUIL, no.
            foreach (var (archivo, marca) in new[] { ("GOMEZ LUCIA", false), ("ROJAS PABLO", true), ("LOPEZ ANA", true), ("RUIZ LUIS", false) })
            {
                using var d = PdfDocument.Open(Path.Combine(carpeta, $"{archivo} 10 2026.pdf"));
                Assert.Equal(marca, d.Information.Keywords!.Contains("por nombre"));
            }
            // Cada uno con su archivo, aunque la hoja de López traiga el CUIL de Ruiz.
            var revision = Recibos.Revisar(Assert.Single(r.Nominas), [carpeta]);
            Assert.All(revision, x => Assert.StartsWith(Path.Combine(carpeta, x.Empleado.Cuil == Lopez.Cuil ? "LOPEZ ANA" : x.Empleado.Nombre), x.Recibo));
            Assert.Equal([Rojas.Cuil, Lopez.Cuil], revision.Where(x => x.PorNombre).Select(x => x.Empleado.Cuil));

            // En el combinado, la hoja de los dos recibos va una sola vez, con los dos; López, «POR NOMBRE».
            using (var d = PdfDocument.Open(Assert.Single(r.Combinados)))
            {
                Assert.Equal(3, d.NumberOfPages);
                var quienes = string.Concat(d.GetPage(3).Letters.Where(l => l.RenderingMode == TextRenderingMode.Neither).Select(l => l.Value)).Replace(" ", "");
                Assert.Contains($"CUIL{Ruiz.Cuil}", quienes);
                Assert.Contains($"PORNOMBRE{Lopez.Cuil}", quienes);
            }
            // La que no tiene a nadie sigue avisando, como antes.
            Assert.Contains("hoja 4", Assert.Single(avisos, a => a.StartsWith('⚠')));
        }
        finally { Directory.Delete(raiz, true); }
    }

    // ---- La misma imagen una sola vez, la pasada sin OCR y los recibos de otro mes ----

    static (string, double, double)[][] HojasSinTexto(int cuantas) => Enumerable.Range(0, cuantas).Select(_ => Array.Empty<(string, double, double)>()).ToArray();

    [Fact]
    public async Task La_misma_imagen_repetida_se_lee_una_sola_vez()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var firmados = Firmados(cliente);
            // La foto de Gómez bajada dos veces, y un PDF escaneado que la trae dos veces más, con la de Rojas al final.
            foreach (var foto in new[] { "gomez.jpeg", "gomez (1).jpeg" }) File.WriteAllBytes(Path.Combine(firmados, foto), [0xFF, 0xD8]);
            Pdf(Path.Combine(firmados, "TODOS.pdf"), HojasSinTexto(3));
            static bool DeRojas(string ruta, int hoja) => Path.GetFileName(ruta) == "TODOS.pdf" && hoja == 3;
            // La huella de la prueba: la de cada imagen, esté en el archivo que esté.
            SacarHuella huella = (ruta, hoja) => Task.FromResult<string?>(DeRojas(ruta, hoja) ? "rojas" : "gomez");
            var lecturas = 0;
            LeerImagen leer = (ruta, hoja, _) =>
            {
                Interlocked.Increment(ref lecturas);
                return Task.FromResult(new Lectura($"CUIL {(DeRojas(ruta, hoja) ? Rojas : Gomez).Cuil} PERIODO DE PAGO 10/2026", Ocr: true));
            };
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, leer, memoria, _ => { }, CancellationToken.None, huella);
            // Cinco hojas y dos imágenes distintas: dos lecturas. Las cinco cuentan igual.
            Assert.Equal(2, lecturas);
            Assert.Equal([4, 1, 0, 0], r.Renglones.Select(x => x.Firmado.Count));
            // Otra copia que llega después se reconoce por la huella, sin leerla.
            File.WriteAllBytes(Path.Combine(firmados, "gomez (2).jpeg"), [0xFF, 0xD8]);
            r = await Clientes.Revisar(cliente, 10, 2026, null, leer, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Equal(2, lecturas);
            Assert.Equal(5, r.Renglones[0].Firmado.Count);
            // Y en el PDF que se guarda de Gómez, la hoja va una sola vez.
            Assert.Single(Assert.Single(Clientes.Planear(r, new HashSet<string>()), e => e.Accion == Accion.Corregido && e.Empleado == Gomez).Hojas);
            // Sin la huella (un OCR que no la da) se lee todo, como antes.
            lecturas = 0;
            await Clientes.Revisar(cliente, 10, 2026, null, leer, new Memoria(Path.Combine(raiz, "otra.json")), _ => { }, CancellationToken.None);
            Assert.Equal(6, lecturas);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Lo_leido_antes_de_la_huella_la_recibe_y_sus_copias_se_reconocen()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var firmados = Firmados(cliente);
            foreach (var foto in new[] { "gomez.jpeg", "gomez (1).jpeg" }) File.WriteAllBytes(Path.Combine(firmados, foto), [0xFF, 0xD8]);
            // Las dos leídas con una versión de antes, sin la huella.
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            foreach (var foto in Directory.GetFiles(firmados)) memoria.Anotar(new FileInfo(foto), "", [new Lectura($"CUIL {Gomez.Cuil}", Ocr: true)]);
            SacarHuella huella = (_, _) => Task.FromResult<string?>("la misma");
            LeerImagen nadie = (_, _, _) => throw new InvalidOperationException("ya está leída");
            // Sin la huella, la misma foto va dos veces en su PDF; con ella, una, y queda guardada en la memoria.
            var r = await Clientes.Revisar(cliente, 10, 2026, null, null, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(2, Clientes.Planear(r, new HashSet<string>()).Single(e => e.Accion == Accion.Corregido).Hojas.Count);
            r = await Clientes.Revisar(cliente, 10, 2026, null, null, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Single(Clientes.Planear(r, new HashSet<string>()).Single(e => e.Accion == Accion.Corregido).Hojas);
            Assert.All(Directory.GetFiles(firmados), foto => Assert.Equal("la misma", memoria.Buscar(new FileInfo(foto))!.Hojas[0].Huella));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task La_lectura_a_fondo_lee_una_vez_cada_imagen()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            // La misma foto ilegible, tres veces.
            foreach (var foto in new[] { "a.jpeg", "b.jpeg", "c.jpeg" }) File.WriteAllBytes(Path.Combine(Firmados(cliente), foto), [0xFF, 0xD8]);
            SacarHuella huella = (_, _) => Task.FromResult<string?>("la misma");
            LeerImagen rapida = (_, _, _) => Task.FromResult(new Lectura("x 1 / ;", Ocr: true));
            var aFondo = 0;
            LeerImagen fondo = (_, _, _) =>
            {
                Interlocked.Increment(ref aFondo);
                return Task.FromResult(new Lectura($"CUIL {Gomez.Cuil}", Ocr: true, Giro: 90));
            };
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, rapida, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Equal(3, r.Ilegibles.Count);
            Assert.Equal(3, await Clientes.Profundizar(r, fondo, memoria, _ => { }, CancellationToken.None));
            Assert.Equal(1, aFondo);
            // Las tres copias quedan con lo que se leyó de una.
            r = await Clientes.Revisar(cliente, 10, 2026, null, rapida, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Empty(r.Ilegibles);
            Assert.Equal(3, r.Renglones[0].Firmado.Count);
            Assert.All(r.Renglones[0].Firmado, h => Assert.Equal(90, h.Lectura.Giro));
            Assert.Empty(Clientes.Dificiles(r));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task La_copia_de_un_PDF_ya_leido_a_fondo_no_se_lee_de_nuevo()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            // Un escaneo con el texto del escáner, que no deja ver a nadie: va a la lectura a fondo, y ahí tampoco aparece.
            Escaneo(Path.Combine(Firmados(cliente), "ESCANEO.pdf"), "FIRMA DEL EMPLEADO x 1 / ;");
            SacarHuella huella = (_, _) => Task.FromResult<string?>("la del escaneo");
            LeerImagen nadie = (_, _, _) => throw new InvalidOperationException("tiene texto: la lectura rápida no la toca");
            var aFondo = 0;
            LeerImagen fondo = (_, _, _) =>
            {
                Interlocked.Increment(ref aFondo);
                return Task.FromResult(new Lectura("y 2 / ;", Ocr: true));
            };
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, nadie, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Equal(1, await Clientes.Profundizar(r, fondo, memoria, _ => { }, CancellationToken.None, huella));
            Assert.Equal(1, aFondo);
            // Lo bajan otra vez, con otro nombre: es la misma imagen, y lo que se sacó de ella es lo mejor que se pudo.
            File.Copy(Path.Combine(Firmados(cliente), "ESCANEO.pdf"), Path.Combine(Firmados(cliente), "ESCANEO (1).pdf"));
            r = await Clientes.Revisar(cliente, 10, 2026, null, nadie, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Single(Clientes.Dificiles(r));
            await Clientes.Profundizar(r, fondo, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Equal(1, aFondo);
            r = await Clientes.Revisar(cliente, 10, 2026, null, nadie, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Empty(Clientes.Dificiles(r));
            Assert.Equal(2, r.Ilegibles.Count);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Sin_OCR_dice_cuantas_hojas_quedan_por_leer()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // Tres fotos y un escaneo de dos hojas entre los firmados.
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: true);
            Pdf(Path.Combine(Firmados(cliente), "ESCANEO.pdf"), HojasSinTexto(2));
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            // La pasada rápida no lee ninguna: dice cuántas esperan, y no las da por leídas.
            var r = await Clientes.Revisar(cliente, 10, 2026, null, null, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(5, r.PorLeer);
            LeerImagen leer = (ruta, _, _) => Task.FromResult(new Lectura(Fotos.GetValueOrDefault(Path.GetFileName(ruta), "x 1 / ;"), Ocr: true));
            r = await Clientes.Revisar(cliente, 10, 2026, null, leer, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(0, r.PorLeer);
            // Ya leídas, la pasada rápida siguiente tiene todo: no queda nada esperando.
            r = await Clientes.Revisar(cliente, 10, 2026, null, null, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(0, r.PorLeer);
            Assert.Equal(Firma.Firmado, r.Renglones[0].Firma);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Theory]
    [InlineData("PERIODO DE PAGO 09/2026", 9)]
    [InlineData("PERIODO DE PAGO | SUELDO BRUTO | 09/2026 | 721.677,15", 9)] // el OCR no siempre lo deja al lado del rótulo
    [InlineData("PERIODO DE PAGO 9 / 2026", 9)]
    [InlineData("PERIODO DE PAGO 10/2026", null)]
    [InlineData("PERIODO 10/2020", null)] // el año mal leído en una foto borrosa: el mes es el que se revisa
    [InlineData("FECHA INGRESO 01/09/2026", null)] // una fecha entera no es un período
    [InlineData("recibo de 09/2026 y recibo de 10/2026 en la misma hoja", null)]
    [InlineData("sin ningún período", null)]
    public void Reconoce_el_recibo_de_otro_mes(string texto, int? mes) => Assert.Equal(mes, Clientes.OtroMes(texto, 10));

    [Fact]
    public async Task Un_recibo_de_otro_mes_no_cuenta_y_se_avisa()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            var textos = new Dictionary<string, string>
            {
                // El de Gómez es el de septiembre; el de Rojas, de octubre con el año mal leído; el de López no deja ver el período.
                ["gomez.jpeg"] = $"CUIL {Gomez.Cuil} FECHA INGRESO 01/09/2024 PERIODO DE PAGO 09/2026",
                ["rojas.jpeg"] = $"CUIL {Rojas.Cuil} PERIODO DE PAGO 10/2020",
                ["lopez.jpeg"] = $"CUIL {Lopez.Cuil} FECHA INGRESO 01/09/2024",
                // De alguien que no está en la nómina, también de septiembre.
                ["otro.jpeg"] = "CUIL 20-12345678-6 PERIODO DE PAGO 09/2026",
                // Y el de septiembre de Ruiz, donde el CUIL no se lee: se sabe que es suyo por el nombre.
                ["foto.jpeg"] = "APELLIDO Y NOMBRE RUIZ, LUIS CUIL 2?-1??22333-9 PERIODO DE PAGO 09/2026",
            };
            foreach (var foto in textos.Keys) File.WriteAllBytes(Path.Combine(Firmados(cliente), foto), [0xFF, 0xD8]);
            LeerImagen leer = (ruta, _, _) => Task.FromResult(new Lectura(textos[Path.GetFileName(ruta)], Ocr: true));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, leer, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            // A Gómez le sigue faltando el de octubre: el de septiembre no es su firmado.
            Assert.Equal([Firma.Falta, Firma.Firmado, Firma.Firmado, Firma.Falta], r.Renglones.Select(x => x.Firma));
            Assert.Equal(["foto.jpeg", "gomez.jpeg", "otro.jpeg"], r.OtrosMeses.Select(o => Path.GetFileName(o.Hoja.Ruta)).Order());
            Assert.All(r.OtrosMeses, o => Assert.Equal(9, o.Mes));
            // De quién es cada uno: por el CUIL, por el nombre, o de nadie de la nómina.
            Assert.Equal([Gomez.Cuil], r.OtrosMeses.Single(o => o.Hoja.Ruta.EndsWith("gomez.jpeg")).Cuils);
            Assert.Equal([Ruiz.Cuil], r.OtrosMeses.Single(o => o.Hoja.Ruta.EndsWith("foto.jpeg")).Cuils);
            Assert.Empty(r.OtrosMeses.Single(o => o.Hoja.Ruta.EndsWith("otro.jpeg")).Cuils);
            // El de alguien que no está en la nómina no es una hoja «sin identificar»: se sabe qué es.
            Assert.Empty(r.Ilegibles);
            Assert.Empty(Clientes.Dificiles(r));
            // No se guarda como el firmado de Gómez, y la lista que queda en la carpeta lo dice.
            var plan = Clientes.Planear(r, new HashSet<string>());
            Assert.DoesNotContain(plan, e => e.Accion == Accion.Corregido && e.Empleado == Gomez);
            Assert.Contains(plan, e => e.Accion == Accion.Lista);
            Assert.Contains("gomez.jpeg · de septiembre", Clientes.Lista(r));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Theory]
    [InlineData(9, 10, 2026, 2026)]
    [InlineData(12, 1, 2027, 2026)] // diciembre, revisando enero: el del año anterior
    [InlineData(8, 7, 2026, 2026)] // el mes siguiente
    [InlineData(1, 12, 2026, 2027)]
    [InlineData(4, 10, 2026, 2026)] // a seis meses de los dos lados: el pasado
    public void El_recibo_de_otro_mes_es_del_anio_mas_cercano(int suMes, int mes, int anio, int esperado) =>
        Assert.Equal(esperado, Clientes.AnioDe(suMes, mes, anio));

    // Como Nomina y Recibo, pero de otro período.
    static (string, double, double)[] DelPeriodo(string periodo, (string, double, double)[] hoja) =>
        hoja.Select(t => t.Item1 == "10/2026" ? (periodo, t.Item2, t.Item3) : t).ToArray();

    [Fact]
    public async Task Lleva_los_recibos_de_otro_mes_a_su_carpeta_sin_pisar_nada()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            // Septiembre, con su nómina y sus recibos sin firmar: Vega se fue en septiembre, y no está en la de octubre.
            var vega = new Empleado("20-12345678-6", "VEGA IRENE");
            var septiembre = Path.Combine(Path.GetDirectoryName(Mes(cliente))!, "PEREZ JUAN SUELDOS SEPTIEMBRE 2026");
            Directory.CreateDirectory(Path.Combine(septiembre, "SUSS Y SIND"));
            Pdf(Path.Combine(septiembre, "SUSS Y SIND", "PEREZ JUAN SUSS NOMINA 09 2026.pdf"), DelPeriodo("09/2026", Nomina(Gomez, Rojas, vega)));
            Pdf(Path.Combine(septiembre, "PEREZ JUAN RECIBOS 09 2026.PDF"), DelPeriodo("09/2026", Recibo(Gomez)), DelPeriodo("09/2026", Recibo(vega)));
            // Con los firmados de octubre llegaron tres de septiembre: el de Gómez, el de Vega (se lee su nombre, no su CUIL) y
            // uno de nadie que se pueda reconocer.
            var textos = new Dictionary<string, string>
            {
                ["gomez sept.jpeg"] = $"CUIL {Gomez.Cuil} PERIODO DE PAGO 09/2026",
                ["vega.jpeg"] = "APELLIDO Y NOMBRE VEGA, IRENE CUIL 2?-1??45678-6 PERIODO DE PAGO 09/2026",
                ["borrosa.jpeg"] = "x / ; PERIODO DE PAGO 09/2026",
            };
            var firmados = Firmados(cliente);
            foreach (var foto in textos.Keys) File.WriteAllBytes(Path.Combine(firmados, foto), [0xFF, 0xD8]);
            LeerImagen leer = (ruta, _, _) => Task.FromResult(new Lectura(textos[Path.GetFileName(ruta)], Ocr: true));
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, leer, memoria, _ => { }, CancellationToken.None);

            // De quién es cada uno sale de la nómina de septiembre, y el nombre, de sus recibos sin firmar.
            Assert.Equal([vega.Cuil], r.OtrosMeses.Single(o => o.Hoja.Ruta.EndsWith("vega.jpeg")).Cuils);
            Assert.Empty(r.OtrosMeses.Single(o => o.Hoja.Ruta.EndsWith("borrosa.jpeg")).Cuils);
            var adonde = Path.Combine(septiembre, "RECIBOS FIRMADOS SEPTIEMBRE");
            Assert.Equal([Path.Combine(adonde, "GOMEZ LUCIA 09 2026.pdf"), Path.Combine(adonde, "VEGA IRENE 09 2026.pdf")], r.ALlevar.Select(l => l.Ruta));
            Assert.Equal([false, true], r.ALlevar.Select(l => l.PorNombre));
            Assert.Equal(3, r.DeOtroMesPendientes.Count());

            // Sin pedirlo no se lleva nada. Pidiéndolo: la carpeta de los firmados de septiembre, que no estaba, y después
            // los dos. El que no se sabe de quién es queda donde está.
            Assert.DoesNotContain(Clientes.Planear(r, new HashSet<string>()), e => e.Accion == Accion.ASuMes || e.Ruta.StartsWith(septiembre));
            var plan = Clientes.Planear(r, new HashSet<string>(), aSuMes: true).Where(e => e.Ruta.StartsWith(septiembre)).ToList();
            Assert.Equal([Accion.Carpeta, Accion.ASuMes, Accion.ASuMes], plan.Select(e => e.Accion));
            Corregir corregir = _ => Task.FromResult((Convert.FromBase64String(Jpeg), 2, 2));
            foreach (var e in plan) await Clientes.Escribir(e, r, corregir);

            // Llegaron con el período y el CUIL en las palabras clave: la revisión de septiembre los cuenta sin OCR, y el de
            // Vega sigue para revisar, porque se la encontró por el nombre.
            using (var doc = PdfDocument.Open(Path.Combine(adonde, "GOMEZ LUCIA 09 2026.pdf"))) Assert.Equal($"{Gomez.Cuil} 09/2026", doc.Information.Keywords);
            LeerImagen nadie = (_, _, _) => throw new InvalidOperationException("no debería leer nada");
            var deSeptiembre = await Clientes.Revisar(cliente, 9, 2026, null, nadie, new Memoria(Path.Combine(raiz, "septiembre.json")), _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.Falta, Firma.PorNombre], deSeptiembre.Renglones.Select(x => x.Firma));

            // En octubre ya no hay nada para llevar: solo queda el que no se sabe de quién es. Los originales siguen ahí.
            r = await Clientes.Revisar(cliente, 10, 2026, null, leer, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(["borrosa.jpeg"], r.DeOtroMesPendientes.Select(o => Path.GetFileName(o.Hoja.Ruta)));
            Assert.DoesNotContain(Clientes.Planear(r, new HashSet<string>(), aSuMes: true), e => e.Accion == Accion.ASuMes);
            Assert.All(textos.Keys, foto => Assert.True(File.Exists(Path.Combine(firmados, foto))));
            Assert.DoesNotContain("gomez sept.jpeg", Clientes.Lista(r));
            Assert.Contains("borrosa.jpeg · de septiembre", Clientes.Lista(r));
            // Y lo que ya está no se pisa: escribirlo otra vez falla, y el archivo queda como estaba.
            var antes = File.ReadAllBytes(plan[1].Ruta);
            await Assert.ThrowsAsync<IOException>(() => Clientes.Escribir(plan[1], r, corregir));
            Assert.Equal(antes, File.ReadAllBytes(plan[1].Ruta));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Sin_la_carpeta_de_su_mes_no_se_lleva()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            File.WriteAllBytes(Path.Combine(Firmados(cliente), "gomez.jpeg"), [0xFF, 0xD8]);
            LeerImagen leer = (_, _, _) => Task.FromResult(new Lectura($"CUIL {Gomez.Cuil} PERIODO DE PAGO 09/2026", Ocr: true));
            var r = await Clientes.Revisar(cliente, 10, 2026, null, leer, new Memoria(Path.Combine(raiz, "memoria.json")), _ => { }, CancellationToken.None);
            // Se sabe de quién es (por la nómina de octubre) y cómo se llamaría, pero no hay adónde llevarlo.
            var l = Assert.Single(r.ALlevar);
            Assert.Equal(("GOMEZ LUCIA 09 2026.pdf", (string?)null), (l.Archivo, l.Ruta));
            Assert.DoesNotContain(Clientes.Planear(r, new HashSet<string>(), aSuMes: true), e => e.Accion is Accion.ASuMes or Accion.Carpeta);
            Assert.Single(r.DeOtroMesPendientes);
        }
        finally { Directory.Delete(raiz, true); }
    }

    // ---- La carpeta de descargas ----

    // Otro cliente, con su carpeta de octubre, su nómina y sus recibos sin firmar.
    static string OtroCliente(string raiz, string nombre, params Empleado[] empleados)
    {
        var mes = Path.Combine(raiz, nombre, "4. SUELDOS Y CARGAS SOCIALES", "1  LIQUIDACIONES DE SUELDOS 2026", $"{nombre} SUELDOS OCTUBRE 2026");
        Directory.CreateDirectory(Path.Combine(mes, "SUSS Y SIND"));
        Pdf(Path.Combine(mes, "SUSS Y SIND", $"{nombre} SUSS NOMINA 10 2026.pdf"), Nomina(empleados));
        Pdf(Path.Combine(mes, $"{nombre} RECIBOS 10 2026.PDF"), empleados.Select(Recibo).ToArray());
        return Path.Combine(raiz, nombre);
    }

    [Theory]
    [InlineData(10, 2026)]
    [InlineData(11, 2026)] // el del mes que corre
    [InlineData(12, 2025)] // diciembre, bajado en noviembre: el del año pasado
    public void El_recibo_bajado_es_del_ultimo_mes_que_paso(int mes, int anio) => Assert.Equal(anio, Descargas.AnioDe(mes, new DateTime(2026, 11, 15)));

    [Fact]
    public async Task Acomoda_las_descargas_en_su_cliente_y_su_mes_sin_pisar_ni_borrar_nada()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clientes = Directory.CreateDirectory(Path.Combine(raiz, "CLIENTES 2026")).FullName;
            var perez = Cliente(clientes, "PEREZ JUAN", conFirmados: false);
            var vega = new Empleado("20-12345678-6", "VEGA IRENE");
            var sosa = OtroCliente(clientes, "SOSA ELENA", vega);
            // La carpeta de descargas, con lo que se bajó como vino: la foto de Gómez dos veces, la de Vega (de otro cliente),
            // una que no es de nadie, una sin el mes a la vista y una constancia.
            var descargas = Directory.CreateDirectory(Path.Combine(raiz, "Descargas")).FullName;
            var textos = new Dictionary<string, string>
            {
                ["foto gomez.jpeg"] = $"CUIL {Gomez.Cuil} PERIODO DE PAGO 10/2026",
                ["foto gomez (1).jpeg"] = $"CUIL {Gomez.Cuil} PERIODO DE PAGO 10/2026",
                ["vega.jpeg"] = $"CUIL {vega.Cuil} PERIODO DE PAGO 10/2026",
                ["nadie.jpeg"] = "x / ; PERIODO DE PAGO 10/2026",
                ["sin mes.jpeg"] = $"CUIL {Rojas.Cuil}",
                ["constancia.jpeg"] = $"CONSTANCIA DEL TRABAJADOR Alta CUIL: {Lopez.Cuil}",
            };
            foreach (var foto in textos.Keys) File.WriteAllBytes(Path.Combine(descargas, foto), [0xFF, 0xD8]);
            File.WriteAllText(Path.Combine(descargas, "notas.txt"), "no es un recibo");
            var lecturas = 0;
            LeerImagen leer = (ruta, _, _) =>
            {
                Interlocked.Increment(ref lecturas);
                return Task.FromResult(new Lectura(textos[Path.GetFileName(ruta)], Ocr: true));
            };
            // La foto bajada dos veces es la misma imagen.
            SacarHuella huella = (ruta, _) => Task.FromResult<string?>(Path.GetFileName(ruta).StartsWith("foto gomez") ? "gomez" : Path.GetFileName(ruta));
            var hoy = new DateTime(2026, 11, 15);
            string? RaizDelAnio(int anio) => anio == 2026 ? clientes : null;
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            var b = await Descargas.Revisar(descargas, RaizDelAnio, hoy, null, leer, memoria, _ => { }, CancellationToken.None, huella);

            // Seis fotos y cinco imágenes distintas: cinco lecturas. Cada recibo, a su cliente; lo demás, sin ubicar y por qué.
            Assert.Equal(5, lecturas);
            Assert.Equal([("PEREZ JUAN", "GOMEZ LUCIA 10 2026.pdf"), ("SOSA ELENA", "VEGA IRENE 10 2026.pdf")],
                b.Acomodos.Select(a => (Path.GetFileName(a.Cliente), a.Recibo.Archivo)));
            Assert.Single(b.Acomodos[0].Recibo.Hojas); // la foto repetida, una vez
            Assert.Equal(["constancia.jpeg: es una constancia de ARCA, no un recibo", "nadie.jpeg: no encontré su CUIL en las nóminas de octubre",
                "sin mes.jpeg: no dice de qué mes es"], b.SinUbicar.Select(u => $"{Path.GetFileName(u.Hoja.Ruta)}: {u.PorQue}"));

            // Lo de un solo cliente: su carpeta de firmados, su recibo y sus dos originales.
            var dePerez = Descargas.Planear(b, perez);
            Assert.Equal([Accion.Carpeta, Accion.ASuMes, Accion.Acomodar, Accion.Acomodar], dePerez.Select(e => e.Accion));
            // Todo: los dos clientes, y los tres originales que ya tienen adónde ir. Los que no, quedan en la carpeta.
            var plan = Descargas.Planear(b);
            Assert.Equal(["foto gomez (1).jpeg", "foto gomez.jpeg", "vega.jpeg"], plan.Where(e => e.Accion == Accion.Acomodar).Select(e => Path.GetFileName(e.Ruta)));
            Corregir corregir = _ => Task.FromResult((Convert.FromBase64String(Jpeg), 2, 2));
            foreach (var e in plan) await Descargas.Escribir(e, b, corregir);

            // Cada recibo quedó en los firmados de su cliente y su mes, con el CUIL y el período para la revisión de Clientes.
            var gomezEnZ = Path.Combine(Mes(perez), "RECIBOS FIRMADOS OCTUBRE", "GOMEZ LUCIA 10 2026.pdf");
            using (var doc = PdfDocument.Open(gomezEnZ)) Assert.Equal($"{Gomez.Cuil} 10/2026", doc.Information.Keywords);
            Assert.True(File.Exists(Path.Combine(Mes(sosa), "RECIBOS FIRMADOS OCTUBRE", "VEGA IRENE 10 2026.pdf")));
            LeerImagen nadieLee = (_, _, _) => throw new InvalidOperationException("no debería leer nada");
            var octubre = await Clientes.Revisar(perez, 10, 2026, null, nadieLee, new Memoria(Path.Combine(raiz, "octubre.json")), _ => { }, CancellationToken.None);
            Assert.Equal(Firma.Firmado, octubre.Renglones[0].Firma);
            // Los originales están en «Acomodados»; lo que no se pudo ubicar sigue en la carpeta, como estaba.
            var acomodados = Path.Combine(descargas, Descargas.Acomodados);
            Assert.Equal(["foto gomez (1).jpeg", "foto gomez.jpeg", "vega.jpeg"], Directory.GetFiles(acomodados).Select(Path.GetFileName).Order());
            Assert.Equal(["constancia.jpeg", "nadie.jpeg", "notas.txt", "sin mes.jpeg"], Directory.GetFiles(descargas).Select(Path.GetFileName).Order());

            // Revisada de nuevo, ya no queda nada para guardar, y lo leído sale de la memoria.
            b = await Descargas.Revisar(descargas, RaizDelAnio, hoy, null, leer, memoria, _ => { }, CancellationToken.None, huella);
            Assert.Empty(b.Acomodos);
            Assert.Equal(3, b.SinUbicar.Count);
            Assert.Empty(Descargas.Planear(b));
            Assert.Equal(5, lecturas);

            // La misma foto, bajada otra vez con el mismo nombre: su recibo ya está en Z, así que no se escribe nada, y el
            // original va a «Acomodados» con otro nombre, sin pisar el que ya estaba.
            File.WriteAllBytes(Path.Combine(descargas, "foto gomez.jpeg"), [0xFF, 0xD8]);
            b = await Descargas.Revisar(descargas, RaizDelAnio, hoy, null, leer, memoria, _ => { }, CancellationToken.None, huella);
            Assert.True(Assert.Single(b.Acomodos).Recibo.EnSuCarpeta);
            plan = Descargas.Planear(b);
            Assert.Equal([Accion.Acomodar], plan.Select(e => e.Accion));
            var antes = File.ReadAllBytes(gomezEnZ);
            foreach (var e in plan) await Descargas.Escribir(e, b, corregir);
            Assert.Equal(antes, File.ReadAllBytes(gomezEnZ));
            Assert.Contains("foto gomez (2).jpeg", Directory.GetFiles(acomodados).Select(Path.GetFileName));
            Assert.Equal(5, lecturas);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task La_misma_foto_ya_guardada_en_el_cliente_no_se_guarda_otra_vez()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clientes = Directory.CreateDirectory(Path.Combine(raiz, "CLIENTES 2026")).FullName;
            var perez = Cliente(clientes, "PEREZ JUAN", conFirmados: false);
            // La foto de Rojas ya está en los firmados con su nombre de WhatsApp, y se volvió a bajar con un nombre cualquiera.
            byte[] foto = [0xFF, 0xD8, 0x01, 0x02];
            File.WriteAllBytes(Path.Combine(Firmados(perez), "WhatsApp Image 2026-11-18 at 10.00.00.jpeg"), foto);
            var descargas = Directory.CreateDirectory(Path.Combine(raiz, "Descargas")).FullName;
            File.WriteAllBytes(Path.Combine(descargas, "WhatsAppDownload.jpeg"), foto);
            // Y otra foto de Gómez, distinta, que no está en ningún lado.
            File.WriteAllBytes(Path.Combine(descargas, "gomez.jpeg"), [0xFF, 0xD8, 0x03]);
            LeerImagen leer = (ruta, _, _) => Task.FromResult(new Lectura(
                $"CUIL {(ruta.EndsWith("gomez.jpeg") ? Gomez : Rojas).Cuil} PERIODO DE PAGO 10/2026", Ocr: true));
            var b = await Descargas.Revisar(descargas, _ => clientes, new DateTime(2026, 11, 15), null, leer, new Memoria(Path.Combine(raiz, "m.json")), _ => { }, CancellationToken.None);
            // La de Rojas ya estaba: no se escribe nada, y el original pasa a «Acomodados». La de Gómez se guarda.
            Assert.Equal([("GOMEZ LUCIA 10 2026.pdf", false), ("ROJAS PABLO 10 2026.pdf", true)], b.Acomodos.Select(a => (a.Recibo.Archivo, a.Recibo.EnSuCarpeta)));
            Assert.Equal([(Accion.ASuMes, "GOMEZ LUCIA 10 2026.pdf"), (Accion.Acomodar, "WhatsAppDownload.jpeg"), (Accion.Acomodar, "gomez.jpeg")],
                Descargas.Planear(b).Select(e => (e.Accion, Path.GetFileName(e.Ruta))).OrderBy(t => t.Item1).ThenBy(t => t.Item2, StringComparer.Ordinal));
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task Un_original_con_una_hoja_sin_ubicar_queda_en_la_carpeta()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clientes = Directory.CreateDirectory(Path.Combine(raiz, "CLIENTES 2026")).FullName;
            var perez = Cliente(clientes, "PEREZ JUAN", conFirmados: false);
            // Gómez está también en la nómina de otro cliente, que trabaja para los dos.
            var sosa = OtroCliente(clientes, "SOSA ELENA", Gomez);
            var descargas = Directory.CreateDirectory(Path.Combine(raiz, "Descargas")).FullName;
            // Un escaneo de tres hojas: la de Rojas, la de Gómez con el nombre de SOSA ELENA en el encabezado (decide el
            // cliente), y otra de Gómez sin nombre de cliente (no se sabe de cuál de los dos es).
            Pdf(Path.Combine(descargas, "escaneo.pdf"), HojasSinTexto(3));
            var textos = new[] { $"CUIL {Rojas.Cuil} PERIODO DE PAGO 10/2026", $"SOSA ELENA CUIL {Gomez.Cuil} PERIODO DE PAGO 10/2026", $"CUIL {Gomez.Cuil} PERIODO DE PAGO 10/2026" };
            LeerImagen leer = (_, hoja, _) => Task.FromResult(new Lectura(textos[hoja - 1], Ocr: true));
            var b = await Descargas.Revisar(descargas, anio => clientes, new DateTime(2026, 11, 15), null, leer, new Memoria(Path.Combine(raiz, "m.json")), _ => { }, CancellationToken.None);
            Assert.Equal([("PEREZ JUAN", "ROJAS PABLO 10 2026.pdf"), ("SOSA ELENA", "GOMEZ LUCIA 10 2026.pdf")], b.Acomodos.Select(a => (Path.GetFileName(a.Cliente), a.Recibo.Archivo)));
            Assert.Equal("su CUIL está en la nómina de más de un cliente", Assert.Single(b.SinUbicar).PorQue);
            // Los dos recibos se guardan; el escaneo no pasa a «Acomodados», porque una hoja suya no tiene adónde ir.
            var plan = Descargas.Planear(b);
            Assert.Equal(2, plan.Count(e => e.Accion == Accion.ASuMes));
            Assert.DoesNotContain(plan, e => e.Accion == Accion.Acomodar);
            Assert.Empty(b.Completos);
        }
        finally { Directory.Delete(raiz, true); }
    }

    [Fact]
    public async Task El_periodo_que_leyo_el_escaner_lo_confirma_la_lectura_a_fondo()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cliente = Cliente(raiz, "PEREZ JUAN", conFirmados: false);
            // Un escaneo con el texto del escáner, que leyó mal el período: dice septiembre y es el de octubre.
            Escaneo(Path.Combine(Firmados(cliente), "ESCANEO.pdf"), $"FIRMA DEL EMPLEADO CUIL {Gomez.Cuil} PERIODO DE PAGO 09/2026");
            var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
            LeerImagen nadie = (_, _, _) => throw new InvalidOperationException("tiene texto: la lectura rápida no la toca");
            var r = await Clientes.Revisar(cliente, 10, 2026, null, nadie, memoria, _ => { }, CancellationToken.None);
            // Por ahora figura de otro mes, pero queda por confirmar: va a la lectura a fondo.
            Assert.True(Assert.Single(r.OtrosMeses).PorConfirmar);
            Assert.Single(Clientes.Dificiles(r));
            LeerImagen fondo = (_, _, _) => Task.FromResult(new Lectura($"CUIL {Gomez.Cuil} PERIODO DE PAGO 10/2026", Ocr: true));
            await Clientes.Profundizar(r, fondo, memoria, _ => { }, CancellationToken.None);
            // La app leyó octubre: es su firmado.
            r = await Clientes.Revisar(cliente, 10, 2026, null, nadie, memoria, _ => { }, CancellationToken.None);
            Assert.Empty(r.OtrosMeses);
            Assert.Equal(Firma.Firmado, r.Renglones[0].Firma);
            Assert.Empty(Clientes.Dificiles(r));
        }
        finally { Directory.Delete(raiz, true); }
    }

    // Con el OCR de la app sobre clientes reales: si junto al proyecto hay una carpeta «prueba-clientes» (copia de
    // algunos clientes, que no va al git), se revisa entera y se cuenta qué se encontró. Sin ella, pasa vacía.
    [Fact]
    public async Task Revisa_clientes_reales()
    {
        var raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../prueba-clientes"));
        if (!Directory.Exists(raiz)) return;
        var ocr = WindowsOcr.Crear();
        var lectores = Lector.Crear();
        Assert.NotNull(lectores);
        var (rapido, aFondo) = lectores.Value;
        var memoria = new Memoria(Path.Combine(Path.GetTempPath(), "prueba-clientes-memoria-v6.json"));
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var revisiones = new List<Revision>();
        foreach (var cliente in Clientes.ConSueldos(raiz))
            revisiones.Add(await Clientes.Revisar(cliente, 10, 2026, ocr, rapido, memoria, _ => { }, CancellationToken.None, WindowsOcr.Huella));
        salida.WriteLine($"primera pasada: {reloj.Elapsed.TotalSeconds:0} s");
        reloj.Restart();
        var dificiles = 0;
        foreach (var r in revisiones) dificiles += await Clientes.Profundizar(r, aFondo, memoria, _ => { }, CancellationToken.None, WindowsOcr.Huella);
        salida.WriteLine($"segunda pasada: {dificiles} hojas difíciles en {reloj.Elapsed.TotalSeconds:0} s");
        memoria.Guardar();
        foreach (var cliente in Clientes.ConSueldos(raiz))
        {
            var r = await Clientes.Revisar(cliente, 10, 2026, ocr, rapido, memoria, _ => { }, CancellationToken.None, WindowsOcr.Huella);
            var fotos = r.Renglones.SelectMany(x => x.Firmado).Count(h => h.Lectura.Ocr);
            var plan = Clientes.Planear(r, new HashSet<string>());
            salida.WriteLine($"{r.Cliente}: nómina {r.Nomina?.Empleados.Count}, firmados {(r.Firmados is null ? "-" : Path.GetFileName(r.Firmados))}, " +
                $"faltan {r.Faltan}, por nombre {r.Renglones.Count(x => x.Firma == Firma.PorNombre)}, de otro mes {r.OtrosMeses.Count}, hojas leídas con OCR {fotos}, " +
                $"giros {string.Join(",", r.Renglones.SelectMany(x => x.Firmado).Where(h => h.Lectura.Ocr).Select(h => h.Lectura.Giro))}, " +
                $"sin leer {r.Ilegibles.Count} ({string.Join(", ", r.Ilegibles.GroupBy(h => Path.GetExtension(h.Ruta).ToLowerInvariant() + (h.Hoja > 0 ? " escaneado" : " foto")).Select(g => $"{g.Count()} {g.Key}"))}), " +
                $"guardaría {plan.Count(e => e.Accion == Accion.Corregido)} corregidos y {plan.FirstOrDefault(e => e.Accion == Accion.Consolidado)?.Hojas.Count ?? 0} hojas " +
                $"del consolidado, avisos {string.Join(" | ", r.Avisos)}");
        }

    }

    // Las muestras de problemas reales: si al lado del proyecto está la carpeta «muestras-recibos» (copias de los recibos que
    // trajeron un problema; no van al git porque son personas reales), cada caso de su casos.json se revisa como lo haría la
    // app y se compara con lo que tiene que dar. Sin la carpeta, como en GitHub, pasa vacía. Lo leído con OCR queda en su
    // memoria.json; para releer todo con el OCR, se borra.
    [Fact]
    public async Task Revisa_las_muestras_de_problemas_reales()
    {
        var raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../muestras-recibos"));
        if (!File.Exists(Path.Combine(raiz, "casos.json"))) return;
        var casos = JsonSerializer.Deserialize<List<Muestra>>(File.ReadAllText(Path.Combine(raiz, "casos.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var ocr = WindowsOcr.Crear();
        var lectores = Lector.Crear();
        Assert.NotNull(lectores);
        var (rapido, aFondo) = lectores.Value;
        var memoria = new Memoria(Path.Combine(raiz, "memoria.json"));
        var fallas = new List<string>();
        void Comparar(string caso, string que, int? espera, int hay)
        {
            salida.WriteLine($"{caso}: {que} {hay}{(espera is { } n && n != hay ? $", TENDRÍA QUE SER {n}" : "")}");
            if (espera is { } e && e != hay) fallas.Add($"{caso}: {que} {hay}, tendría que ser {e}");
        }
        foreach (var c in casos)
        {
            if (c.Clientes is { } esperado)
            {
                var cliente = Path.Combine(raiz, c.Cliente);
                var r = await Clientes.Revisar(cliente, c.Mes, c.Anio, ocr, rapido, memoria, _ => { }, CancellationToken.None, WindowsOcr.Huella);
                if (await Clientes.Profundizar(r, aFondo, memoria, _ => { }, CancellationToken.None, WindowsOcr.Huella) > 0)
                    r = await Clientes.Revisar(cliente, c.Mes, c.Anio, ocr, rapido, memoria, _ => { }, CancellationToken.None, WindowsOcr.Huella);
                var plan = Clientes.Planear(r, new HashSet<string>());
                Comparar(c.Caso, "faltan", esperado.Faltan, r.Faltan);
                Comparar(c.Caso, "por nombre", esperado.PorNombre, r.Renglones.Count(x => x.Firma == Firma.PorNombre));
                Comparar(c.Caso, "corregidos", esperado.Corregidos, plan.Count(p => p.Accion == Accion.Corregido));
                Comparar(c.Caso, "hojas del consolidado", esperado.HojasDelConsolidado,
                    plan.FirstOrDefault(p => p.Accion == Accion.Consolidado)?.Hojas.Count ?? 0);
                // El último recurso, con las iniciales del estudio para todos los que no tienen el firmado.
                Comparar(c.Caso, "con iniciales", esperado.Iniciales,
                    Clientes.Planear(r, r.Renglones.Select(x => x.Empleado.Cuil).ToHashSet()).Count(p => p.Accion == Accion.Iniciales));
                Comparar(c.Caso, "archivos de otro mes", esperado.DeOtroMes, r.OtrosMeses.Select(o => o.Hoja.Ruta).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Comparar(c.Caso, "para llevar a su mes", esperado.ASuMes,
                    Clientes.Planear(r, new HashSet<string>(), aSuMes: true).Count(p => p.Accion == Accion.ASuMes));
            }
            if (c.Separar is { } separar)
            {
                // Lo separado va a una carpeta temporal: en las muestras no se escribe nada.
                var tmp = Directory.CreateTempSubdirectory().FullName;
                try
                {
                    var r = await Recibos.Procesar([Path.Combine(raiz, separar.Pdf)], ocr, _ => { }, tmp, iniciales: false);
                    Comparar(c.Caso, "separados", separar.Recibos, r.Creados.Count);
                    var revision = r.Nominas.Count == 1 ? Recibos.Revisar(r.Nominas[0], Directory.GetDirectories(tmp)) : [];
                    Comparar(c.Caso, "separados por el nombre", separar.PorNombre, revision.Count(x => x.PorNombre));
                    Comparar(c.Caso, "faltan al separar", separar.Faltan, r.Nominas.Count == 1 ? revision.Count(x => x.Recibo is null) : -1);
                }
                finally { Directory.Delete(tmp, true); }
            }
        }
        memoria.Guardar();
        Assert.True(fallas.Count == 0, string.Join("\n", fallas));
    }

    record Muestra(string Caso, string Cliente, int Mes, int Anio, EnClientes? Clientes, EnSeparar? Separar);
    record EnClientes(int? Faltan, int? PorNombre, int? Corregidos, int? HojasDelConsolidado, int? Iniciales, int? DeOtroMes, int? ASuMes);
    record EnSeparar(string Pdf, int? Recibos, int? PorNombre, int? Faltan);
}
