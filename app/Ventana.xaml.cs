using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Formas = System.Windows.Shapes;

public partial class Ventana : Window
{
    const string AppName = "Organizador de Recibos";
    static readonly string CarpetasFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "carpetas.txt");
    // El verde y el rojo de las marcas y del letrero son fijos y llevan blanco encima: los del tema oscuro
    // (verde claro, rosa) no se leen con letra blanca.
    static readonly SolidColorBrush Verde = new(Color.FromRgb(0x10, 0x7C, 0x10)), Rojo = new(Color.FromRgb(0xC4, 0x2B, 0x1C));
    const string Tilde = "", Cruz = "", Alerta = "", Carpeta = "";

    readonly string[] startupFiles;
    readonly Ocr? ocr = CrearOcr();
    bool working, escribiendo, buscando, anticipando, cartelVisible;
    TaskCompletionSource<bool>? permiso; // la pregunta de guardar fuera de esta PC, mientras está abierta
    string? nuevoExe; // descargado y verificado: reemplaza al actual al reiniciar
    DateTime ultimaBusqueda; // «Revisar de nuevo» busca versión nueva una vez por hora como mucho
    Nomina? nomina; // la última leída: si después llegan recibos de ese mes, se vuelve a revisar
    Nomina? mostrada; // la que está en pantalla: si se vuelve a revisar, sus renglones cambian en el lugar
    readonly Dictionary<string, Ficha> porCarpeta = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Fila> porCuil = [];
    int generacion; // una revisión nueva deja sin efecto los pasos que la anterior tenía pendientes
    int faltanDestino; // hacia dónde rueda el número del letrero
    int volando;
    DateTime proximoVuelo;

    sealed class Ficha
    {
        public required Button Boton;
        public required TextBlock Numero;
        public required FrameworkElement Carpeta;
        public required ScaleTransform Escala;
        public int Llegados;
        public string? Archivo;
    }

    sealed class Fila
    {
        public required FrameworkElement El;
        public required Button Boton;
        public required Border Caja; // debajo del nombre, sus hojas: se abre al tocarlo
        public required StackPanel Adentro;
        public required Formas.Ellipse Disco;
        public required TextBlock Marca, Nombre, Cuil;
        public required ScaleTransform MarcaEscala;
        public required TranslateTransform Corrida;
        public required int Orden;
        public bool? Ok; // null: todavía sin revisar, un punto gris
        public bool PorNombre; // está su recibo, pero lo encontré por el nombre: hay que mirarlo
        public string? Recibo, Cargado; // su recibo, y el que ya se dibujó en la caja
        public bool Abierta;
    }

    public Ventana(string[] files)
    {
        startupFiles = files;
        InitializeComponent();
        Clientes.ArchivoNominas = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "nominas.json");
        IniciarClientes();
        IniciarDescargas();

        // Se ve solo cuando hay una versión nueva; mientras se baja, tocarlo no hace nada.
        actualizar.Click += (_, _) => { if (nuevoExe is not null) Reiniciar(); };
        tarjeta.Click += async (_, _) => await Pick();
        permisoSi.Click += (_, _) => Responder(true);
        permisoNo.Click += (_, _) => Responder(false);
        // Cerrar a mitad de un guardado dejaba un PDF cortado con nombre de recibo, y como la app no pisa nada, nadie lo
        // reemplazaba después. La ventana espera a que termine, y lo dice sacudiéndose. Con la pregunta abierta todavía
        // no se escribió nada: cerrar es decir que no.
        Closing += (_, e) =>
        {
            if (permiso is not null) Responder(false);
            else if (guardando)
            {
                e.Cancel = true;
                Resorte.Animar((panelCaja, "x"), 0, v => panelCorrida.X = v, velocidad: 700, rigidez: 900, rebote: 0.85);
            }
            else if (escribiendo)
            {
                e.Cancel = true;
                Sacudir();
            }
        };
        // Lo que va a pasar se insinúa: la tarjeta cede bajo el puntero antes de abrir el diálogo.
        tarjeta.PreviewMouseLeftButtonDown += (_, _) => Escalar(0.985);
        tarjeta.PreviewMouseLeftButtonUp += (_, _) => Escalar(anticipando ? 1.02 : 1);
        tarjeta.MouseLeave += (_, _) => Escalar(anticipando ? 1.02 : 1);
        DragEnter += (_, e) => Arrastre(e);
        DragOver += (_, e) => Arrastre(e);
        // WPF avisa DragLeave también al pasar de un elemento a otro adentro: cuenta solo si salió de la ventana.
        DragLeave += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) Anticipar(false);
        };
        Drop += (_, e) =>
        {
            Anticipar(false);
            if (working || enClientes || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
            Tragar();
            _ = ProcessAll(dropped);
        };
        // Otra resolución u otra escala de pantalla: la ventana se vuelve a acomodar para entrar entera.
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(Reencuadrar); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reencuadrar);
        ContentRendered += async (_, _) =>
        {
            AddToSendTo();
            _ = BuscarActualizacion();
            // La versión anterior se cierra apenas abre esta: un rato después ya se puede borrar.
            Luego(3000, Actualizador.BorrarViejo);
            if (startupFiles.Length > 0) await ProcessAll(startupFiles);
        };
    }

    static Ocr? CrearOcr()
    {
        try { return WindowsOcr.Crear(); }
        catch { return null; } // sin OCR igual funcionan los recibos con texto
    }

    async Task Pick()
    {
        if (working) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Elegí los PDF de los recibos o de la nómina",
            Filter = "PDF (*.pdf)|*.pdf",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) await ProcessAll(dialog.FileNames);
    }

    async Task ProcessAll(string[] paths)
    {
        if (working) return;
        // Una carpeta cuenta como todos los PDF que tiene adentro.
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.pdf") : [p])
            .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Avisar("");
        if (files.Length == 0)
        {
            Avisar("Eso no es un PDF");
            Sacudir();
            return;
        }

        working = true;
        SacarFichas();
        Estado(files.Length == 1 ? $"Leyendo {Path.GetFileName(files[0])}…" : $"Leyendo {files.Length} PDF…");
        var problems = new List<string>();
        var r = new Resultado([], [], [], []);
        var iniciales = estampar.IsChecked == true; // se lee acá: la casilla no se puede tocar desde otro hilo
        var negado = false;
        var conocida = nomina; // la que ya estaba cargada: con ella también se encuentra a quien la hoja no nombra bien
        try
        {
            // La pregunta se hace en el hilo de la ventana; Procesar espera la respuesta en el suyo.
            async Task<bool> Permiso(IReadOnlyList<string> rutas, IReadOnlyList<string> combinados)
            {
                var si = await await Dispatcher.InvokeAsync(() => PedirPermiso(rutas, combinados));
                negado = !si;
                return si;
            }
            // El OCR de Windows es asíncrono, pero el armado de los PDF no: afuera del hilo de la ventana.
            // Invoke y no BeginInvoke: un aviso en cola llegaría después de mostrar el resultado y se perdería.
            r = await Task.Run(() => Recibos.Procesar(files, ocr, msg => Dispatcher.Invoke(() =>
            {
                if (msg.StartsWith('⚠')) problems.Add(msg[2..]);
                else if (msg.StartsWith('✓')) Separado(msg[2..]);
                else if (msg.StartsWith('▤')) Juntado(msg[2..]);
                else Estado(msg);
            }), iniciales: iniciales, permiso: Permiso, conocida: conocida));
            escribiendo = false; // lo que sigue solo lee: ya se puede cerrar
            RecordarCarpetas([.. r.Creados, .. r.YaEstaban]);
            foreach (var f in r.Creados) FichaDe(f).Archivo ??= f;
            // Los que ya estaban separados no vuelan, porque no se escribieron, pero están: su ficha los cuenta.
            foreach (var f in r.YaEstaban)
            {
                var ficha = FichaDe(f);
                ficha.Archivo ??= f;
                ficha.Llegados++;
                Contar(ficha);
            }
            if (r.YaEstaban.Count > 0)
                problems.Add(r.YaEstaban.Count == 1 ? "1 recibo ya estaba separado y no lo reemplacé. Para rehacerlo, borrá antes el viejo."
                    : $"{r.YaEstaban.Count} recibos ya estaban separados y no los reemplacé. Para rehacerlos, borrá antes los viejos.");
            if (r.Nominas.Count > 0) nomina = r.Nominas[^1];
            // Se revisa si llegó una nómina, o si llegaron recibos del mes de la que ya estaba cargada.
            if (nomina is { } n && (r.Nominas.Count > 0 || r.Creados.Concat(r.YaEstaban).Any(c => c.EndsWith(Recibos.Sufijo(n), StringComparison.OrdinalIgnoreCase))))
            {
                Estado("Revisando la nómina…");
                var revision = await Task.Run(() => Recibos.Revisar(n, CarpetasParaRevisar(n)));
                // Sin recibos de esta tanda, la ficha es la de la carpeta donde están los de la nómina.
                if (r.Creados.Count == 0 && r.YaEstaban.Count == 0)
                    foreach (var recibo in revision.Select(x => x.Recibo).OfType<string>())
                    {
                        var f = FichaDe(recibo);
                        f.Archivo ??= recibo;
                        f.Llegados++;
                        Contar(f);
                    }
                MostrarRevision(n, revision);
            }
            else OcultarRevision();
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }
        finally
        {
            working = escribiendo = false;
            Estado("");
        }

        // Si dijo que no guardara, no hay nada que avisar: es lo que pidió.
        if (r.Creados.Count == 0 && r.Combinados.Count == 0 && r.Nominas.Count == 0 && problems.Count == 0 && !negado) problems.Add("No separé ningún recibo");
        if (problems.Count == 0) return;
        Avisar(problems.Count == 1 ? problems[0] : $"{problems[0]}\n(y {problems.Count - 1} avisos más)");
        Sacudir();
    }

    void Estado(string texto)
    {
        estado.Text = texto;
        estado.Visibility = texto.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Avisar(string texto)
    {
        avisos.Text = texto;
        avisos.Visibility = texto.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (texto.Length > 0) Opacidad(avisos, 1, desde: 0);
        Reencuadrar();
    }

    // Cambió el alto de lo que hay arriba de la revisión (un aviso que aparece o se va) o la pantalla (otra resolución, otra
    // escala): la lista de la revisión toma lo que sobre, con su barra de desplazamiento, y la ventana no se pasa de la
    // pantalla. Sin revisión, la ventana solo sube si hace falta.
    void Reencuadrar()
    {
        if (mostrada is not null && !enClientes) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (mostrada is not null && !enClientes) Crecer(true); });
        else Encuadrar();
    }

    // Si lo que se agregó (una ficha, un aviso) estira la ventana más allá del borde de abajo, la ventana sube
    // lo que haga falta en vez de quedar tapada por la barra de tareas. Después de acomodarse: ahí ya mide.
    void Encuadrar() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var area = SystemParameters.WorkArea;
        var arriba = Math.Max(area.Top, Math.Min(Top, area.Bottom - ActualHeight));
        if (Math.Abs(arriba - Top) > 0.5)
            Resorte.Animar((this, "arriba"), arriba, v => Top = v, desde: Top, rigidez: 170, rebote: 0.08);
    });

    // ---- El permiso para guardar fuera de esta PC ----

    // Todo lo que no es un disco fijo de esta PC: una unidad de red (Z:), una carpeta \\servidor\… o un disco que se saca.
    static bool FueraDeLaPc(string ruta)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(ruta)) is not { Length: > 0 } raiz || raiz.StartsWith(@"\\") || new DriveInfo(raiz).DriveType != DriveType.Fixed; }
        catch { return true; } // si no se puede saber, se pregunta
    }

    // «Z:» si la ruta está en una unidad; si es una carpeta de red (\\servidor\…), cómo llamarla.
    static string Unidad(string? ruta, string deRed) =>
        ruta is not null && Path.GetPathRoot(ruta) is { Length: > 0 } u && !u.StartsWith(@"\\") ? u.TrimEnd('\\') : deRed;

    // Separar en un disco de esta PC es lo que se pidió al soltar los PDF. Afuera (las carpetas de los clientes están en
    // Z:) nada se escribe sin preguntar: debajo de la tarjeta sale cuántos recibos son y dónde van, y se espera el clic.
    Task<bool> PedirPermiso(IReadOnlyList<string> rutas, IReadOnlyList<string> combinados)
    {
        var todas = rutas.Concat(combinados).ToList();
        if (todas.FirstOrDefault(FueraDeLaPc) is not { } afuera)
        {
            escribiendo = true;
            return Task.FromResult(true);
        }
        var donde = Unidad(afuera, "la carpeta de red");
        var carpeta = Path.GetDirectoryName(afuera)!;
        var madre = Path.GetFileName(Path.GetDirectoryName(carpeta));
        var otras = todas.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() - 1;
        // Los combinados aparte: no son recibos, y uno que ya existe se completa, no se crea.
        var recibos = rutas.Count == 0 ? "" : rutas.Count == 1 ? "1 recibo" : $"{rutas.Count} recibos";
        var juntos = combinados.Count == 0 ? "" : combinados.Count == 1 ? "el combinado" : $"{combinados.Count} combinados";
        permisoTitulo.Text = $"¿Guardo {string.Join(" y ", new[] { recibos, juntos }.Where(t => t.Length > 0))} en {donde}?";
        permisoTexto.Text = $"En la carpeta «{Path.GetFileName(carpeta)}»"
            + (string.IsNullOrEmpty(madre) ? "" : $", adentro de «{madre}»")
            + (otras == 0 ? "" : otras == 1 ? ", y en 1 carpeta más" : $", y en {otras} carpetas más")
            + (combinados.Any(File.Exists)
                ? ". No se borra nada: lo que ya existe queda como está, y al combinado que le falta alguien solo se le suman hojas al final."
                : ". No se reemplaza ningún archivo: lo que ya existe queda como está.");
        permisoTexto.ToolTip = carpeta;
        permisoSi.Content = $"Guardar en {donde}";
        permisoCaja.Visibility = Visibility.Visible;
        Estado("");
        Aparecer(permisoCaja, permisoCorrida, desdeY: -14);
        Encuadrar();
        // RunContinuationsAsynchronously: sin eso, lo que sigue a la respuesta (armar y escribir todos los PDF) corre
        // adentro del clic, en el hilo de la ventana, y la deja dura hasta que termina.
        return (permiso = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    void Responder(bool si)
    {
        if (permiso is not { } pregunta) return;
        permiso = null;
        escribiendo = si;
        pregunta.SetResult(si);
        // Con el sí sigue hacia las fichas, adonde van a llegar los recibos; con el no vuelve a la tarjeta, por donde vino.
        Resorte.Animar((permisoCaja, "y"), si ? 12 : -14, v => permisoCorrida.Y = v, rigidez: 260);
        Opacidad(permisoCaja, 0, alTerminar: () =>
        {
            if (permiso is null) permisoCaja.Visibility = Visibility.Collapsed;
        });
    }

    // ---- La tarjeta ----

    void Arrastre(DragEventArgs e)
    {
        // En la vista de clientes no hay tarjeta donde soltar.
        var sirve = !working && !enClientes && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = sirve ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (sirve) Anticipar(true);
    }

    // Con archivos encima la tarjeta se levanta, el borde se vuelve continuo y la carpeta se asoma. Si se van
    // sin soltar, todo vuelve a su lugar.
    void Anticipar(bool si)
    {
        if (si == anticipando) return;
        anticipando = si;
        Escalar(si ? 1.02 : 1);
        borde.StrokeDashArray = si ? null : new DoubleCollection([4, 3]);
        borde.SetResourceReference(Formas.Shape.StrokeProperty, si ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush");
        Resorte.Animar((icono, "escala"), si ? 1.12 : 1, v => iconoEscala.ScaleX = iconoEscala.ScaleY = v, rigidez: 420, rebote: 0.3);
        Resorte.Animar((icono, "giro"), si ? -7 : 0, v => iconoGiro.Angle = v, rigidez: 300, rebote: 0.3);
        Resorte.Animar((icono, "altura"), si ? -8 : 0, v => iconoAltura.Y = v, rigidez: 300, rebote: 0.3);
    }

    // Al soltar, la carpeta se traga los PDF: se aprieta y vuelve.
    void Tragar() =>
        Resorte.Animar((icono, "escala"), 1, v => iconoEscala.ScaleX = iconoEscala.ScaleY = v, velocidad: -5, rigidez: 420, rebote: 0.45);

    void Escalar(double v) =>
        Resorte.Animar((tarjeta, "escala"), v, x => tarjetaEscala.ScaleX = tarjetaEscala.ScaleY = x, rigidez: 500, rebote: 0.2);

    // Un problema sacude la tarjeta: un rebote corto que dice «esto no».
    void Sacudir() =>
        Resorte.Animar((tarjeta, "corrida"), 0, v => tarjetaCorrida.X = v, velocidad: 700, rigidez: 900, rebote: 0.85);

    // ---- Las fichas y los recibos que vuelan ----

    Ficha FichaDe(string ruta)
    {
        var carpeta = Path.GetFileName(Path.GetDirectoryName(ruta)) ?? "";
        if (porCarpeta.TryGetValue(carpeta, out var f)) return f;

        var glifo = new TextBlock { Text = Carpeta, FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 16, Margin = new(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Carpeta") };
        var nombre = new TextBlock { Text = carpeta, FontSize = 13, FontWeight = FontWeights.SemiBold, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var numero = new TextBlock { Text = "0", FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Margin = new(7, 1, 7, 1), Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0x2C, 0x00)) };
        var fondo = new Border
        {
            CornerRadius = new(16), Padding = new(12, 7, 8, 7), BorderThickness = new(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { glifo, nombre, new Border { CornerRadius = new(9), MinWidth = 22, Margin = new(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = (Brush)FindResource("Carpeta"), Child = numero } }
            }
        };
        fondo.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        fondo.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var escala = new ScaleTransform();
        var corrida = new TranslateTransform();
        var boton = new Button
        {
            Style = (Style)FindResource("Llano"), Content = fondo, Margin = new(0, 8, 8, 0), ToolTip = "Abrir la carpeta",
            RenderTransformOrigin = new(0.5, 0.5), RenderTransform = new TransformGroup { Children = { escala, corrida } }
        };
        AutomationProperties.SetName(boton, $"Abrir la carpeta {carpeta}");
        f = new Ficha { Boton = boton, Numero = numero, Carpeta = glifo, Escala = escala };
        var ficha = f;
        boton.Click += (_, _) => Abrir(ficha.Archivo);
        fichas.Children.Add(boton);
        porCarpeta[carpeta] = f;
        // Sale de la tarjeta: entra bajando desde ella.
        Aparecer(boton, corrida, desdeY: -14);
        Encuadrar();
        return f;
    }

    // Las fichas de la tanda anterior se achican hasta desaparecer: lo nuevo empieza limpio.
    void SacarFichas()
    {
        foreach (var f in porCarpeta.Values)
        {
            var b = f.Boton;
            Resorte.Animar((f, "latido"), 0.8, v => f.Escala.ScaleX = f.Escala.ScaleY = v, rigidez: 300);
            Opacidad(b, 0, alTerminar: () => fichas.Children.Remove(b));
        }
        porCarpeta.Clear();
    }

    // Cada recibo separado vuela de la tarjeta a la ficha de su carpeta, que lo cuenta al llegar. Si son
    // muchos, vuelan los primeros ocho y el resto solo se cuenta.
    void Separado(string carpetaYArchivo)
    {
        var f = FichaDe(carpetaYArchivo);
        if (Resorte.Quieto || volando >= 8)
        {
            f.Llegados++;
            Contar(f);
            return;
        }
        var espera = Math.Max(0, (proximoVuelo - DateTime.Now).TotalMilliseconds);
        proximoVuelo = DateTime.Now.AddMilliseconds(espera + 70);
        volando++;
        Luego((int)espera, () => Volar(f));
    }

    // El combinado llega último a la misma ficha, más grueso: son todos juntos. No suma al número, que cuenta recibos.
    void Juntado(string carpetaYArchivo)
    {
        var f = FichaDe(carpetaYArchivo);
        if (Resorte.Quieto || volando >= 8)
        {
            Resorte.Animar((f, "latido"), 1, v => f.Escala.ScaleX = f.Escala.ScaleY = v, velocidad: 2.5, rigidez: 500, rebote: 0.5);
            return;
        }
        var espera = Math.Max(0, (proximoVuelo - DateTime.Now).TotalMilliseconds);
        proximoVuelo = DateTime.Now.AddMilliseconds(espera + 70);
        volando++;
        Luego((int)espera, () => Volar(f, recibo: false));
    }

    void Volar(Ficha f, bool recibo = true)
    {
        var papel = recibo ? Papel() : Pila();
        capa.Children.Add(papel);
        var desde = icono.TranslatePoint(new Point(icono.ActualWidth / 2, icono.ActualHeight * 0.4), capa);
        var clave = (papel, "t");
        Resorte.Animar(clave, 1, t =>
        {
            // En arco, de la tarjeta a la carpeta de la ficha, achicándose. El destino se mide en cada cuadro
            // por si la ficha todavía se está acomodando.
            var hasta = f.Carpeta.TranslatePoint(new Point(f.Carpeta.ActualWidth / 2, f.Carpeta.ActualHeight / 2), capa);
            var cima = new Point((desde.X + hasta.X) / 2, Math.Min(desde.Y, hasta.Y) - 70);
            var u = 1 - t;
            var x = u * u * desde.X + 2 * u * t * cima.X + t * t * hasta.X;
            var y = u * u * desde.Y + 2 * u * t * cima.Y + t * t * hasta.Y;
            var e = 1 - 0.5 * t;
            papel.RenderTransform = new MatrixTransform(e, 0, 0, e, x - 9 * e, y - 11 * e);
        }, desde: 0, rigidez: 90, alLlegar: () =>
        {
            capa.Children.Remove(papel);
            Resorte.Olvidar(clave);
            volando--;
            if (recibo)
            {
                f.Llegados++;
                Contar(f);
            }
            // El destino acusa recibo: la ficha late.
            Resorte.Animar((f, "latido"), 1, v => f.Escala.ScaleX = f.Escala.ScaleY = v, velocidad: 2.5, rigidez: 500, rebote: 0.5);
        });
    }

    // Una hoja chiquita con la banda azul del PDF del ícono.
    Grid Papel() => new()
    {
        Width = 18, Height = 22,
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.25 },
        Children =
        {
            new Border { Background = Brushes.White, CornerRadius = new(3), BorderThickness = new(0.5), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)) },
            new Border { Height = 6, Margin = new(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom, Background = (Brush)FindResource("Pdf") }
        }
    };

    // El combinado: tres hojas apiladas, corridas apenas.
    Grid Pila()
    {
        var pila = new Grid { Width = 22, Height = 26 };
        for (var i = 2; i >= 0; i--)
        {
            var hoja = Papel();
            hoja.HorizontalAlignment = HorizontalAlignment.Left;
            hoja.VerticalAlignment = VerticalAlignment.Top;
            hoja.Margin = new(i * 2, i * 2, 0, 0);
            pila.Children.Add(hoja);
        }
        return pila;
    }

    // Los números ruedan.
    static void Contar(Ficha f) =>
        Resorte.Animar((f, "numero"), f.Llegados, v => f.Numero.Text = Math.Round(v).ToString(), rigidez: 120);

    static void Abrir(string? path)
    {
        if (path is not null && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    // ---- La revisión de la nómina ----

    double AnchoFila => Math.Max(200, fichas.ActualWidth - 14); // lugar para la barra de desplazamiento

    // Un renglón por empleado y abajo el letrero. Con una nómina nueva, los renglones entran de a uno con
    // un punto gris, cada marca se resuelve a su turno y al final los que faltan suben al principio. Con la
    // misma nómina (llegaron los recibos después), solo cambian las marcas que cambiaron, esos renglones
    // viajan a su grupo y el número del letrero rueda hacia abajo.
    void MostrarRevision(Nomina n, List<(Empleado Empleado, string? Recibo, bool PorNombre)> revision)
    {
        generacion++;
        var nueva = !ReferenceEquals(n, mostrada);
        if (nueva)
        {
            filas.Children.Clear();
            porCuil.Clear();
            mostrada = n;
            seccionTitulo.Text = $"NÓMINA {n.Mes:00}/{n.Anio} · {n.Empleados.Count} EMPLEADOS";
            cartelVisible = false;
            cartel.Opacity = 0;
        }

        double y = 0;
        int i = 0, turno = 0;
        foreach (var (e, recibo, porNombre) in revision)
        {
            var ok = recibo is not null;
            if (!porCuil.TryGetValue(e.Cuil, out var f))
            {
                f = NuevaFila(i);
                porCuil[e.Cuil] = f;
                filas.Children.Add(f.El);
                f.Nombre.Text = e.Nombre.Length > 0 ? e.Nombre : e.Cuil;
                f.El.Measure(new Size(AnchoFila, double.PositiveInfinity));
                var top = y;
                var el = f.El;
                Resorte.Animar((el, "top"), top, v => Canvas.SetTop(el, v), desde: top);
                y += f.El.DesiredSize.Height;
                Aparecer(f.El, f.Corrida, desdeY: 10, espera: Math.Min(i, 14) * 35);
            }
            // El que está se muestra como su archivo, que sale del texto del recibo y es exacto; el que falta,
            // como lo leyó el OCR en la nómina, que puede tener algún error: por eso lleva el CUIL.
            var nombre = ok ? Path.GetFileName(recibo!)[..^Recibos.Sufijo(n).Length] : e.Nombre.Length > 0 ? e.Nombre : e.Cuil;
            if (f.Ok != ok || f.PorNombre != porNombre || f.Recibo != recibo)
            {
                var fila = f;
                var cuil = e.Cuil;
                Despues((nueva ? 280 : 120) + Math.Min(turno++, 14) * 70, () => Resolver(fila, ok, porNombre, nombre, cuil, recibo));
            }
            i++;
        }
        if (nueva) filas.Height = y;
        Crecer(true);
        // Cuando terminaron de resolverse todas las marcas: cada renglón a su grupo y el letrero.
        Despues((nueva ? 280 : 120) + Math.Min(turno, 15) * 70 + 120, () =>
        {
            Ordenar();
            if (!cartelVisible)
            {
                cartelVisible = true;
                // Arranca en 1 y rueda hasta los que faltan: empezar en 0 mostraría un «Todos OK» de mentira.
                faltanDestino = Math.Min(1, porCuil.Values.Count(f => f.Ok == false));
                Resorte.Animar((cartel, "n"), faltanDestino, PintarCartel, desde: faltanDestino);
                Aparecer(cartel, cartelAltura, desdeY: 8);
            }
            Rodar();
        });
    }

    Fila NuevaFila(int orden)
    {
        var disco = new Formas.Ellipse { Width = 24, Height = 24 };
        disco.SetResourceReference(Formas.Shape.FillProperty, "ControlStrongStrokeColorDefaultBrush");
        var marca = new TextBlock { FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var marcaEscala = new ScaleTransform(0.45, 0.45);
        var nombre = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        // El CUIL va en su propio renglón: con el nombre largo de la nómina no se corta.
        var cuil = new TextBlock { FontSize = 12, Visibility = Visibility.Collapsed };
        cuil.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { nombre, cuil } };
        Grid.SetColumn(textos, 1);
        var corrida = new TranslateTransform();
        var linea = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children =
            {
                new Grid { Width = 24, Height = 24, Margin = new(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = marcaEscala, Children = { disco, marca } },
                textos
            }
        };
        // Como los renglones de Clientes: el renglón es un botón, y lo que abre aparece debajo, alineado con el nombre.
        var boton = new Button { Style = (Style)FindResource("Llano"), Content = linea, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsHitTestVisible = false, Focusable = false };
        var adentro = new StackPanel();
        var caja = new Border { Height = 0, ClipToBounds = true, Child = adentro, Margin = new(38, 0, 0, 0) };
        Resorte.Animar((caja, "alto"), 0, v => caja.Height = Math.Max(0, v), desde: 0);
        var el = new StackPanel { Width = AnchoFila, Margin = new(0, 4, 0, 4), RenderTransform = corrida, Children = { boton, caja } };
        var f = new Fila { El = el, Boton = boton, Caja = caja, Adentro = adentro, Disco = disco, Marca = marca, Nombre = nombre, Cuil = cuil, MarcaEscala = marcaEscala, Corrida = corrida, Orden = orden };
        boton.Click += async (_, _) => await VerRecibo(f);
        return f;
    }

    // Tocar a alguien que tiene su recibo lo abre ahí mismo con sus hojas, como en Clientes: así se ve si es el suyo (sobre
    // todo el encontrado por el nombre), y tocar una hoja la abre entera. Otro toque lo cierra.
    async Task VerRecibo(Fila f)
    {
        if (f.Recibo is not { } recibo) return;
        f.Abierta = !f.Abierta;
        if (f.Abierta && f.Cargado != recibo)
        {
            f.Cargado = recibo;
            f.Adentro.Children.Clear();
            f.Adentro.Children.Add(Nota("Cargando…"));
            AbrirCaja(f);
            List<UIElement> cargado;
            try
            {
                var n = await Task.Run(() => { using var d = UglyToad.PdfPig.PdfDocument.Open(recibo); return d.NumberOfPages; });
                var leyenda = f.PorNombre ? "Lo encontré por el nombre: fijate que sea el suyo. Tocá la hoja para verla entera." : null;
                // La hoja, del alto que entra en la lista sin perder el renglón de arriba; entera, con un toque.
                var maxAlto = Math.Clamp(desplazar.MaxHeight - 90, 140, 320);
                cargado = await Hojas([.. Enumerable.Range(1, n).Select(i => new Hallazgo(recibo, i, new Lectura("")))], leyenda, maxAlto)!();
            }
            catch (Exception ex) { cargado = [Nota($"No pude mostrarlo: {ex.Message}")]; }
            f.Adentro.Children.Clear();
            foreach (var c in cargado) f.Adentro.Children.Add(c);
        }
        AbrirCaja(f);
    }

    // La caja crece o se achica con resorte, y los renglones de abajo la siguen cuadro a cuadro. La ventana le hace lugar
    // antes de abrirla; al cerrarla, recién cuando terminó, para no cortarla en el camino.
    void AbrirCaja(Fila f)
    {
        f.Adentro.Measure(new Size(Math.Max(100, AnchoFila - 38), double.PositiveInfinity));
        var alto = f.Abierta ? f.Adentro.DesiredSize.Height : 0;
        if (alto > f.Caja.Height)
        {
            filas.Height += alto - f.Caja.Height;
            Crecer(true);
        }
        Resorte.Animar((f.Caja, "alto"), alto, v =>
        {
            f.Caja.Height = Math.Max(0, v);
            AcomodarFilas();
        }, rigidez: 260, rebote: 0.1, alLlegar: () =>
        {
            Crecer(true);
            // El renglón entero, con su nombre arriba: así se sabe de quién es la hoja que se abrió.
            if (f.Abierta) f.El.BringIntoView();
        });
    }

    // Cada renglón a su lugar según el alto que tiene ahora, de una: lo mueve la caja que se abre, que ya va con resorte.
    // El estado del resorte de cada uno queda ahí mismo, para que el próximo viaje salga desde donde está.
    void AcomodarFilas()
    {
        double y = 0;
        foreach (var f in EnOrden())
        {
            var el = f.El;
            var top = y;
            Canvas.SetTop(el, top);
            Resorte.Animar((el, "top"), top, v => Canvas.SetTop(el, v), desde: top);
            el.Measure(new Size(AnchoFila, double.PositiveInfinity));
            y += el.DesiredSize.Height;
        }
        filas.Height = y;
    }

    // Los que faltan arriba, después los que hay que mirar, cada grupo en el orden de la nómina.
    IEnumerable<Fila> EnOrden() => porCuil.Values.OrderBy(f => f.Ok != true ? 0 : f.PorNombre ? 1 : 2).ThenBy(f => f.Orden);

    // La marca pasa del punto gris a la tilde o a la cruz con un rebote chico. Debajo del nombre va el CUIL del que falta;
    // en el que se encontró por el nombre, que hay que mirarlo, dicho como en Clientes.
    void Resolver(Fila f, bool ok, bool porNombre, string nombre, string cuil, string? recibo)
    {
        f.Ok = ok;
        f.PorNombre = porNombre;
        // Con recibo, el renglón se puede tocar para verlo; si cambió el que se estaba mostrando, se cierra.
        f.Recibo = recibo;
        f.Boton.IsHitTestVisible = f.Boton.Focusable = recibo is not null;
        if (f.Abierta && f.Cargado != recibo)
        {
            f.Abierta = false;
            AbrirCaja(f);
        }
        f.Nombre.Text = nombre;
        f.Cuil.Text = porNombre ? "encontrado por el nombre: ilegible, revisar" : $"CUIL {cuil}";
        f.Cuil.Visibility = ok && !porNombre ? Visibility.Collapsed : Visibility.Visible;
        f.Disco.Fill = ok ? Verde : Rojo;
        f.Marca.Text = ok ? Tilde : Cruz;
        f.Boton.ToolTip = porNombre ? $"{nombre} (CUIL {cuil}): el CUIL no se leía bien y lo encontré por el nombre. Tocalo para ver el recibo"
            : ok ? $"{nombre} (CUIL {cuil}). Tocalo para ver el recibo" : $"{nombre} (CUIL {cuil})";
        AutomationProperties.SetName(f.Boton, $"{nombre}: {(porNombre ? "encontrado por el nombre, revisar" : ok ? "tiene su recibo" : "falta su recibo")}");
        AutomationProperties.SetHelpText(f.Boton, ok ? "Ver el recibo" : "");
        Resorte.Animar((f, "marca"), 1, v => f.MarcaEscala.ScaleX = f.MarcaEscala.ScaleY = v, desde: 0.45, velocidad: 3, rigidez: 420, rebote: 0.4);
        if (cartelVisible) Rodar();
    }

    // Los que faltan arriba, después los que hay que mirar, cada grupo en el orden de la nómina. Cada renglón viaja a su
    // lugar y los vecinos le hacen lugar.
    void Ordenar()
    {
        double y = 0;
        foreach (var f in EnOrden())
        {
            var el = f.El;
            el.Measure(new Size(AnchoFila, double.PositiveInfinity));
            Resorte.Animar((el, "top"), y, v => Canvas.SetTop(el, v), rigidez: 220, rebote: 0.12);
            y += el.DesiredSize.Height;
        }
        filas.Height = y;
        Crecer(true);
    }

    // El número del letrero rueda hacia los que faltan ahora; al llegar, el lector de pantalla lo anuncia.
    void Rodar()
    {
        faltanDestino = porCuil.Values.Count(f => f.Ok == false);
        Resorte.Animar((cartel, "n"), faltanDestino, PintarCartel, rigidez: 90, alLlegar: () =>
        {
            AutomationProperties.SetName(cartelTexto, cartelTexto.Text);
            (UIElementAutomationPeer.FromElement(cartelTexto) ?? UIElementAutomationPeer.CreatePeerForElement(cartelTexto))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    void PintarCartel(double valor)
    {
        var k = Math.Max(1, (int)Math.Round(valor));
        var resueltos = porCuil.Values.All(f => f.Ok is not null);
        var mirar = porCuil.Values.Count(f => f.Ok == true && f.PorNombre);
        // Sin ningún CUIL leído no hay nada que decir «OK». Si están todos pero alguno se encontró por el nombre, se dice.
        var (texto, icono, verde) = porCuil.Count == 0 ? ("No pude leer la nómina", Alerta, false)
            : faltanDestino == 0 && valor < 0.5 && resueltos
                ? (mirar == 0 ? "Todos OK" : mirar == 1 ? "Están todos · 1 para revisar" : $"Están todos · {mirar} para revisar", Tilde, true)
            : (k == 1 ? "Falta 1 recibo de sueldo" : $"Faltan {k} recibos de sueldo", Alerta, false);
        cartelTexto.Text = texto;
        if (cartelIcono.Text != icono)
        {
            cartelIcono.Text = icono;
            Resorte.Animar((cartelIcono, "escala"), 1, v => cartelIconoEscala.ScaleX = cartelIconoEscala.ScaleY = v, desde: 0.4, rigidez: 420, rebote: 0.4);
        }
        Opacidad(cartelVerde, verde ? 1 : 0);
    }

    // La sección crece y se achica con resorte.
    void Crecer(bool mostrar)
    {
        // Con la vista de clientes adelante, la sección se acomoda sin mover la ventana, que ahora es de la otra
        // vista: al volver, CambiarVista mide la de separar entera.
        if (enClientes)
        {
            seccion.Height = mostrar ? double.NaN : 0;
            return;
        }
        // ponytail-keep: lo que se agregó recién (la ficha de la carpeta, que aparece justo antes de la
        // revisión) tiene que estar acomodado antes de medir. Si no, ActualHeight no la cuenta, la ventana se
        // ubica 40 px más abajo de lo que debe y el letrero queda detrás de la barra de tareas.
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        // Lo de arriba de la sección se mide, no se deduce del alto de la ventana. Mientras la sección se anima, la ventana
        // está en SizeToContent.Manual y no crece con lo que aparece: los avisos que llegan al final de la tanda no entraban
        // en ActualHeight, la lista quedaba más alta de lo que cabía y el letrero se cortaba detrás de la barra de tareas
        // (en 1680×1050, con los siete avisos de las iniciales de un escaneo, el 21/11/2026).
        todo.Measure(new Size(todo.ActualWidth + todo.Margin.Left + todo.Margin.Right, double.PositiveInfinity));
        var marco = ActualHeight - ((FrameworkElement)Content).ActualHeight;
        var resto = marco + todo.DesiredSize.Height - seccion.ActualHeight;
        // ponytail-keep: el alto se suma a mano. Medir seccionContenido entero devolvía el alto que tenía la
        // lista en la pasada de diseño anterior (0 la primera vez, 288 cuando ya medía 387): los contenedores
        // guardan su medida hasta la próxima pasada, y el letrero quedaba afuera de la ventana.
        seccionTitulo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var fijo = seccionTitulo.DesiredSize.Height + cartel.Margin.Top + cartel.Height;
        // Que no se salga de la pantalla: si aun arriba de todo no entra, la lista se acorta y se desplaza.
        // Hasta 14 renglones de una línea; con más aparece la barra de desplazamiento.
        desplazar.MaxHeight = Math.Max(120, Math.Min(14 * 32, area.Height - resto - fijo));
        var alto = mostrar ? fijo + Math.Min(filas.Height, desplazar.MaxHeight) : 0;
        // ponytail-keep: la ventana no sigue a la sección cuadro a cuadro: toma su alto final de una vez y el
        // contenido crece adentro. Al agrandarse en cada cuadro, Windows dejaba ver una franja blanca de
        // 5 px en el borde de abajo. Al achicarse, la ventana se ajusta recién al final.
        SizeToContent = SizeToContent.Manual;
        if (mostrar) Height = resto + alto;
        Resorte.Animar((seccion, "alto"), alto, v => seccion.Height = Math.Max(0, v), desde: seccion.ActualHeight,
            rigidez: 170, rebote: 0.08, alLlegar: () => SizeToContent = SizeToContent.Height);
        // Si crece más allá del borde de abajo, la ventana sube lo que haga falta.
        var arriba = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - resto - alto));
        if (Math.Abs(arriba - Top) > 0.5)
            Resorte.Animar((this, "arriba"), arriba, v => Top = v, desde: Top, rigidez: 170, rebote: 0.08);
    }

    void OcultarRevision()
    {
        if (mostrada is null) return;
        generacion++;
        mostrada = null;
        Crecer(false);
    }

    // ---- El botón de actualizar ----

    // Se busca callado: al abrir y después de «Revisar de nuevo». El botón de arriba aparece recién si hay una versión
    // nueva: entra con el anillo de la descarga y, bajada y verificada, se estira hasta decir «Reiniciar para actualizar».
    async Task BuscarActualizacion()
    {
        if (buscando || nuevoExe is not null) return;
        buscando = true;
        ultimaBusqueda = DateTime.Now;
        try
        {
            if (await Actualizador.Buscar() is not { } nueva) return;
            MostrarActualizar();
            var ultimo = -1;
            nuevoExe = await Actualizador.Descargar(nueva, new Progress<int>(p =>
            {
                if (p == ultimo) return; // el progreso llega cientos de veces con el mismo porcentaje
                ultimo = p;
                Anillo(p / 100.0);
            }));
            Estirar();
            actualizar.ToolTip = $"La versión {nueva.Version} ya está descargada";
        }
        // shortcut: sin internet, o si la carpeta del programa no deja escribir (Archivos de programa), no avisa nada y la
        // versión nueva no se baja. Si algún día la instalan ahí, que el botón aparezca y abra la página de descarga.
        catch { }
        finally
        {
            buscando = false;
            if (nuevoExe is null) EsconderActualizar();
        }
    }

    // Lo que no viene de ningún lado entra con lo que lo trae: baja un poco mientras aparece.
    void MostrarActualizar()
    {
        actualizar.Visibility = Visibility.Visible;
        Aparecer(actualizar, actualizarCorrida, -8);
    }

    // Si la descarga no llega, se va por donde vino.
    void EsconderActualizar()
    {
        if (actualizar.Visibility != Visibility.Visible) return;
        Resorte.Animar((actualizar, "y"), -8, v => actualizarCorrida.Y = v, rigidez: 260);
        Opacidad(actualizar, 0, alTerminar: () =>
        {
            actualizar.Visibility = Visibility.Hidden;
            anillo.Opacity = 0;
        });
    }

    // La descarga llena un anillo alrededor de la flecha.
    void Anillo(double fraccion)
    {
        // StrokeDashArray se mide en grosores de trazo: la circunferencia completa es π·(28 − 2)/2.
        var vuelta = Math.PI * 13;
        anillo.StrokeDashArray = new DoubleCollection([vuelta * fraccion, vuelta]);
        if (anillo.Opacity < 1) Opacidad(anillo, 1);
    }

    // Descargada la versión nueva, la flecha se estira ahí mismo hasta decir «Reiniciar para actualizar».
    void Estirar()
    {
        AutomationProperties.SetName(actualizar, "Reiniciar para actualizar");
        actualizarTexto.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Con las tres pestañas, la frase entera puede no entrar al lado: entonces dice lo justo.
        if (actualizarTexto.DesiredSize.Width > ((FrameworkElement)actualizar.Parent).ActualWidth - ((FrameworkElement)selector.Parent).ActualWidth - 12)
        {
            actualizarTexto.Text = "Reiniciar";
            actualizarTexto.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        Resorte.Animar((actualizarCaja, "ancho"), actualizarTexto.DesiredSize.Width, v => actualizarCaja.Width = v, desde: 32, rigidez: 260, rebote: 0.15);
        Opacidad(actualizarAcento, 1);
        Opacidad(actualizarTexto, 1);
        Opacidad(anillo, 0);
        flecha.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
    }

    // Los avisos de la flecha salen de la flecha misma y vuelven a ella.
    void Burbuja(string texto)
    {
        var letra = new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        letra.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var escala = new ScaleTransform(0.6, 0.6);
        var b = new Border
        {
            CornerRadius = new(8), Padding = new(12, 8, 12, 8), BorderThickness = new(1), MaxWidth = 300, Child = letra,
            RenderTransformOrigin = new(1, 0), RenderTransform = escala, Opacity = 0,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.18 }
        };
        b.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        capa.Children.Add(b);
        b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var esquina = actualizar.TranslatePoint(new Point(actualizar.ActualWidth, actualizar.ActualHeight + 6), capa);
        Canvas.SetLeft(b, esquina.X - b.DesiredSize.Width);
        Canvas.SetTop(b, esquina.Y);
        Resorte.Animar((b, "escala"), 1, v => escala.ScaleX = escala.ScaleY = v, desde: 0.6, rigidez: 380, rebote: 0.2);
        Opacidad(b, 1, desde: 0);
        Luego(2600, () =>
        {
            Resorte.Animar((b, "escala"), 0.6, v => escala.ScaleX = escala.ScaleY = v, rigidez: 380);
            Opacidad(b, 0, alTerminar: () =>
            {
                capa.Children.Remove(b);
                Resorte.Olvidar((b, "escala"));
                Resorte.Olvidar((b, "opacidad"));
            });
        });
    }

    void Reiniciar()
    {
        // Ni una separación ni un guardado se cortan a la mitad: quedaría un archivo a medio escribir.
        if (working || guardando)
        {
            Burbuja(guardando ? "Esperá a que termine de guardar" : "Esperá a que termine de separar");
            return;
        }
        try
        {
            Actualizador.Reemplazar(nuevoExe!);
            Close();
        }
        catch
        {
            Burbuja("No pude reiniciar: cerrá y abrí el programa");
        }
    }

    // ---- Movimiento ----

    // Lo que no viene de ningún lado entra con lo que lo trae: sube o baja un poco mientras aparece.
    void Aparecer(FrameworkElement el, TranslateTransform corrida, double desdeY, int espera = 0)
    {
        el.Opacity = 0;
        corrida.Y = Resorte.Quieto ? 0 : desdeY;
        Luego(espera, () =>
        {
            Opacidad(el, 1, desde: 0);
            Resorte.Animar((el, "y"), 0, v => corrida.Y = v, desde: desdeY, rigidez: 260, rebote: 0.2);
        });
    }

    // Con «Efectos de animación» apagado, lo que entra o sale se funde en vez de deslizarse.
    static void Opacidad(UIElement el, double destino, double? desde = null, Action? alTerminar = null)
    {
        if (Resorte.Quieto)
        {
            var a = new DoubleAnimation(destino, TimeSpan.FromMilliseconds(160));
            if (desde is double d) a.From = d;
            if (alTerminar is not null) a.Completed += (_, _) => alTerminar();
            el.BeginAnimation(OpacityProperty, a);
            return;
        }
        el.BeginAnimation(OpacityProperty, null);
        Resorte.Animar((el, "opacidad"), destino, v => el.Opacity = Math.Clamp(v, 0, 1), desde: desde, rigidez: 240, alLlegar: alTerminar);
    }

    static void Luego(int ms, Action accion)
    {
        if (ms <= 0) { accion(); return; }
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            accion();
        };
        t.Start();
    }

    // Como Luego, pero se anula si mientras tanto empezó otra revisión: la nueva rehace todo su estado.
    void Despues(int ms, Action accion)
    {
        var g = generacion;
        Luego(Resorte.Quieto ? 0 : ms, () =>
        {
            if (g == generacion) accion();
        });
    }

    // ---- Carpetas ----

    // Donde pueden estar los recibos ya separados: la carpeta de la nómina y las que tiene adentro (ahí
    // cae la del empleador), y las carpetas donde la app exportó hace poco, aunque se haya cerrado en el medio.
    static IEnumerable<string> CarpetasParaRevisar(Nomina n)
    {
        var carpeta = Path.GetDirectoryName(n.Ruta)!;
        string[] adentro = [];
        try { adentro = Directory.GetDirectories(carpeta); } catch { }
        return [carpeta, .. adentro, .. CarpetasRecientes()];
    }

    static List<string> CarpetasRecientes()
    {
        try { return File.ReadAllLines(CarpetasFile).Where(l => l.Length > 0).ToList(); }
        catch { return []; } // la primera vez no hay lista
    }

    static void RecordarCarpetas(List<string> creados)
    {
        if (creados.Count == 0) return;
        try
        {
            var todas = creados.Select(Path.GetDirectoryName).OfType<string>().Concat(CarpetasRecientes())
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(CarpetasFile)!);
            File.WriteAllLines(CarpetasFile, todas);
        }
        catch { } // recordar es un extra: sin la lista igual se revisan las carpetas al lado de la nómina
    }

    // "Enviar a → Organizador de Recibos" en el Explorador. Se rehace en cada inicio por si el .exe cambió de lugar.
    static void AddToSendTo()
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), AppName + ".lnk"));
            link.TargetPath = Environment.ProcessPath;
            link.Save();
        }
        catch { } // si una política bloquea WScript, solo falta el atajo
    }
}
