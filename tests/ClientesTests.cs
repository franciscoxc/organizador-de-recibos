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

    // La carpeta de firmados de un cliente armado como los del estudio, sin nada adentro.
    static string Firmados(string cliente) => Directory.CreateDirectory(Path.Combine(cliente, "4. SUELDOS Y CARGAS SOCIALES",
        "1  LIQUIDACIONES DE SUELDOS 2026", $"{Path.GetFileName(cliente)} SUELDOS OCTUBRE 2026", "RECIBOS FIRMADOS OCTUBRE")).FullName;

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

            // La segunda revisión sale de la memoria: un OCR que falla no se llega a usar.
            LeerImagen roto = (_, _, _) => throw new InvalidOperationException("no debería leer de nuevo");
            var otra = await Clientes.Revisar(cliente, 10, 2026, null, roto, memoria, _ => { }, CancellationToken.None);
            Assert.Equal(r.Renglones.Select(x => x.Firma), otra.Renglones.Select(x => x.Firma));

            // Un archivo que ya existe con el nombre de destino no se pisa: Rojas ya tiene el suyo.
            var firmados = r.Firmados!;
            File.WriteAllText(Path.Combine(firmados, "ROJAS PABLO 10 2026.pdf"), "de otro");
            var plan = Clientes.Planear(r, new HashSet<string> { Lopez.Cuil, Ruiz.Cuil });
            Assert.All(plan, e => Assert.False(File.Exists(e.Ruta)));
            Assert.Equal(
            [
                (Accion.Corregido, "GOMEZ LUCIA 10 2026.pdf"),
                (Accion.Iniciales, "LOPEZ ANA 10 2026.pdf"), // Ruiz no tiene recibo sin firmar: no hay sobre qué estampar
                (Accion.Lista, "FALTAN RECIBOS FIRMADOS 10 2026.txt"),
            ], plan.Select(e => (e.Accion, Path.GetFileName(e.Ruta))));
            // Todo va a los firmados: el PDF sin firmar de todos juntos es el que el estudio le manda al cliente, y no se
            // separa ni se toca.
            Assert.All(plan, e => Assert.Equal(firmados, Path.GetDirectoryName(e.Ruta)));

            // Una foto «corregida» de mentira: lo que importa acá es que el PDF se arme con ella.
            var jpeg = await WindowsOcr.Jpeg(Enumerable.Repeat((byte)200, 8 * 6 * 4).ToArray(), 8, 6);
            Corregir corregir = h => Task.FromResult((jpeg, 8, 6));
            foreach (var e in plan.Where(e => e.Accion != Accion.Lista)) await Clientes.Escribir(e, r, corregir);
            Assert.Equal("de otro", File.ReadAllText(Path.Combine(firmados, "ROJAS PABLO 10 2026.pdf")));

            using (var d = PdfDocument.Open(plan[1].Ruta))
            {
                Assert.Contains("iniciales", d.Information.Keywords);
                Assert.Contains("L.A.", string.Join(" ", d.GetPage(1).GetWords().Select(w => w.Text)));
            }
            using (var d = PdfDocument.Open(plan[0].Ruta)) Assert.Equal(1, d.NumberOfPages);

            // Si mientras tanto apareció el archivo, falla en vez de pisarlo.
            await Assert.ThrowsAsync<IOException>(() => Clientes.Escribir(plan[1], r, corregir));

            // La lista se escribe con el cliente revisado de nuevo: López ya tiene las iniciales del estudio.
            var nueva = await Clientes.Revisar(cliente, 10, 2026, null, Leer, memoria, _ => { }, CancellationToken.None);
            Assert.Equal([Firma.Firmado, Firma.PorNombre, Firma.ConIniciales, Firma.Falta], nueva.Renglones.Select(x => x.Firma));
            await Clientes.Escribir(plan[^1], nueva, corregir);
            var lista = File.ReadAllText(plan[^1].Ruta);
            salida.WriteLine(lista);
            Assert.Contains("Falta 1:", lista);
            Assert.Contains(Ruiz.Cuil, lista);
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
            Assert.Contains(r.Ilegibles, h => Path.GetFileName(h.Ruta) == "BAJA LOPEZ ANA.jpeg");
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
        }
        finally
        {
            Directory.Delete(raiz, true);
        }
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
            revisiones.Add(await Clientes.Revisar(cliente, 10, 2026, ocr, rapido, memoria, _ => { }, CancellationToken.None));
        salida.WriteLine($"primera pasada: {reloj.Elapsed.TotalSeconds:0} s");
        reloj.Restart();
        var dificiles = 0;
        foreach (var r in revisiones) dificiles += await Clientes.Profundizar(r, aFondo, memoria, _ => { }, CancellationToken.None);
        salida.WriteLine($"segunda pasada: {dificiles} hojas difíciles en {reloj.Elapsed.TotalSeconds:0} s");
        memoria.Guardar();
        foreach (var cliente in Clientes.ConSueldos(raiz))
        {
            var r = await Clientes.Revisar(cliente, 10, 2026, ocr, rapido, memoria, _ => { }, CancellationToken.None);
            var fotos = r.Renglones.SelectMany(x => x.Firmado).Count(h => h.Lectura.Ocr);
            salida.WriteLine($"{r.Cliente}: nómina {r.Nomina?.Empleados.Count}, firmados {(r.Firmados is null ? "-" : Path.GetFileName(r.Firmados))}, " +
                $"faltan {r.Faltan}, por nombre {r.Renglones.Count(x => x.Firma == Firma.PorNombre)}, hojas leídas con OCR {fotos}, " +
                $"giros {string.Join(",", r.Renglones.SelectMany(x => x.Firmado).Where(h => h.Lectura.Ocr).Select(h => h.Lectura.Giro))}, " +
                $"sin leer {r.Ilegibles.Count} ({string.Join(", ", r.Ilegibles.GroupBy(h => Path.GetExtension(h.Ruta).ToLowerInvariant() + (h.Hoja > 0 ? " escaneado" : " foto")).Select(g => $"{g.Count()} {g.Key}"))}), avisos {string.Join(" | ", r.Avisos)}");
        }

    }
}
