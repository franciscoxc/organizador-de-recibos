using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

// Nombres y CUIL inventados (con dígito verificador válido): el repo es público y los recibos reales
// tienen datos personales.
public class RecibosTests
{
    const string Empleador = "30-71234567-1";

    [Fact]
    public void Iniciales_apellidos_y_despues_nombres()
    {
        Assert.Equal("P.J.C.", Recibos.Iniciales("PEREZ, JUAN CARLOS"));
        Assert.Equal("D.L.F.A.", Recibos.Iniciales("DE LA FUENTE, ANA"));
    }

    [Fact]
    public void Nombre_de_archivo_con_mes_de_dos_digitos()
    {
        Assert.Equal("GOMEZ LUCIA 07 2026.pdf", Recibos.NombreArchivo("GOMEZ, LUCIA", 7, "2026"));
    }

    [Fact]
    public void Cuil_por_digito_verificador()
    {
        Assert.True(Recibos.CuilValido("20-12345678-6"));
        Assert.False(Recibos.CuilValido("20-12345678-5"));
        // El OCR a veces deja espacios junto a los guiones; el mal leído se descarta.
        Assert.Equal(["20-12345678-6"], Recibos.Cuils("CUIL20 -12345678- 6 y 20-12345678-5"));
    }

    static Palabra P(string texto, double izq, double der, double y) => new(texto, izq, y - 4, der, y + 4);

    // Imita la nómina de AFIP: encabezado con el CUIT del empleador, una columna por empleado con su CUIL
    // y el nombre en renglones, y la columna «Suma Totales» al final.
    [Fact]
    public void Lee_la_nomina_por_columnas()
    {
        Palabra[] ws =
        [
            P("CUIT:", 40, 60, 800), P(Empleador, 100, 160, 800), P("Período:", 300, 330, 800), P("07/2026", 335, 365, 800),
            P("Contribuyente:", 40, 90, 788),
            P("CUIL", 60, 80, 760), P("20-11222333-9", 100, 160, 760), P("27-44555666-7", 200, 260, 760),
            P("Suma", 300, 320, 760), P("Totales", 322, 350, 760),
            // El OCR pega el rótulo «Apellido y Nombre» al primer nombre.
            P("Apellido", 20, 50, 748), P("y", 52, 55, 748), P("NombrePEREZ", 57, 120, 748), P("JUAN", 122, 140, 748),
            P("ANA", 200, 215, 748), P("MARIA", 217, 240, 748), P("Suma", 300, 320, 748),
            P("CARLOS", 100, 130, 738), P("DE", 200, 210, 738), P("LA", 212, 220, 738), P("FUENTE", 222, 250, 738),
            P("Obra", 30, 50, 725), P("Social", 52, 80, 725), P("126205", 100, 130, 725),
        ];
        var avisos = new List<string>();
        var (mes, anio, empleados) = Recibos.LeerNomina(ws, avisos.Add);
        Assert.Equal((7, "2026"), (mes, anio));
        Assert.Equal([new("20-11222333-9", "PEREZ JUAN CARLOS"), new Empleado("27-44555666-7", "ANA MARIA DE LA FUENTE")], empleados);
        Assert.Empty(avisos);
    }

    static void Recibo(string ruta, string texto, string claves = "")
    {
        var b = new PdfDocumentBuilder();
        b.DocumentInformation.Keywords = claves;
        var fuente = b.AddStandard14Font(Standard14Font.Helvetica);
        var p = b.AddPage(PageSize.A4);
        if (texto.Length > 0) p.AddText(texto, 10, new PdfPoint(50, 700), fuente);
        File.WriteAllBytes(ruta, b.Build());
    }

    [Fact]
    public void Revisa_la_nomina_contra_los_recibos_de_las_carpetas()
    {
        var carpeta = Directory.CreateTempSubdirectory().FullName;
        Recibo(Path.Combine(carpeta, "PEREZ JUAN CARLOS 07 2026.pdf"), $"CUIL 20-12345678-6 CUIT {Empleador}");
        // Escaneado: sin texto, el CUIL está solo en las palabras clave.
        Recibo(Path.Combine(carpeta, "GOMEZ LUCIA 07 2026.pdf"), "", $"27-23456789-1 {Empleador}");
        // Con otro nombre de archivo: cuenta por el período que dice adentro.
        Recibo(Path.Combine(carpeta, "recibo rojas.pdf"), $"CUIL 20-45678901-4 CUIT {Empleador} PERIODO DE PAGO 07/2026");
        // El de junio, pagado en julio: la fecha de pago no lo vuelve de julio.
        Recibo(Path.Combine(carpeta, "recibo lopez.pdf"), $"CUIL 27-34567890-0 CUIT {Empleador} PERIODO 06/2026 PAGO 05/07/2026");
        // El PDF de todos juntos no prueba que el recibo de cada uno se haya separado.
        Recibo(Path.Combine(carpeta, "TODOS 07 2026.pdf"), $"20-12345678-6 27-34567890-0 20-11222333-9 {Empleador}");

        var n = new Nomina("nomina.pdf", 7, "2026",
        [
            new("20-12345678-6", "JUAN CARLOS PEREZ"), new("27-23456789-1", "LUCIA GOMEZ"), new("20-45678901-4", "PABLO ROJAS"),
            new("27-34567890-0", "ANA LOPEZ"), new("20-11222333-9", "LUIS RUIZ"),
        ]);
        var revision = Recibos.Revisar(n, [carpeta, carpeta]);
        Assert.Equal(["ANA LOPEZ", "LUIS RUIZ"], revision.Where(r => r.Recibo is null).Select(r => r.Empleado.Nombre));
        Assert.EndsWith("PEREZ JUAN CARLOS 07 2026.pdf", revision[0].Recibo);
        Assert.EndsWith("recibo rojas.pdf", revision[2].Recibo);
        Directory.Delete(carpeta, true);
    }

