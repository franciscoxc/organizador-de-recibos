using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

public record VersionNueva(Version Version, string Url, string Sha256, long Tamano);

// Actualización desde los releases de GitHub: buscar, descargar y verificar; el reemplazo, al reiniciar.
// Sin WinForms: así las pruebas lo compilan directo.
public static class Actualizador
{
    const string Api = "https://api.github.com/repos/franciscoxc/organizador-de-recibos/releases/latest";
    public const string Pagina = "https://github.com/franciscoxc/organizador-de-recibos/releases/latest";

    // La pone el workflow desde el tag (v1.0.3 -> 1.0.3); una compilación local queda en 0.0.0.
    public static Version Actual { get; } = Normalizar(typeof(Actualizador).Assembly.GetName().Version ?? new Version());

    static readonly HttpClient Http = new()
    {
        // GitHub rechaza los pedidos a su API sin User-Agent.
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("OrganizadorDeRecibos", Actual.ToString()) } }
    };

    // El ensamblado dice 1.0.3.0 y el tag 1.0.3: sin igualarlos, 1.0.3.0 parecería más nueva que 1.0.3.
    static Version Normalizar(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static async Task<VersionNueva?> Buscar() =>
        Elegir(await Http.GetStringAsync(Api), Actual, RuntimeInformation.ProcessArchitecture);

    /// <summary>La versión publicada si es más nueva que la actual, con el .exe que corresponde a esta PC.</summary>
    public static VersionNueva? Elegir(string json, Version actual, Architecture arquitectura)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        if (!Version.TryParse(raiz.GetProperty("tag_name").GetString()?.TrimStart('v'), out var v) || Normalizar(v) <= Normalizar(actual))
            return null;
        // Un x64 corriendo emulado en ARM informa X64: sigue con la variante que eligió quien lo instaló.
        var sufijo = arquitectura == Architecture.Arm64 ? ".arm.exe" : ".x64.exe";
        var a = raiz.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith(sufijo, StringComparison.OrdinalIgnoreCase) == true);
        if (a.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"la versión {v} no trae el archivo para esta PC");
        // Sin la huella que publica GitHub no hay cómo verificar la descarga: mejor no actualizar.
        var huella = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (huella is null || !huella.StartsWith("sha256:")) throw new InvalidDataException($"la versión {v} no trae la huella del archivo");
        return new VersionNueva(Normalizar(v), a.GetProperty("browser_download_url").GetString()!, huella[7..], a.GetProperty("size").GetInt64());
    }

    /// <summary>Descarga la versión nueva al lado del .exe actual, verifica la huella y devuelve su ruta.</summary>
    public static async Task<string> Descargar(VersionNueva n, IProgress<int> progreso)
    {
        var destino = Environment.ProcessPath + ".nuevo";
        // Si ya se bajó en una apertura anterior y nadie reinició, no se baja de nuevo.
        if (File.Exists(destino) && await Coincide(destino, n.Sha256)) return destino;

        using (var respuesta = await Http.GetAsync(n.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            respuesta.EnsureSuccessStatusCode();
            await using var entrada = await respuesta.Content.ReadAsStreamAsync();
            await using var salida = File.Create(destino);
            var buffer = new byte[81920];
            long total = 0;
            int leidos;
            while ((leidos = await entrada.ReadAsync(buffer)) > 0)
            {
                await salida.WriteAsync(buffer.AsMemory(0, leidos));
                total += leidos;
                progreso.Report((int)(total * 100 / Math.Max(1, n.Tamano)));
            }
        }
        if (!await Coincide(destino, n.Sha256))
        {
            File.Delete(destino);
            throw new InvalidDataException("la descarga no coincide con la huella publicada");
        }
        return destino;
    }

    static async Task<bool> Coincide(string ruta, string sha256)
    {
        await using var f = File.OpenRead(ruta);
        return Convert.ToHexString(await SHA256.HashDataAsync(f)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Windows no deja sobrescribir un .exe abierto, pero sí renombrarlo: el actual pasa a .old, el nuevo
    // toma su nombre (así siguen andando los accesos directos y «Enviar a») y se abre. El .old lo borra
    // la versión nueva al arrancar.
    public static void Reemplazar(string nuevo)
    {
        var exe = Environment.ProcessPath!;
        var viejo = exe + ".old";
        File.Move(exe, viejo, true);
        try { File.Move(nuevo, exe); }
        catch { File.Move(viejo, exe); throw; } // sin esto la PC quedaría sin el programa
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    public static void BorrarViejo()
    {
        try { File.Delete(Environment.ProcessPath + ".old"); }
        catch { } // si la versión anterior todavía se está cerrando, se borra en la próxima apertura
    }
}
