using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Formas = System.Windows.Shapes;

// La pestaña Descargas: la carpeta de esta PC donde se bajan los recibos de cualquier cliente (ver Descargas.cs). Usa las
// piezas de Clientes: los renglones, el panel de la derecha, las vistas previas, la lista de lo que se guarda y su tilde.
public partial class Ventana
{
    static readonly string DescargasFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "descargas.txt");
    // Lo leído de la carpeta de descargas, aparte de lo de cada mes: sus archivos son de clientes y meses distintos, y se van.
    static readonly string MemoriaDescargasFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "lecturas-descargas.json");
    const string Revisar = "REVISAR"; // el renglón de lo que no se sabe de quién es

    string? carpetaDescargas;
    bool descargasVistas, panelDeDescargas, clientesViejos;
    bool mostrandoResultado; // el panel muestra cómo salió lo guardado: no se rearma solo hasta que lo toquen
    Bandeja? bandeja;
    Memoria? memoriaDescargas;
    CancellationTokenSource? corteDescargas;
    int generacionDescargas, turnoPlanDescarga;
    string? firmaPanel; // lo que muestra el panel de Descargas: si no cambió, no se rearma (se cerrarían las vistas previas abiertas)
    readonly Dictionary<string, FilaCliente> porDescarga = new(StringComparer.OrdinalIgnoreCase);
    // Lo que se guarda: lo de un cliente (su panel) o lo de todos (el botón de abajo); el panel muestra uno a la vez.
    List<(FilaCliente Fila, Escritura E, TextBlock Marca, TextBlock Texto)> planDescargas = [];
    // El renglón de cada recibo en el panel, por su ruta en Z: ahí acusa recibo al guardarse.
    readonly Dictionary<string, (TextBlock Marca, TextBlock Texto)> lineasDescarga = new(StringComparer.OrdinalIgnoreCase);

    string TextoGuardarDescargas => $"Guardar en {Donde}…";

    // Desde IniciarClientes, después de InitializeComponent.
    void IniciarDescargas()
    {
        try { carpetaDescargas = File.ReadAllText(DescargasFile).Trim() is { Length: > 0 } c ? c : null; }
        catch { } // nunca se eligió
        TituloDescargas();
        reDescargas.Click += (_, _) =>
        {
            if (guardando) return;
            // La flecha da una vuelta: dice «de nuevo». Y vuelve a mirar las carpetas de los clientes, por si hay una nueva.
            Resorte.Animar((reDescargasGiro, "giro"), Math.Round(reDescargasGiro.Angle / 360) * 360 + 360, v => reDescargasGiro.Angle = v, rigidez: 140, rebote: 0.1);
            Descargas.Olvidar();
            _ = RevisarDescargas(cerrarPanel: true);
        };
        cambiarDescargas.Click += (_, _) => ElegirDescargas();
        elegirDescargas.Click += (_, _) => ElegirDescargas();
        guardarDescargas.Click += async (_, _) =>
        {
            if (muestra == Muestra.Guardar && panelDeDescargas) CerrarPanel();
            else await AbrirGuardarDescargas();
        };
        Hundir(reDescargas, 0.94);
        Hundir(cambiarDescargas, 0.94);
        Hundir(guardarDescargas, 0.98, objetivo: guardarDescargasCaja);
        // Apagado mientras no hay nada para guardar: encendido parecería que se puede tocar.
        guardarDescargas.IsEnabledChanged += (_, _) => Opacidad(guardarDescargasCaja, guardarDescargas.IsEnabled ? 1 : 0.4);
    }

    void TituloDescargas()
    {
        descargasTitulo.Text = carpetaDescargas is null ? "Descargas" : Path.GetFileName(carpetaDescargas.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : carpetaDescargas;
        cambiarDescargas.ToolTip = carpetaDescargas is null ? "Elegir la carpeta de descargas" : $"Carpeta de descargas: {carpetaDescargas}\nTocala para elegir otra.";
        AutomationProperties.SetHelpText(cambiarDescargas, (string)cambiarDescargas.ToolTip);
        guardarDescargasTexto.Text = TextoGuardarDescargas;
    }

    void EstadoDescargas(string texto) => descargasEstado.Text = texto;

    // La carpeta de los clientes de ese año (la misma de la pestaña Clientes), o null si no está.
    string? RaizDelAnio(int anio) => RaizClientes(anio).Raiz;

    void ElegirDescargas()
    {
        if (guardando || planeando) return;
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Elegí la carpeta donde bajás los recibos firmados" };
        if (carpetaDescargas is not null && Directory.Exists(carpetaDescargas)) d.InitialDirectory = carpetaDescargas;
        if (d.ShowDialog(this) != true) return;
        // Adentro de la de los clientes no: ahí cada archivo ya tiene su lugar, y «Acomodados» quedaría en la unidad del estudio.
        if (RaizClientes(DateTime.Today.Year).Raiz is { } raiz && (d.FolderName + "\\").StartsWith(raiz.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            EstadoDescargas("⚠ Esa carpeta está adentro de la de los clientes: elegí una de esta PC.");
            return;
        }
        carpetaDescargas = d.FolderName;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DescargasFile)!);
            File.WriteAllText(DescargasFile, d.FolderName);
        }
        catch { } // sin recordarla, vale hasta cerrar la app
        TituloDescargas();
        // La carpetita acusa recibo, y la lista se vacía y se vuelve a llenar con lo de la carpeta nueva.
        Resorte.Animar((cambiarDescargas, "latido"), 1, v => cambiarDescargasEscala.ScaleX = cambiarDescargasEscala.ScaleY = v, velocidad: 2.5, rigidez: 500, rebote: 0.5);
        SacarDescargas();
        _ = RevisarDescargas(cerrarPanel: true);
    }

    // ---- La revisión ----

    // Tres pasadas, como Clientes: primero lo que ya se sabe (lo leído antes y los PDF con texto), que es un instante; después
    // las fotos y los escaneos nuevos con el OCR, mostrando cada pocos segundos lo que ya se ubicó; y al final, a fondo, las
    // hojas que no se pudieron ubicar.
    async Task RevisarDescargas(bool cerrarPanel = false)
    {
        corteDescargas?.Cancel();
        var corte = (corteDescargas = new CancellationTokenSource()).Token;
        var g = ++generacionDescargas;
        if (cerrarPanel && panelDeDescargas && muestra != Muestra.Nada && !guardando) CerrarPanel();
        guardarDescargas.IsEnabled = false;
        var hoy = DateTime.Today;
        var (raiz, esperada) = await Task.Run(() => RaizClientes(hoy.Year));
        if (g != generacionDescargas) return;
        // El botón dice adónde va lo que se guarda («Guardar en Z:…»), aunque todavía no se haya abierto Clientes.
        raizActual ??= raiz;
        guardarDescargasTexto.Text = TextoGuardarDescargas;
        string? falta = carpetaDescargas is null
            ? "Elegí la carpeta de esta PC donde bajás los recibos firmados de los clientes, como vengan. Se elige una sola vez: queda guardada."
            : !Directory.Exists(carpetaDescargas) ? $"No encuentro la carpeta de descargas:\n{carpetaDescargas}"
            : esperada is null ? "Primero elegí la carpeta de los clientes, en la pestaña Clientes: ahí se guarda cada recibo."
            : raiz is null ? $"No encuentro la carpeta de los clientes:\n{esperada}" : null;
        if (falta is not null)
        {
            SacarDescargas();
            bandeja = null;
            Opacidad(descargasCartel, 0);
            Sin(falta, conBoton: carpetaDescargas is null || !Directory.Exists(carpetaDescargas));
            EstadoDescargas("");
            return;
        }
        var carpeta = carpetaDescargas!;
        EstadoDescargas("Buscando en la carpeta…");
        memoriaDescargas ??= await Task.Run(() => new Memoria(MemoriaDescargasFile));
        await PrepararLector();
        if (g != generacionDescargas) return;
        var mem = memoriaDescargas;
        void Avisar(string m) => Dispatcher.InvokeAsync(() => { if (g == generacionDescargas) EstadoDescargas(m); });
        try
        {
            var b = await Task.Run(() => Descargas.Revisar(carpeta, RaizDelAnio, hoy, ocr, null, mem, Avisar, corte), corte);
            if (g != generacionDescargas) return;
            MostrarBandeja(b, leyendo: b.PorLeer > 0 && lector is not null);
            if (b.PorLeer > 0 && lector is { } leer)
            {
                var conOcr = Task.Run(() => Descargas.Revisar(carpeta, RaizDelAnio, hoy, ocr, leer, mem, Avisar, corte, Huella), corte);
                while (await Task.WhenAny(conOcr, Task.Delay(4000, corte)) != conOcr)
                {
                    var parcial = await Task.Run(() => Descargas.Revisar(carpeta, RaizDelAnio, hoy, ocr, null, mem, _ => { }, corte), corte);
                    if (g != generacionDescargas) return;
                    MostrarBandeja(parcial, leyendo: true);
                }
                b = await conOcr;
                if (g != generacionDescargas) return;
                MostrarBandeja(b);
                _ = Task.Run(mem.Guardar);
            }
            if (lectorAFondo is { } aFondo && b.SinUbicar.Count > 0)
            {
                EstadoDescargas("Leyendo a fondo lo que no pude ubicar…");
                if (await Task.Run(() => Descargas.Profundizar(b, RaizDelAnio, hoy, ocr, aFondo, mem, m => Avisar($"A fondo: {m}"), corte, Huella), corte) > 0)
                {
                    b = await Task.Run(() => Descargas.Revisar(carpeta, RaizDelAnio, hoy, ocr, lector, mem, Avisar, corte, Huella), corte);
                    if (g != generacionDescargas) return;
                    MostrarBandeja(b);
                    _ = Task.Run(mem.Guardar);
                }
            }
            var archivos = b.Completos.Count + b.SinUbicar.Select(u => u.Hoja.Ruta).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            EstadoDescargas(lector is null ? "⚠ No pude preparar el OCR: las fotos quedaron sin leer"
                : b.Avisos.Count > 0 ? $"⚠ {b.Avisos[0]}" : archivos == 1 ? "1 archivo revisado" : $"{archivos} archivos revisados");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (g == generacionDescargas) EstadoDescargas($"⚠ {ex.Message}");
        }
    }

    // La lista: un renglón por cliente con recibos encontrados, en orden alfabético, y «REVISAR» al final. Los que ya no
    // están se van; los nuevos entran; los demás corren a su lugar.
    void MostrarBandeja(Bandeja b, bool leyendo = false)
    {
        bandeja = b;
        var claves = b.Acomodos.Select(a => a.Cliente).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (b.SinUbicar.Count > 0) claves.Add(Revisar);
        foreach (var f in porDescarga.Values.Where(f => !claves.Contains(f.Ruta, StringComparer.OrdinalIgnoreCase)).ToList()) SacarDescarga(f);
        var orden = claves.OrderBy(c => c == Revisar).ThenBy(c => Path.GetFileName(c), StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < orden.Count; i++)
        {
            var f = porDescarga.TryGetValue(orden[i], out var ya) ? ya : AgregarFila(orden[i], i, deClientes: false);
            f.Orden = i;
            PintarDescarga(f, b);
        }
        double y = 0;
        foreach (var f in porDescarga.Values.OrderBy(f => f.Orden))
        {
            var el = f.El;
            var top = y;
            // El que entra aparece en su lugar; los demás corren al suyo.
            Resorte.Animar((el, "top"), top, v => Canvas.SetTop(el, v), desde: double.IsNaN(Canvas.GetTop(el)) || el.Opacity < 0.05 ? top : null, rigidez: 220, rebote: 0.12);
            y += AltoRenglon;
        }
        descargasFilas.Height = y;
        if (orden.Count > 0) Sin(null);
        else if (leyendo) Sin(null); // todavía no se ubicó nada: arriba dice qué está leyendo
        else Sin("No hay recibos para acomodar en la carpeta.", conBoton: false);
        guardarDescargas.IsEnabled = b.Acomodos.Any(a => !a.Recibo.EnSuCarpeta) || b.Completos.Count > 0;
        CartelDescargas(b, leyendo);
        // El panel abierto se rearma solo si cambió lo suyo, y se cierra si su renglón ya no está.
        if (panelDeDescargas && muestra == Muestra.Cliente && elegida is { } e && !guardando && !mostrandoResultado)
        {
            if (!porDescarga.ContainsValue(e)) CerrarPanel();
            else if (FirmaDe(e, b) != firmaPanel) MostrarDescarga(e);
        }
    }

    // El texto del centro de la lista (sin carpeta, sin recibos), o nada.
    void Sin(string? texto, bool conBoton = false)
    {
        if (texto is null)
        {
            Opacidad(sinDescargas, 0, alTerminar: () => { if (sinDescargas.Opacity < 0.05) sinDescargas.Visibility = Visibility.Collapsed; });
            return;
        }
        sinDescargasTexto.Text = texto;
        elegirDescargas.Visibility = conBoton ? Visibility.Visible : Visibility.Collapsed;
        sinDescargas.Visibility = Visibility.Visible;
        Opacidad(sinDescargas, 1);
    }

    void SacarDescarga(FilaCliente f)
    {
        porDescarga.Remove(f.Ruta);
        var el = f.El;
        Opacidad(el, 0, alTerminar: () => descargasFilas.Children.Remove(el));
    }

    void SacarDescargas()
    {
        foreach (var f in porDescarga.Values.ToList()) SacarDescarga(f);
        if (panelDeDescargas && muestra != Muestra.Nada && !guardando) CerrarPanel();
    }

    // «octubre», «octubre y septiembre», «octubre, septiembre y agosto»: los más nuevos primero.
    static string DeQueMeses(IEnumerable<Acomodo> cuales)
    {
        var meses = cuales.Select(a => (a.Recibo.Anio, a.Recibo.Mes)).Distinct().OrderByDescending(m => m).Select(m => Clientes.NombreMes(m.Mes).ToLowerInvariant()).ToList();
        return meses.Count <= 1 ? string.Concat(meses) : $"{string.Join(", ", meses.SkipLast(1))} y {meses[^1]}";
    }

    // Verde: hay recibos para guardar. Gris: ya estaban en su carpeta (solo falta pasar los originales a «Acomodados»).
    // Amarillo, con la pregunta: lo que no se sabe de quién es.
    void PintarDescarga(FilaCliente f, Bandeja b)
    {
        string glifo, texto;
        Brush? color;
        if (f.Ruta == Revisar)
        {
            var archivos = b.SinUbicar.Select(u => u.Hoja.Ruta).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            (glifo, color, texto) = (Pregunta, Amarillo, archivos == 1 ? "1 archivo que no sé de quién es" : $"{archivos} archivos que no sé de quién son");
        }
        else
        {
            var suyos = b.Acomodos.Where(a => a.Cliente.Equals(f.Ruta, StringComparison.OrdinalIgnoreCase)).ToList();
            var nuevos = suyos.Where(a => !a.Recibo.EnSuCarpeta).ToList();
            var ya = suyos.Count - nuevos.Count;
            texto = nuevos.Count == 0 ? (ya == 1 ? "ya estaba en su carpeta" : "ya estaban en su carpeta")
                : $"{(nuevos.Count == 1 ? "1 recibo" : $"{nuevos.Count} recibos")} de {DeQueMeses(nuevos)}" + (ya > 0 ? $" · {ya} ya {(ya == 1 ? "estaba" : "estaban")}" : "");
            (glifo, color) = nuevos.Count > 0 ? (Tilde, Verde) : (Tilde, null);
        }
        // La marca pasa a su forma con un rebote chico, solo cuando cambia: la lista se repinta cada pocos segundos.
        var cambio = f.Marca.Text != glifo || f.Detalle.Text != texto;
        if (color is null) f.Disco.SetResourceReference(Formas.Shape.FillProperty, "ControlStrongStrokeColorDefaultBrush");
        else f.Disco.Fill = color;
        f.Marca.Text = glifo;
        f.Marca.Foreground = color == Amarillo ? TintaOscura : Brushes.White;
        f.Detalle.Text = texto;
        f.Detalle.ToolTip = texto;
        f.Detalle.SetResourceReference(TextBlock.ForegroundProperty, color == Amarillo ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(f.Boton, $"{f.Nombre}: {texto}");
        Opacidad(f.Chevron, 1);
        if (cambio) Resorte.Animar((f, "marca"), 1, v => f.MarcaEscala.ScaleX = f.MarcaEscala.ScaleY = v, desde: 0.45, velocidad: 3, rigidez: 420, rebote: 0.4);
    }

    void CartelDescargas(Bandeja b, bool leyendo)
    {
        var nuevos = b.Acomodos.Where(a => !a.Recibo.EnSuCarpeta).ToList();
        var clientes = nuevos.Select(a => a.Cliente).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var revisar = b.SinUbicar.Select(u => u.Hoja.Ruta).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var (texto, icono, verde, amarillo) =
            nuevos.Count > 0 ? ($"{(nuevos.Count == 1 ? "1 recibo" : $"{nuevos.Count} recibos")} para guardar en {(clientes == 1 ? "1 cliente" : $"{clientes} clientes")}", Tilde, true, false)
            : b.Completos.Count > 0 ? ("Ya estaban en su carpeta: los originales pasarán a «Acomodados»", Tilde, false, false)
            : revisar > 0 ? (revisar == 1 ? "1 archivo para revisar" : $"{revisar} archivos para revisar", Pregunta, false, true)
            : (leyendo ? "Leyendo la carpeta…" : "Nada para acomodar", Raya, false, false);
        descargasCartelTexto.Text = texto;
        if (descargasCartelIcono.Text != icono)
        {
            descargasCartelIcono.Text = icono;
            Resorte.Animar((descargasCartelIcono, "escala"), 1, v => descargasCartelIconoEscala.ScaleX = descargasCartelIconoEscala.ScaleY = v, desde: 0.4, rigidez: 420, rebote: 0.4);
        }
        Opacidad(descargasCartelVerde, verde ? 1 : 0);
        Opacidad(descargasCartelAmarillo, amarillo ? 1 : 0);
        // Sobre el amarillo, la marca oscura para que se lea.
        descargasCartelIcono.Foreground = amarillo ? TintaOscura : Brushes.White;
        if (descargasCartel.Opacity < 0.99) Aparecer(descargasCartel, descargasCartelAltura, desdeY: 8);
    }

    // ---- El panel ----

    void ElegirDescarga(FilaCliente f)
    {
        if (elegida == f && muestra == Muestra.Cliente)
        {
            CerrarPanel();
            return;
        }
        MarcarElegida(f);
        MostrarDescarga(f);
    }

    // Lo que muestra el panel de un renglón, para saber si cambió.
    static string FirmaDe(FilaCliente f, Bandeja b) => f.Ruta == Revisar
        ? string.Join("|", b.SinUbicar.Select(u => $"{u.Hoja.Ruta}:{u.Hoja.Hoja}:{u.PorQue}"))
        : string.Join("|", b.Acomodos.Where(a => a.Cliente.Equals(f.Ruta, StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.Recibo.Ruta}:{a.Recibo.Hojas.Count}:{a.Recibo.EnSuCarpeta}"));

    // El panel de un cliente: sus recibos por mes, cada uno ya con el nombre con que se guardará y su vista previa (derecha y
    // con contraste), y abajo lo demás que se guardará. El de «REVISAR»: los archivos que quedan en la carpeta, y por qué.
    void MostrarDescarga(FilaCliente f)
    {
        if (bandeja is not { } b) return;
        panelDeDescargas = true;
        mostrandoResultado = false;
        firmaPanel = FirmaDe(f, b);
        lineasDescarga.Clear();
        pieGuardar.Visibility = Visibility.Collapsed;
        pieCliente.Visibility = Visibility.Visible;
        guardarCliente.Visibility = Visibility.Collapsed;
        if (f.Ruta == Revisar)
        {
            var grupos = b.SinUbicar.GroupBy(u => u.Hoja.Ruta, StringComparer.OrdinalIgnoreCase).ToList();
            Llenar(Revisar, grupos.Count == 1 ? "1 archivo que no sé de quién es: queda en la carpeta, como está"
                : $"{grupos.Count} archivos que no sé de quién son: quedan en la carpeta, como están", null, lista =>
            {
                foreach (var g in grupos)
                {
                    var suyas = g.ToList();
                    var (linea, _, _) = LineaPlan($"{Path.GetFileName(g.Key)} · {Descargas.PorQueNo(b, suyas)}", g.Key);
                    lista.Add(ConPrevia(linea, Hojas(suyas.Select(u => u.Hoja).ToList()), aire: 0));
                }
            });
            // «Abrir la carpeta», la de descargas: para acomodarlos a mano.
            carpetaCliente.Visibility = Visibility.Visible;
            copiarCarpeta.ToolTip = $"Copiar la dirección de la carpeta:\n{carpetaDescargas}";
            AutomationProperties.SetHelpText(copiarCarpeta, carpetaDescargas ?? "");
            AbrirPanel(Muestra.Cliente);
            return;
        }
        var suyos = b.Acomodos.Where(a => a.Cliente.Equals(f.Ruta, StringComparison.OrdinalIgnoreCase)).ToList();
        var nuevos = suyos.Count(a => !a.Recibo.EnSuCarpeta);
        var ya = suyos.Count - nuevos;
        var resumen = string.Join(" · ", new[]
        {
            nuevos == 0 ? "" : nuevos == 1 ? "1 recibo para guardar" : $"{nuevos} recibos para guardar",
            ya == 0 ? "" : ya == 1 ? "1 ya estaba en su carpeta" : $"{ya} ya estaban en su carpeta",
        }.Where(t => t.Length > 0));
        var seccion = new StackPanel();
        Llenar(f.Nombre, resumen, null, lista =>
        {
            foreach (var mes in suyos.GroupBy(a => (a.Recibo.Anio, a.Recibo.Mes)).OrderByDescending(m => m.Key))
            {
                lista.Add(Seccion($"{Clientes.NombreMes(mes.Key.Mes)} {mes.Key.Anio}"));
                var carpeta = Path.GetFileName(mes.First().Recibo.Carpeta);
                lista.Add(Nota(mes.All(a => a.Recibo.EnSuCarpeta) ? $"Ya estaban en «{carpeta}»: no se guarda nada, y sus originales pasarán a «Acomodados»."
                    : $"Se guardarán en «{carpeta}», derechos y con más contraste:"));
                foreach (var a in mes)
                {
                    var l = a.Recibo;
                    var texto = Path.GetFileNameWithoutExtension(l.Archivo) + (l.Hojas.Count > 1 ? $" ({l.Hojas.Count} hojas)" : "") + (l.EnSuCarpeta ? " · ya estaba en su carpeta" : "");
                    var (linea, marca, t) = LineaPlan(texto, l.Ruta!);
                    lista.Add(ConPrevia(linea, ComoQueda(null, new(Accion.ASuMes, l.Ruta!, l.Empleado, l.Hojas)), tooltip: "Ver cómo va a quedar", aire: 0));
                    if (!l.EnSuCarpeta) lineasDescarga[l.Ruta!] = (marca, t);
                }
            }
            lista.Add(seccion);
        });
        carpetaCliente.Visibility = Visibility.Collapsed;
        AbrirPanel(Muestra.Cliente);
        _ = PlanDeDescarga(f, seccion);
    }

    // Lo que se guardará de un cliente: cada recibo acusa recibo en su renglón; abajo, la carpeta nueva y los originales.
    async Task PlanDeDescarga(FilaCliente f, StackPanel destino)
    {
        var turno = ++turnoPlanDescarga;
        var b = bandeja!;
        List<Escritura> suyo;
        try { suyo = await Task.Run(() => Descargas.Planear(b, f.Ruta)); }
        catch (Exception ex)
        {
            suyo = [];
            destino.Children.Add(Nota($"⚠ {ex.Message}"));
        }
        if (turno != turnoPlanDescarga || elegida != f || muestra != Muestra.Cliente || !panelDeDescargas) return;
        destino.Children.Clear();
        var marcas = new Dictionary<Escritura, (TextBlock Marca, TextBlock Texto)>();
        foreach (var e in suyo.Where(e => e.Accion == Accion.ASuMes))
            if (lineasDescarga.TryGetValue(e.Ruta, out var linea)) marcas[e] = linea;
        var resto = suyo.Where(e => !marcas.ContainsKey(e)).ToList();
        if (resto.Count > 0)
        {
            destino.Children.Add(Seccion("También se guardará"));
            ArmarPlan(null, resto, el => destino.Children.Add(el), (e, marca, texto) => marcas[e] = (marca, texto));
        }
        // En el orden del plan: la carpeta nueva antes que lo de adentro, los originales al final.
        planDescargas = suyo.Select(e => (f, e, marcas[e].Marca, marcas[e].Texto)).ToList();
        if (suyo.Count == 0)
        {
            destino.Children.Add(Nota("Nada para guardar de este cliente."));
            return;
        }
        guardarCliente.Content = suyo.Count == 1 ? "Guardar 1 cosa" : $"Guardar {suyo.Count} cosas";
        guardarCliente.IsEnabled = true;
        guardarCliente.Visibility = Visibility.Visible;
        // Entra con la lista que lo trae, subiendo apenas.
        var corrida = new TranslateTransform();
        guardarCliente.RenderTransform = corrida;
        Aparecer(guardarCliente, corrida, desdeY: 6);
    }

    // ---- Guardar ----

    // El cliente de lo que se guarda: el del recibo, o el de la carpeta de firmados que se crea.
    string ClienteDe(Escritura e) => bandeja?.Acomodos.FirstOrDefault(a => e.Accion == Accion.Carpeta
        ? string.Equals(a.Recibo.Carpeta, e.Ruta, StringComparison.OrdinalIgnoreCase)
        : string.Equals(a.Recibo.Ruta, e.Ruta, StringComparison.OrdinalIgnoreCase))?.Cliente ?? "";

    async Task AbrirGuardarDescargas()
    {
        if (guardando || planeando || bandeja is not { } b) return;
        planeando = true;
        guardarDescargasTexto.Text = "Armando la lista…";
        List<Escritura> nuevo;
        try { nuevo = await Task.Run(() => Descargas.Planear(b)); }
        catch (Exception ex)
        {
            guardarDescargasTexto.Text = ex.Message;
            Luego(2600, () => { if (!guardando) guardarDescargasTexto.Text = TextoGuardarDescargas; });
            return;
        }
        finally
        {
            planeando = false;
        }
        guardarDescargasTexto.Text = TextoGuardarDescargas;
        if (nuevo.Count == 0)
        {
            guardarDescargasTexto.Text = "No hay nada para guardar";
            Luego(2200, () => { if (!guardando) guardarDescargasTexto.Text = TextoGuardarDescargas; });
            return;
        }
        var recibos = nuevo.Count(e => e.Accion == Accion.ASuMes);
        var originales = nuevo.Count(e => e.Accion == Accion.Acomodar);
        var clientes = nuevo.Where(e => e.Accion == Accion.ASuMes).Select(ClienteDe).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var partes = new[]
        {
            recibos == 0 ? "" : $"{(recibos == 1 ? "1 recibo" : $"{recibos} recibos")} en {(clientes == 1 ? "1 cliente" : $"{clientes} clientes")}",
            originales == 0 ? "" : originales == 1 ? "1 original a «Acomodados»" : $"{originales} originales a «Acomodados»",
        }.Where(t => t.Length > 0);
        var marcas = new Dictionary<Escritura, (TextBlock Marca, TextBlock Texto)>();
        panelDeDescargas = true;
        mostrandoResultado = false;
        MarcarElegida(null);
        Llenar(nuevo.Count == 1 ? "Se guardará 1 cosa" : $"Se guardarán {nuevo.Count} cosas", $"{string.Join(" · ", partes)}.", Disquete, lista =>
        {
            foreach (var g in nuevo.Where(e => e.Accion != Accion.Acomodar).GroupBy(ClienteDe, StringComparer.OrdinalIgnoreCase))
            {
                var titulo = new TextBlock { Text = Path.GetFileName(g.Key), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 3) };
                titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                lista.Add(titulo);
                ArmarPlan(null, g.ToList(), el => lista.Add(el), (e, marca, texto) => marcas[e] = (marca, texto));
            }
            var acomodar = nuevo.Where(e => e.Accion == Accion.Acomodar).ToList();
            if (acomodar.Count > 0)
            {
                var titulo = new TextBlock { Text = "Carpeta de descargas", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 3) };
                titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                lista.Add(titulo);
                ArmarPlan(null, acomodar, el => lista.Add(el), (e, marca, texto) => marcas[e] = (marca, texto));
            }
        });
        // En el orden del plan: cada carpeta antes que lo de adentro, y los originales después de sus recibos.
        planDescargas = nuevo.Select(e => (porDescarga.GetValueOrDefault(ClienteDe(e))!, e, marcas[e].Marca, marcas[e].Texto)).ToList();
        pieGuardar.Visibility = Visibility.Visible;
        pieGuardarTexto.Text = "No se borra ningún archivo: los originales se moverán a «Acomodados», y lo que ya existe en la carpeta de los clientes queda como está.";
        pieCliente.Visibility = Visibility.Collapsed;
        guardarSi.IsEnabled = guardarNo.IsEnabled = true;
        guardarNo.Content = "Cancelar";
        AbrirPanel(Muestra.Guardar);
    }

    // Guarda la lista que se está viendo, renglón por renglón. Los originales van al final, y cada uno se mueve solo si sus
    // recibos ya están en Z (ver Descargas.Escribir).
    async Task GuardarDescargas(List<(FilaCliente Fila, Escritura E, TextBlock Marca, TextBlock Texto)> cuales, bool deUnCliente)
    {
        if (guardando || cuales.Count == 0 || bandeja is not { } b) return;
        guardando = true;
        guardarSi.IsEnabled = guardarNo.IsEnabled = panelCerrar.IsEnabled = guardarCliente.IsEnabled = false;
        int hechos = 0, fallas = 0;
        foreach (var p in cuales)
        {
            panelSubtitulo.Text = $"Guardando {hechos + fallas + 1} de {cuales.Count}…";
            try
            {
                await Task.Run(() => Descargas.Escribir(p.E, b, WindowsOcr.Corregir));
                hechos++;
                Marcar(p.Marca, ok: true);
            }
            catch (Exception ex)
            {
                fallas++;
                Marcar(p.Marca, ok: false);
                p.Texto.Text = p.Texto.Text.Length > 0 ? $"{p.Texto.Text} · no se pudo: {ex.Message}" : $"no se pudo: {ex.Message}";
                p.Texto.Visibility = Visibility.Visible;
                p.Texto.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            }
        }
        // La pestaña Clientes, si ya revisó, no sabe de los recibos nuevos: se revisa de nuevo al volver a ella.
        if (cuales.Any(p => p.E.Accion == Accion.ASuMes) && clientesVistos) clientesViejos = true;
        // El destino no se ve: el panel mismo se vuelve la confirmación.
        panelTitulo.Text = fallas == 0 ? hechos == 1 ? "Guardé 1 cosa" : $"Guardé {hechos} cosas" : $"Guardé {hechos}; {fallas} no se pudieron";
        panelSubtitulo.Text = fallas == 0 ? "Cada recibo quedó en su cliente y su mes, y los originales en «Acomodados»." : "Las que no se pudieron están marcadas en la lista.";
        Icono(fallas == 0 ? Tilde : Alerta, fallas == 0 ? Verde : Rojo);
        guardando = false;
        mostrandoResultado = true;
        panelCerrar.IsEnabled = true;
        if (!deUnCliente && fallas > 0)
        {
            guardarNo.Content = "Cerrar";
            guardarNo.IsEnabled = true;
        }
        // Con todo bien, al rato se cierra el panel y la carpeta se revisa de nuevo (lo leído sale de la memoria): lo
        // guardado se va de la lista. Con fallas, el panel queda abierto para leerlas.
        if (fallas == 0)
            Luego(2400, () =>
            {
                if (guardando || !mostrandoResultado) return; // ya lo cambió el usuario
                if (panelDeDescargas && muestra != Muestra.Nada) CerrarPanel();
                _ = RevisarDescargas();
            });
        else _ = RevisarDescargas();
    }
}