    const string Release = """
        {"tag_name": "v1.0.3", "assets": [
          {"name": "Organizador.de.Recibos.arm.exe", "size": 10, "digest": "sha256:aa", "browser_download_url": "https://x/arm.exe"},
          {"name": "Organizador.de.Recibos.x64.exe", "size": 20, "digest": "sha256:bb", "browser_download_url": "https://x/x64.exe"}]}
        """;

    [Fact]
    public void Actualizacion_elige_el_exe_de_la_pc_solo_si_es_mas_nueva()
    {
        Assert.Equal("https://x/arm.exe", Actualizador.Elegir(Release, new Version(1, 0, 2), Architecture.Arm64)!.Url);
        Assert.Equal("bb", Actualizador.Elegir(Release, new Version(1, 0, 2), Architecture.X64)!.Sha256);
        // El ensamblado dice 1.0.3.0: no tiene que parecer más vieja que el tag v1.0.3.
        Assert.Null(Actualizador.Elegir(Release, new Version(1, 0, 3, 0), Architecture.X64));
        Assert.Null(Actualizador.Elegir(Release, new Version(1, 1, 0), Architecture.X64));
    }

    // Caveat es el respaldo si la PC no tiene Segoe Print: PdfPig tiene que poder incrustarla.
    [Fact]
    public void La_letra_de_respaldo_se_puede_usar()
    {
        using var s = typeof(Recibos).Assembly.GetManifestResourceStream("Caveat-Regular.ttf")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        var b = new PdfDocumentBuilder();
        var fuente = b.AddTrueTypeFont(m.ToArray());
        b.AddPage(PageSize.A4).AddText("P.J.C.", 16, new PdfPoint(100, 100), fuente);
        using var doc = PdfDocument.Open(b.Build());
        Assert.Contains("P.J.C.", doc.GetPage(1).Text);
    }

    // Los recibos y la nómina reales están en la carpeta del proyecto y no van al git (son datos
    // personales). Sin ellos (en CI, por ejemplo) estas pruebas no tienen con qué correr y pasan vacías.
    static readonly string Raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    [Fact]
    public async Task Separa_los_recibos_reales()
    {
        var pdfs = Directory.GetFiles(Raiz, "*RECIBOS*.pdf");
        if (pdfs.Length == 0) return;

        var salida = Directory.CreateTempSubdirectory().FullName;
        var avisos = new List<string>();
        var creados = (await Recibos.Procesar(pdfs, null, avisos.Add, salida)).Creados;

        Assert.DoesNotContain(avisos, a => a.StartsWith('⚠'));
        // Ninguna hoja se pierde ni se repite: un recibo puede ocupar más de una hoja.
        static int Hojas(string p) { using var d = PdfDocument.Open(p); return d.NumberOfPages; }
        Assert.Equal(pdfs.Sum(Hojas), creados.Sum(Hojas));

        // Cada archivo dentro de la carpeta de un empleador, con «NOMBRE MM AAAA.pdf» y sus iniciales en cada hoja.
        foreach (var ruta in creados)
        {
            Assert.Equal(salida, Path.GetDirectoryName(Path.GetDirectoryName(ruta)));
            var m = Regex.Match(Path.GetFileName(ruta), @"^(.+) (\d{2}) (\d{4})\.pdf$");
            Assert.True(m.Success, ruta);
            using var doc = PdfDocument.Open(ruta);
            foreach (var hoja in doc.GetPages())
                Assert.Contains(Recibos.Iniciales(m.Groups[1].Value), string.Join(" ", hoja.GetWords().Select(w => w.Text)));
        }
        Directory.Delete(salida, true);
    }

    // Con el OCR de Windows: cada empleado de la nómina real tiene que tener su recibo entre los separados.
    [Fact]
    public async Task Revisa_la_nomina_real()
    {
        var nominas = Directory.GetFiles(Raiz, "*NOMINA*.pdf");
        if (nominas.Length == 0) return;
        // Con la nómina presente, sin OCR la prueba pasaría sin probar nada: que falle.
        var ocr = WindowsOcr.Crear();
        Assert.NotNull(ocr);

        var salida = Directory.CreateTempSubdirectory().FullName;
        var avisos = new List<string>();
        var r = await Recibos.Procesar(nominas.Concat(Directory.GetFiles(Raiz, "*RECIBOS*.pdf")), ocr, avisos.Add, salida);

        Assert.DoesNotContain(avisos, a => a.StartsWith('⚠'));
        Assert.Equal(nominas.Length, r.Nominas.Count);
        foreach (var n in r.Nominas)
        {
            Assert.NotEmpty(n.Empleados);
            Assert.All(n.Empleados, e => Assert.NotEmpty(e.Nombre));
            Assert.All(Recibos.Revisar(n, Directory.GetDirectories(salida)), x => Assert.NotNull(x.Recibo));
        }
        Directory.Delete(salida, true);
    }
}
