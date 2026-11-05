using System.Text.RegularExpressions;
using UglyToad.PdfPig;
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
        var esperado = pdfs.Sum(p => { using var d = PdfDocument.Open(p); return d.NumberOfPages; });
        Assert.Equal(esperado, creados.Count);

        // Cada archivo dentro de la carpeta de un empleador, con «NOMBRE MM AAAA.pdf» y sus iniciales impresas.
        foreach (var ruta in creados)
        {
            Assert.Equal(salida, Path.GetDirectoryName(Path.GetDirectoryName(ruta)));
            var m = Regex.Match(Path.GetFileName(ruta), @"^(.+) (\d{2}) (\d{4})\.pdf$");
            Assert.True(m.Success, ruta);
            using var doc = PdfDocument.Open(ruta);
            var texto = string.Join(" ", doc.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
            Assert.Contains(Recibos.Iniciales(m.Groups[1].Value), texto);
        }
        Directory.Delete(salida, true);
    }
}
