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
    const string Flecha = "", Tilde = "", Cruz = "", Alerta = "", Carpeta = "";

    readonly string[] startupFiles;
    readonly Ocr? ocr = CrearOcr();
    bool working, buscando, anticipando, cartelVisible;
    string? nuevoExe; // descargado y verificado: reemplaza al actual al reiniciar
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
        public required Grid El;
        public required Formas.Ellipse Disco;
        public required TextBlock Marca, Nombre, Cuil;
        public required ScaleTransform MarcaEscala;
        public required TranslateTransform Corrida;
        public required int Orden;
        public bool? Ok; // null: todavía sin revisar, un punto gris
    }

    public Ventana(string[] files)
    {
        startupFiles = files;
        InitializeComponent();

        actualizar.Click += async (_, _) =>
        {
            if (nuevoExe is null) await BuscarActualizacion(manual: true);
            else Reiniciar();
        };
        tarjeta.Click += async (_, _) => await Pick();
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
            if (working || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
            Tragar();
            _ = ProcessAll(dropped);
        };
        ContentRendered += async (_, _) =>
        {
            AddToSendTo();
            actualizar.ToolTip = $"Versión {Actualizador.Actual} · buscar actualización";
            _ = BuscarActualizacion(manual: false);
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
        var r = new Resultado([], []);
        try
        {
            // El OCR de Windows es asíncrono, pero el armado de los PDF no: afuera del hilo de la ventana.
            // Invoke y no BeginInvoke: un aviso en cola llegaría después de mostrar el resultado y se perdería.
            r = await Task.Run(() => Recibos.Procesar(files, ocr, msg => Dispatcher.Invoke(() =>
            {
                if (msg.StartsWith('⚠')) problems.Add(msg[2..]);
                else if (msg.StartsWith('✓')) Separado(msg[2..]);
                else Estado(msg);
            })));
            RecordarCarpetas(r.Creados);
            foreach (var f in r.Creados) FichaDe(f).Archivo ??= f;
            if (r.Nominas.Count > 0) nomina = r.Nominas[^1];
            // Se revisa si llegó una nómina, o si llegaron recibos del mes de la que ya estaba cargada.
            if (nomina is { } n && (r.Nominas.Count > 0 || r.Creados.Any(c => c.EndsWith(Recibos.Sufijo(n), StringComparison.OrdinalIgnoreCase))))
            {
                Estado("Revisando la nómina…");
                var revision = await Task.Run(() => Recibos.Revisar(n, CarpetasParaRevisar(n)));
                // Sin recibos nuevos, la ficha es la de la carpeta donde están los de la nómina.
                if (r.Creados.Count == 0)
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
            working = false;
            Estado("");
        }

        if (r.Creados.Count == 0 && r.Nominas.Count == 0 && problems.Count == 0) problems.Add("No separé ningún recibo");
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
        if (texto.Length == 0) return;
        Opacidad(avisos, 1, desde: 0);
        Encuadrar();
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

    // ---- La tarjeta ----

    void Arrastre(DragEventArgs e)
    {
        var sirve = !working && e.Data.GetDataPresent(DataFormats.FileDrop);
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

    void Volar(Ficha f)
    {
        var papel = Papel();
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
            f.Llegados++;
            Contar(f);
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
    void MostrarRevision(Nomina n, List<(Empleado Empleado, string? Recibo)> revision)
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
        foreach (var (e, recibo) in revision)
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
            if (f.Ok != ok)
            {
                var fila = f;
                var cuil = e.Cuil;
                Despues((nueva ? 280 : 120) + Math.Min(turno++, 14) * 70, () => Resolver(fila, ok, nombre, cuil));
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
                // Arranca en 1 y rueda hasta los que faltan: empezar en 0 mostraría un «TODOS OK» de mentira.
                faltanDestino = Math.Min(1, porCuil.Values.Count(f => f.Ok == false));
                Resorte.Animar((cartel, "n"), faltanDestino, PintarCartel, desde: faltanDestino);
                Aparecer(cartel, cartelAltura, desdeY: 24);
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
        var el = new Grid
        {
            Width = AnchoFila, Margin = new(0, 4, 0, 4), RenderTransform = corrida,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children =
            {
                new Grid { Width = 24, Height = 24, Margin = new(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = marcaEscala, Children = { disco, marca } },
                textos
            }
        };
        return new Fila { El = el, Disco = disco, Marca = marca, Nombre = nombre, Cuil = cuil, MarcaEscala = marcaEscala, Corrida = corrida, Orden = orden };
    }

    // La marca pasa del punto gris a la tilde o a la cruz con un rebote chico.
    void Resolver(Fila f, bool ok, string nombre, string cuil)
    {
        f.Ok = ok;
        f.Nombre.Text = nombre;
        f.Cuil.Text = $"CUIL {cuil}";
        f.Cuil.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        f.Disco.Fill = ok ? Verde : Rojo;
        f.Marca.Text = ok ? Tilde : Cruz;
        f.El.ToolTip = $"{nombre} (CUIL {cuil})";
        AutomationProperties.SetName(f.El, $"{nombre}: {(ok ? "tiene su recibo" : "falta su recibo")}");
        Resorte.Animar((f, "marca"), 1, v => f.MarcaEscala.ScaleX = f.MarcaEscala.ScaleY = v, desde: 0.45, velocidad: 3, rigidez: 420, rebote: 0.4);
        if (cartelVisible) Rodar();
    }

    // Los que faltan arriba, cada grupo en el orden de la nómina. Cada renglón viaja a su lugar y los vecinos
    // le hacen lugar.
    void Ordenar()
    {
        double y = 0;
        foreach (var f in porCuil.Values.OrderBy(f => f.Ok == true).ThenBy(f => f.Orden))
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
        // Sin ningún CUIL leído no hay nada que decir «OK».
        var (texto, icono, verde) = porCuil.Count == 0 ? ("No pude leer la nómina", Alerta, false)
            : faltanDestino == 0 && valor < 0.5 && resueltos ? ("TODOS OK", Tilde, true)
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
        // ponytail-keep: lo que se agregó recién (la ficha de la carpeta, que aparece justo antes de la
        // revisión) tiene que estar acomodado antes de medir. Si no, ActualHeight no la cuenta, la ventana se
        // ubica 40 px más abajo de lo que debe y el letrero queda detrás de la barra de tareas.
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        var resto = ActualHeight - seccion.ActualHeight;
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

    // ---- La flecha de actualizar ----

    async Task BuscarActualizacion(bool manual)
    {
        if (buscando) return;
        buscando = true;
        Girar(true);
        try
        {
            var nueva = await Actualizador.Buscar();
            if (nueva is null)
            {
                if (manual)
                {
                    Girar(false);
                    Tildar();
                    Burbuja($"Ya tenés la última versión ({Actualizador.Actual})");
                }
                return;
            }
            Girar(false);
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
        catch (UnauthorizedAccessException) when (manual)
        {
            // La carpeta del programa no deja escribir (Archivos de programa, por ejemplo): a mano.
            Process.Start(new ProcessStartInfo(Actualizador.Pagina) { UseShellExecute = true });
            Burbuja("Bajá la versión nueva de la página que se abrió");
        }
        catch (Exception) when (manual)
        {
            Burbuja("No pude buscar actualizaciones");
        }
        catch { } // la búsqueda al abrir es silenciosa: sin internet, no molesta
        finally
        {
            buscando = false;
            Girar(false);
            if (nuevoExe is null) Opacidad(anillo, 0);
        }
    }

    // Mientras busca, la flecha gira: lo único lineal, porque gira sin parar. Al terminar no frena en seco:
    // completa la vuelta con resorte.
    void Girar(bool si)
    {
        if (si && !Resorte.Quieto)
        {
            giro.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            return;
        }
        var angulo = giro.Angle;
        giro.BeginAnimation(RotateTransform.AngleProperty, null);
        Resorte.Animar((giro, "angulo"), Math.Ceiling(angulo / 360) * 360, v => giro.Angle = v, desde: angulo, rigidez: 120);
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
        Resorte.Animar((actualizarCaja, "ancho"), actualizarTexto.DesiredSize.Width, v => actualizarCaja.Width = v, desde: 32, rigidez: 260, rebote: 0.15);
        Opacidad(actualizarAcento, 1);
        Opacidad(actualizarTexto, 1);
        Opacidad(anillo, 0);
        flecha.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
    }

    // «Ya tenés la última»: la flecha se vuelve tilde un momento y vuelve a ser flecha.
    void Tildar()
    {
        void Cambiar(string glifo)
        {
            flecha.Text = glifo;
            Resorte.Animar((flecha, "escala"), 1, v => flechaEscala.ScaleX = flechaEscala.ScaleY = v, desde: 0.4, rigidez: 420, rebote: 0.4);
        }
        Cambiar(Tilde);
        Luego(1800, () => Cambiar(Flecha));
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
        if (working) return; // no cortar una separación a la mitad
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
