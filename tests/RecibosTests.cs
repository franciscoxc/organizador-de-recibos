using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Xunit;

// Nombres inventados: el repo es público y los recibos reales tienen datos personales.
public class RecibosTests
{
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

    // Los recibos reales están en la carpeta del proyecto y no van al git (son datos personales).
    // Sin ellos (en CI, por ejemplo) la prueba no tiene con qué correr y pasa vacía.
    [Fact]
    public async Task Separa_los_recibos_reales()
    {
        var raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var pdfs = Directory.GetFiles(raiz, "*.pdf");
        if (pdfs.Length == 0) return;

        var salida = Directory.CreateTempSubdirectory().FullName;
        var avisos = new List<string>();
        var creados = await Recibos.Procesar(pdfs, null, avisos.Add, salida);

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
}
