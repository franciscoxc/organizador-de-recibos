using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Formas = System.Windows.Shapes;

// La revisión de todos los clientes de un mes: un renglón por cliente que se resuelve a medida que se lee, y lo
// que haya que guardar en la carpeta de los clientes, siempre con un segundo clic.
public partial class Ventana
{
    static readonly string RaizFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "raiz.txt");
    static readonly string SinInicialesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "sin iniciales");
    // Una memoria por mes: con una sola para todo, el archivo crecía sin fin (cada mes sumaba el texto de todos sus PDF)
    // y se reescribía entero después de cada cliente. El número cambia cuando lo guardado antes ya no sirve: 3 fue
    // PP-OCRv6, y desde el 4 cada recibo guarda también cómo nombra al empleador.
    static string MemoriaFile(int mes, int anio) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, $"lecturas-4-{anio}-{mes:00}.json");
    // La de la versión 1.1.0, una sola para todos los meses: de ahí salen, la primera vez, las lecturas de cada mes.
    static readonly string MemoriaAnterior = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "lecturas-3.json");
    // Rojo: falta el recibo. Amarillo (el de la carpeta del ícono, con la marca oscura para que se lea): está el recibo
    // y falta la firma. Azul: todavía se está leyendo. Violeta: lo firmó el estudio con las iniciales.
    static readonly SolidColorBrush Amarillo = new(Color.FromRgb(0xEB, 0xB4, 0x1C)), TintaOscura = new(Color.FromRgb(0x3D, 0x2C, 0x00)),
        Azul = new(Color.FromRgb(0x2B, 0x6C, 0xB0)), Violeta = new(Color.FromRgb(0x87, 0x64, 0xB8));
    const string Reloj = "\uE823", Pregunta = "\uE897", Raya = "\uE738", Lapiz = "\uE70F", Disquete = "\uE74E";
    const double AltoRenglon = 38;

    bool enClientes, clientesVistos, ordenarPorGrupo, guardando, planeando, lectorCreado;
    // Lo que muestra el panel de la derecha, y el cliente elegido cuando muestra uno.
    enum Muestra { Nada, Cliente, Guardar }
    Muestra muestra;
    FilaCliente? elegida;
    int mesElegido, anioElegido, generacionClientes, esperaMes, faltanClientes;
    CancellationTokenSource? corteClientes;
    TextBlock? mesActual;
    Memoria? memoria;
    (int Mes, int Anio) memoriaDe; // el mes de esa memoria
    LeerImagen? lector, lectorAFondo; // la lectura rápida, para todo, y a fondo, para las hojas difíciles
    string? raizElegida, raizActual;
    readonly Dictionary<string, FilaCliente> porCliente = new(StringComparer.OrdinalIgnoreCase);
    // «carpeta del cliente|CUIL» de los que el usuario eligió para estamparles las iniciales. Nada viene marcado.
    readonly HashSet<string> elegidos = [];
    // Lo que se guarda: el de todos los clientes (el botón de abajo) y el del cliente elegido (su panel).
    List<(FilaCliente Fila, Escritura E, TextBlock Marca, TextBlock Texto)> plan = [], planCliente = [];
    StackPanel? paraGuardar; // la sección «Para guardar» del cliente que muestra el panel
    int turnoPlanCliente;
    // El renglón de cada empleado del panel, por CUIL: ahí van los íconos de lo que se le va a guardar.
    readonly Dictionary<string, Ranura> ranuras = new();
    // El ícono «se guarda derecho» de cada uno: al recalcular el plan (por tildar iniciales) se reusa y no vuelve a entrar.
    readonly Dictionary<string, (FrameworkElement Caja, TextBlock Glifo)> iconosPlan = new();

    sealed record Ranura(StackPanel Acciones, TextBlock Debajo, Border Despliegue, string Nombre, TextBlock? Lupa, CheckBox? Casilla);
    // ponytail-keep: la columna de la derecha de los renglones de empleados mide lo mismo en todos (SharedSizeGroup,
    // con panelLista como alcance). Si cada renglón midiera la suya, el ícono de «se guarda derecho» quedaba contra el
    // borde y las casillas de «Firmar iniciales», más adentro: no se alineaban. Así arrancan todos en la misma línea.
    const string Derecha = "Derecha";

    sealed class FilaCliente
    {
        public required string Ruta;
        public required Grid El;
        public required Border Tarjeta;
        public required Button Boton;
        public required Formas.Ellipse Disco;
        public required TextBlock Marca, Detalle, Chevron;
        public required ScaleTransform MarcaEscala;
        public required FrameworkElement Ruedita;
        public required RotateTransform RueditaGiro;
        public int Dificiles; // hojas difíciles que todavía no se leyeron a fondo
        public required TranslateTransform Corrida, ChevronCorrida;
        public required int Orden;
        public Revision? Revision;
        public int Grupo = 9; // todavía sin leer: al final
    }

    // Desde el constructor, después de InitializeComponent.
    void IniciarClientes()
    {
        // Lo habitual es revisar el mes que se acaba de liquidar.
        var antes = DateTime.Today.AddMonths(-1);
        (mesElegido, anioElegido) = (antes.Month, antes.Year);
        mesActual = NuevoMes(TextoMes());
        mesMarco.Children.Add(mesActual);

        verSeparar.Click += (_, _) => CambiarVista(false);
        verClientes.Click += (_, _) => CambiarVista(true);
        mesAntes.Click += (_, _) => CambiarMes(-1);
        mesDespues.Click += (_, _) => CambiarMes(1);
        // Un solo botón para traer lo nuevo: relee las carpetas y después busca si hay una versión nueva del programa.
        // Después y no a la vez, para que la descarga no le saque red a la lectura de Z:.
        reRevisar.ToolTip = $"Revisar de nuevo · versión {Actualizador.Actual}";
        reRevisar.Click += async (_, _) =>
        {
            if (guardando) return; // la lista que se está guardando es la de esta revisión
            // La flecha da una vuelta: dice «de nuevo».
            Resorte.Animar((reRevisarGiro, "giro"), Math.Round(reRevisarGiro.Angle / 360) * 360 + 360, v => reRevisarGiro.Angle = v, rigidez: 140, rebote: 0.1);
            var antes = generacionClientes;
            await RevisarClientes(0);
            // Si mientras tanto empezó otra revisión (cambió el mes), la búsqueda queda para la próxima vuelta.
            if (generacionClientes == antes + 1 && DateTime.Now - ultimaBusqueda > TimeSpan.FromHours(1)) await BuscarActualizacion();
        };
        elegirRaiz.Click += (_, _) => ElegirRaiz();
        cambiarRaiz.Click += (_, _) => ElegirRaiz();
        // La carpetita hace el gesto de la carpeta grande de la tarjeta cuando le llegan archivos: se inclina y se asoma.
        cambiarRaiz.MouseEnter += (_, _) => AsomarCarpeta(true);
        cambiarRaiz.MouseLeave += (_, _) => AsomarCarpeta(false);
        guardar.Click += async (_, _) =>
        {
            if (muestra == Muestra.Guardar) CerrarPanel();
            else await AbrirGuardar();
        };
        guardarNo.Click += (_, _) => CerrarPanel();
        guardarSi.Click += async (_, _) => await Guardar(plan, deUnCliente: false);
        guardarCliente.Click += async (_, _) => await Guardar(planCliente, deUnCliente: true);
        panelCerrar.Click += (_, _) => CerrarPanel();
        // La cruz gira al pasar por encima: dice «cerrar» antes del clic.
        panelCerrar.MouseEnter += (_, _) => Resorte.Animar((panelCerrarGiro, "giro"), 90, v => panelCerrarGiro.Angle = v, rigidez: 300, rebote: 0.2);
        panelCerrar.MouseLeave += (_, _) => Resorte.Animar((panelCerrarGiro, "giro"), 0, v => panelCerrarGiro.Angle = v, rigidez: 300, rebote: 0.2);
        abrirCarpeta.Click += (_, _) =>
        {
            if (elegida?.Revision is { } r && (r.Firmados ?? r.CarpetaMes) is { } carpeta)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{carpeta}\"") { UseShellExecute = true });
        };
        // Arrastrar un borde lo hace Windows entre WM_ENTERSIZEMOVE y WM_EXITSIZEMOVE: así se distingue del tamaño que
        // pone la app al cambiar de vista o abrir el panel.
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(
            (IntPtr ventana, int msg, IntPtr w, IntPtr l, ref bool manejado) =>
            {
                if (msg == 0x0231) estirando = true;  // WM_ENTERSIZEMOVE
                if (msg == 0x0232) estirando = false; // WM_EXITSIZEMOVE
                return IntPtr.Zero;
            });
        // Los 400 del XAML cuentan el borde: se le suma, para que adentro queden los del diseño (ver Marco).
        SourceInitialized += (_, _) => Width = AnchoSeparar + Marco();
        SizeChanged += (_, e) => Estirado(e);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Responder(false); // la pregunta de guardar de Separar, si está abierta
            if (muestra != Muestra.Nada && !guardando) CerrarPanel();
        };
        // Al apretar, cada control cede un poco. Las flechas del mes, además, empiezan a llevar el nombre hacia el
        // lado por donde se va a ir.
        Hundir(mesAntes, 0.94, si => Insinuar(si ? 8 : 0));
        Hundir(mesDespues, 0.94, si => Insinuar(si ? -8 : 0));
        Hundir(reRevisar, 0.94);
        Hundir(cambiarRaiz, 0.94);
        Hundir(guardar, 0.98, objetivo: guardarCaja);
        clientesDesplazar.ScrollChanged += (_, _) => Bordes();
        // Mientras no hay revisión terminada, el botón se apaga: encendido parecería que se puede tocar.
        guardar.IsEnabledChanged += (_, _) => Opacidad(guardarCaja, guardar.IsEnabled ? 1 : 0.4);
        guardarCaja.Opacity = 0.4;

        estampar.IsChecked = !File.Exists(SinInicialesFile);
        estampar.Click += (_, _) =>
        {
            try
            {
                if (estampar.IsChecked == true) File.Delete(SinInicialesFile);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SinInicialesFile)!);
                    File.WriteAllText(SinInicialesFile, "");
                }
            }
            catch { } // si no se puede recordar, vale para esta vez
            DetalleTarjeta();
        };
        DetalleTarjeta();

        // Los resortes arrancan donde está cada cosa: así el primer cambio ya sale desde ahí.
        Loaded += (_, _) =>
        {
            Resorte.Animar((pastilla, "x"), 0, v => pastillaCorrida.X = v, desde: 0);
            Resorte.Animar((pastilla, "ancho"), verSeparar.ActualWidth, v => pastilla.Width = Math.Max(0, v), desde: verSeparar.ActualWidth);
            Resorte.Animar((vistaSeparar, "x"), 0, v => separarCorrida.X = v, desde: 0);
            Resorte.Animar((panel, "ancho"), 0, v => panel.Width = Math.Max(0, v), desde: 0);
            Resorte.Animar((panelCaja, "x"), 0, v => panelCorrida.X = v, desde: 0);
            Resorte.Animar((cambiarRaiz, "giro"), 0, v => cambiarRaizGiro.Angle = v, desde: 0);
            Resorte.Animar((cambiarRaiz, "altura"), 0, v => cambiarRaizAltura.Y = v, desde: 0);
        };
    }

    // Lo que se aprieta se hunde y vuelve al soltar, o al irse sin soltar. «insinuar» es el gesto de lo que va a pasar.
    void Hundir(Button boton, double cuanto, Action<bool>? insinuar = null, FrameworkElement? objetivo = null)
    {
        var el = objetivo ?? boton;
        var escala = new ScaleTransform();
        el.RenderTransformOrigin = new(0.5, 0.5);
        el.RenderTransform = escala;
        void Ir(double v, bool apretado)
        {
            Resorte.Animar((escala, "hundir"), v, x => escala.ScaleX = escala.ScaleY = x, rigidez: 520, rebote: 0.25);
            insinuar?.Invoke(apretado);
        }
        boton.PreviewMouseLeftButtonDown += (_, _) => Ir(cuanto, true);
        boton.PreviewMouseLeftButtonUp += (_, _) => Ir(1, false);
        boton.MouseLeave += (_, _) => Ir(1, false);
    }

    // Al apretar una flecha, el mes ya empieza a irse hacia ese lado; si se suelta afuera, vuelve.
    void Insinuar(double x)
    {
        if (mesActual is not { } m || guardando) return;
        var t = (TranslateTransform)m.RenderTransform;
        Resorte.Animar((m, "x"), x, v => t.X = v, rigidez: 420, rebote: 0.2);
    }

    // Una barra fija toma sombra recién cuando la lista pasa por debajo de ella.
    void Bordes()
    {
        Opacidad(sombraArriba, clientesDesplazar.VerticalOffset > 1 ? 1 : 0);
        Opacidad(sombraAbajo, clientesDesplazar.VerticalOffset < clientesDesplazar.ScrollableHeight - 1 ? 1 : 0);
    }

    bool iconoLucido;
    int esperaIcono;

    // Con la lista vacía mientras busca, la carpeta del ícono. La primera vez se forma y hojea, y queda quieta;
    // las siguientes solo aparece. Si la búsqueda es rápida (todo en la memoria) no llega a asomar: sería un destello.
    void MostrarIcono(int espera = 400)
    {
        var turno = ++esperaIcono;
        Luego(espera, () =>
        {
            if (turno != esperaIcono || porCliente.Count > 0) return;
            Opacidad(clientesIcono, 1);
            if (iconoLucido)
            {
                Resorte.Animar((clientesIcono, "escala"), 1, v => clientesIconoEscala.ScaleX = clientesIconoEscala.ScaleY = v, rigidez: 300, rebote: 0.2);
                return;
            }
            iconoLucido = true;
            Resorte.Animar((clientesIcono, "escala"), 1, v => clientesIconoEscala.ScaleX = clientesIconoEscala.ScaleY = v, desde: 0.6, rigidez: 300, rebote: 0.35);
            // Hojea: se inclina como quien busca entre las carpetas, y vuelve.
            Luego(280, () => Resorte.Animar((clientesIcono, "giro"), 0, v => clientesIconoGiro.Angle = v, desde: 0, velocidad: -110, rigidez: 240, rebote: 0.3));
        });
    }

    // Cuando llegan los clientes, la carpeta se achica y les deja el lugar.
    void EsconderIcono()
    {
        esperaIcono++;
        if (clientesIcono.Opacity <= 0) return;
        Opacidad(clientesIcono, 0);
        Resorte.Animar((clientesIcono, "escala"), 0.85, v => clientesIconoEscala.ScaleX = clientesIconoEscala.ScaleY = v, rigidez: 300);
    }

    void DetalleTarjeta() => tarjetaDetalle.Text = estampar.IsChecked == true
        ? "Un PDF por empleado y mes, con sus iniciales en la firma. O hacé clic para buscarlos."
        : "Un PDF por empleado y mes. O hacé clic para buscarlos.";

    // ---- Las dos vistas ----

    void CambiarVista(bool clientes)
    {
        if (clientes == enClientes) return;
        enClientes = clientes;
        Pastilla();
        Estirable(clientes);
        FrameworkElement entra = clientes ? vistaClientes : vistaSeparar, sale = clientes ? vistaSeparar : vistaClientes;
        var corridaEntra = clientes ? clientesCorrida : separarCorrida;
        var corridaSale = clientes ? separarCorrida : clientesCorrida;
        // Hacia adelante (de Separar a Clientes) la nueva entra por la derecha; hacia atrás, por la izquierda.
        var lado = clientes ? 1 : -1;
        // El panel de la derecha es de la vista de clientes: al irse, se cierra de una y la ventana vuelve a su ancho.
        if (!clientes && muestra != Muestra.Nada) CerrarPanel(deUna: true);
        var area = SystemParameters.WorkArea;
        var anchoVentana = AnchoSeparar;
        var ancho = vistas.ActualWidth + 40;
        if (clientes) vistaClientes.Height = AltoClientes();
        var estaba = entra.Visibility == Visibility.Visible;
        entra.Visibility = Visibility.Visible;
        entra.Measure(new Size(anchoVentana - 40, double.PositiveInfinity));
        var hasta = entra.DesiredSize.Height;
        var desde = vistas.ActualHeight;

        // ponytail-keep: como en Crecer, la ventana no sigue al alto cuadro a cuadro, porque dejaba ver una
        // franja blanca abajo: si crece, toma el alto final de una vez y el contenido se acomoda adentro; si se
        // achica, se ajusta al final.
        var resto = ActualHeight - desde;
        SizeToContent = SizeToContent.Manual;
        if (hasta > desde) Height = resto + hasta;
        vistas.ClipToBounds = true;
        Resorte.Animar((vistas, "alto"), hasta, v => vistas.Height = Math.Max(0, v), desde: double.IsNaN(vistas.Height) ? desde : null,
            rigidez: 240, rebote: 0.1, alLlegar: () =>
            {
                vistas.Height = double.NaN;
                vistas.ClipToBounds = false;
                SizeToContent = SizeToContent.Height;
            });
        Resorte.Animar((entra, "x"), 0, v => corridaEntra.X = v, desde: estaba ? null : lado * ancho, rigidez: 240, rebote: 0.1);
        Resorte.Animar((sale, "x"), -lado * ancho, v => corridaSale.X = v, rigidez: 240, rebote: 0.1,
            alLlegar: () => { if (enClientes == clientes) sale.Visibility = Visibility.Collapsed; });
        var arriba = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - resto - hasta));
        if (Math.Abs(arriba - Top) > 0.5) Resorte.Animar((this, "arriba"), arriba, v => Top = v, desde: Top, rigidez: 170, rebote: 0.08);

        if (clientes && !clientesVistos)
        {
            clientesVistos = true;
            _ = RevisarClientes(0);
        }
    }

    // El ancho de siempre (la tarjeta de separar y la lista de clientes, 360 más los márgenes) y lo que suma el panel
    // de la derecha cuando está abierto.
    const double AnchoSeparar = 400, AnchoPanel = 440;

    // En la vista de clientes la ventana se estira: hacia abajo crece la lista y, con el panel abierto, hacia los
    // costados crece el panel. Lo elegido se recuerda mientras la app está abierta.
    double? altoElegido, anchoPanelElegido;
    bool estirando; // mientras el usuario arrastra un borde: los cambios de tamaño de la app no cuentan
    double AnchoDelPanel => anchoPanelElegido ?? AnchoPanel;

    void Estirable(bool clientes)
    {
        ResizeMode = clientes ? ResizeMode.CanResize : ResizeMode.CanMinimize;
        var abierto = clientes && muestra != Muestra.Nada;
        var borde = Marco(); // ya con el borde nuevo: el de estirar es más ancho
        MinWidth = (abierto ? AnchoSeparar + 360 : AnchoSeparar) + borde;
        MaxWidth = abierto ? double.PositiveInfinity : AnchoSeparar + borde;
    }

    // ponytail-keep: el ancho de la ventana se mide, no se escribe. Width cuenta el borde que Windows pone alrededor
    // del contenido (unos 7 de cada lado, uno más si la ventana se puede estirar), y el diseño necesita sus 400
    // adentro: con Width = 400 quedaban 385, y la tarjeta de separar, la barra del mes y el panel se cortaban a la
    // derecha (19/11/2026). Depende del tipo de borde y de la pantalla. Minimizada, lo de adentro mide cero: vale la
    // última medida.
    double marco;
    double Marco()
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Minimized && GetWindowRect(h, out var v) && GetClientRect(h, out var c) && c.Derecha > 0)
            marco = (v.Derecha - v.Izquierda - c.Derecha) / VisualTreeHelper.GetDpi(this).DpiScaleX;
        return marco;
    }

    [StructLayout(LayoutKind.Sequential)] struct Rectangulo { public int Izquierda, Arriba, Derecha, Abajo; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr ventana, out Rectangulo r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr ventana, out Rectangulo r);

    // Lo que movió el usuario arrastrando un borde: el alto va a la lista y el ancho al panel.
    void Estirado(SizeChangedEventArgs e)
    {
        if (!estirando || !enClientes) return;
        var alto = vistaClientes.Height + e.NewSize.Height - e.PreviousSize.Height;
        vistaClientes.Height = (altoElegido = Math.Max(380, alto)).Value;
        if (muestra == Muestra.Nada || e.WidthChanged == false) return;
        var ancho = Math.Max(360, panel.Width + e.NewSize.Width - e.PreviousSize.Width);
        anchoPanelElegido = ancho;
        panelCaja.Width = ancho;
        Resorte.Animar((panel, "ancho"), ancho, v => panel.Width = Math.Max(0, v), desde: ancho);
    }

    // La lista tiene alto fijo: lo que entre en la pantalla, sin pasar de 600.
    double AltoClientes() => altoElegido ?? Math.Max(380, Math.Min(600, SystemParameters.WorkArea.Height - 160));

    // La pastilla viaja hasta la tarea elegida y toma su ancho.
    void Pastilla()
    {
        var boton = enClientes ? verClientes : verSeparar;
        var x = boton.TranslatePoint(new Point(0, 0), selector).X;
        Resorte.Animar((pastilla, "x"), x, v => pastillaCorrida.X = v, rigidez: 380, rebote: 0.2);
        Resorte.Animar((pastilla, "ancho"), boton.ActualWidth, v => pastilla.Width = Math.Max(0, v), rigidez: 380, rebote: 0.2);
        verSepararTexto.SetResourceReference(TextBlock.ForegroundProperty, enClientes ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        verClientesTexto.SetResourceReference(TextBlock.ForegroundProperty, enClientes ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
    }

    // ---- El mes ----

    string TextoMes()
    {
        var mes = Clientes.NombreMes(mesElegido);
        return $"{mes[..1]}{mes[1..].ToLowerInvariant()} {anioElegido}";
    }

    TextBlock NuevoMes(string texto)
    {
        var t = new TextBlock
        {
            Text = texto, FontFamily = (FontFamily)FindResource("Titulos"), FontSize = 17, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform()
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        AutomationProperties.SetLiveSetting(t, AutomationLiveSetting.Polite);
        return t;
    }

    void CambiarMes(int paso)
    {
        if (guardando) return; // la lista que se está guardando es la de este mes
        var nuevo = new DateTime(anioElegido, mesElegido, 1).AddMonths(paso);
        (mesElegido, anioElegido) = (nuevo.Month, nuevo.Year);
        // El nombre viejo sale hacia el lado contrario y el nuevo entra desde el lado de la flecha tocada.
        if (mesActual is { } viejo)
        {
            var tv = (TranslateTransform)viejo.RenderTransform;
            Resorte.Animar((viejo, "x"), -paso * 80, v => tv.X = v, rigidez: 320, rebote: 0.1);
            Opacidad(viejo, 0, alTerminar: () =>
            {
                mesMarco.Children.Remove(viejo);
                Resorte.Olvidar((viejo, "x"));
                Resorte.Olvidar((viejo, "opacidad"));
            });
        }
        var actual = mesActual = NuevoMes(TextoMes());
        mesMarco.Children.Add(actual);
        var ta = (TranslateTransform)actual.RenderTransform;
        Resorte.Animar((actual, "x"), 0, v => ta.X = v, desde: paso * 80, rigidez: 320, rebote: 0.1);
        Opacidad(actual, 1, desde: 0);

        // Los renglones del mes anterior se van ya, hacia el mismo lado que su nombre.
        corteClientes?.Cancel();
        generacionClientes++;
        CerrarPanel();
        SacarFilas(paso);
        Opacidad(clientesCartel, 0);
        guardar.IsEnabled = false;
        EstadoClientes("");
        // Se revisa cuando deja de tocar las flechas: pasar por tres meses no lanza tres revisiones.
        var espera = ++esperaMes;
        Luego(650, () =>
        {
            if (espera == esperaMes) _ = RevisarClientes(0);
        });
    }

    // ---- La revisión ----

    void EstadoClientes(string texto) => clientesEstado.Text = texto;

    async Task RevisarClientes(int lado)
    {
        corteClientes?.Cancel();
        var corte = (corteClientes = new CancellationTokenSource()).Token;
        var g = ++generacionClientes;
        var (mes, anio) = (mesElegido, anioElegido);
        CerrarPanel();
        SacarFilas(lado);
        MostrarIcono();
        elegidos.Clear();
        Opacidad(clientesCartel, 0);
        guardar.IsEnabled = false;
        sinRaiz.Visibility = Visibility.Collapsed;
        EstadoClientes($"Buscando los clientes de {TextoMes()}…");

        var (raiz, esperada) = await Task.Run(() => RaizClientes(anio));
        if (g != generacionClientes) return;
        raizActual = raiz;
        guardarTexto.Text = TextoGuardar;
        // Cuál se está usando, para no tener que adivinarlo: lo dice la carpetita al pasar por encima.
        cambiarRaiz.ToolTip = raiz is not null ? $"Carpeta de los clientes: {raiz}\nTocala para elegir otra."
            : esperada is not null ? $"No encuentro {esperada}\nTocala para elegir otra carpeta."
            : "Elegir la carpeta de los clientes";
        AutomationProperties.SetHelpText(cambiarRaiz, (string)cambiarRaiz.ToolTip);
        if (raiz is null)
        {
            sinRaizTexto.Text = esperada is null
                ? "Elegí la carpeta donde están las carpetas de los clientes. Se elige una sola vez: queda guardada en esta PC."
                : $"No encuentro la carpeta de los clientes de {anio}:\n{esperada}";
            sinRaiz.Visibility = Visibility.Visible;
            MostrarIcono(0);
            Opacidad(sinRaiz, 1, desde: 0);
            EstadoClientes("");
            return;
        }
        if (memoria is not { } mem || memoriaDe != (mes, anio))
        {
            // De la memoria anterior, lo que está adentro de una carpeta de este mes («… SUELDOS OCTUBRE 2026»).
            mem = await Task.Run(() => new Memoria(MemoriaFile(mes, anio), MemoriaAnterior,
                ruta => (Path.GetDirectoryName(ruta) ?? "").Split('\\', '/').Any(carpeta => Clientes.EsDelMes(carpeta, mes, anio))));
            if (g != generacionClientes) return;
            (memoria, memoriaDe) = (mem, (mes, anio));
        }
        if (!lectorCreado)
        {
            lectorCreado = true;
            // Si los modelos del OCR no se pudieron preparar (no hay dónde copiarlos), se revisan igual los PDF con
            // texto; las fotos y los escaneos quedan sin leer, y al terminar se avisa.
            try { if (await Task.Run(Lector.Crear) is { } lectores) (lector, lectorAFondo) = lectores; }
            catch { }
        }
        try
        {
            var clientes = await Task.Run(() => Clientes.ConSueldos(raiz), corte);
            if (g != generacionClientes) return;
            EsconderIcono();
            foreach (var (ruta, i) in clientes.Select((c, i) => (c, i))) AgregarFila(ruta, i);
            Acomodar();
            foreach (var (ruta, i) in clientes.Select((c, i) => (c, i)))
            {
                corte.ThrowIfCancellationRequested();
                var fila = porCliente[ruta];
                EstadoClientes($"{i + 1} de {clientes.Count} · {Path.GetFileName(ruta)}");
                Rueda(fila, true);
                var r = await Task.Run(() => Clientes.Revisar(ruta, mes, anio, ocr, lector, mem,
                    msg => Dispatcher.InvokeAsync(() => { if (g == generacionClientes) EstadoClientes(msg); }), corte), corte);
                if (g != generacionClientes) return;
                Rueda(fila, false);
                ResolverCliente(fila, r);
                _ = Task.Run(mem.Guardar);
            }
            ordenarPorGrupo = true;
            Acomodar();
            CartelClientes();

            // La segunda pasada: primero se leyó todo lo fácil, para poder empezar a trabajar; ahora, de a un cliente,
            // las hojas donde no apareció nadie de la nómina, a fondo. Cada cliente se actualiza al terminar el suyo.
            var conDificiles = porCliente.Values.Where(f => f.Dificiles > 0).OrderBy(f => f.Grupo).ThenBy(f => f.Orden).ToList();
            foreach (var (fila, i) in conDificiles.Select((f, i) => (f, i)))
            {
                corte.ThrowIfCancellationRequested();
                var antes = fila.Revision!;
                EstadoClientes($"Hojas difíciles: {i + 1} de {conDificiles.Count} clientes · {antes.Cliente}");
                Rueda(fila, true);
                await Task.Run(() => Clientes.Profundizar(antes, lectorAFondo!, mem,
                    msg => Dispatcher.InvokeAsync(() => { if (g == generacionClientes) EstadoClientes($"Hojas difíciles · {antes.Cliente}: {msg}"); }), corte), corte);
                var r = await Task.Run(() => Clientes.Revisar(fila.Ruta, mes, anio, ocr, lector, mem, _ => { }, corte), corte);
                if (g != generacionClientes) return;
                Rueda(fila, false);
                ResolverCliente(fila, r);
                _ = Task.Run(mem.Guardar);
            }
            if (conDificiles.Count > 0)
            {
                Acomodar();
                CartelClientes();
            }
            EstadoClientes(lector is null ? "⚠ No pude preparar el OCR: las fotos quedaron sin leer"
                : clientes.Count == 1 ? $"1 cliente · {TextoMes()}" : $"{clientes.Count} clientes · {TextoMes()}");
            guardar.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (g == generacionClientes) EstadoClientes($"⚠ {ex.Message}");
        }
    }

    // La carpeta de los clientes la elige cada PC una vez y queda guardada en ella: la ruta del estudio no va en el
    // código, que es público. Si la ruta nombra un año, para otro año se busca la misma con ese año. Esperada es
    // la que se buscó (null si nunca se eligió ninguna).
    (string? Raiz, string? Esperada) RaizClientes(int anio)
    {
        var elegida = raizElegida;
        if (elegida is null)
            try { elegida = File.ReadAllText(RaizFile).Trim(); }
            catch { } // nunca se eligió
        if (string.IsNullOrWhiteSpace(elegida)) return (null, null);
        var delAnio = Clientes.RaizDelAnio(elegida, anio);
        return (Directory.Exists(delAnio) ? delAnio : null, delAnio);
    }

    // Lo que dicen los botones de guardar: la unidad de la carpeta elegida («Z:»), o la carpeta si es de red.
    string Donde => Unidad(raizActual, "la carpeta de los clientes");

    string TextoGuardar => $"Guardar en {Donde}…";

    void ElegirRaiz()
    {
        // Lo que se está guardando, o la lista que se está armando para guardar, es de la carpeta de ahora.
        if (guardando || planeando) return;
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Elegí la carpeta donde están las carpetas de los clientes" };
        // Abre en la que se usa ahora: «Seleccionar carpeta» sin moverse la deja como estaba. Abriendo en la de arriba,
        // ese mismo clic elegiría la de arriba.
        if (raizActual is not null) d.InitialDirectory = raizActual;
        if (d.ShowDialog(this) != true) return;
        raizElegida = d.FolderName;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RaizFile)!);
            File.WriteAllText(RaizFile, d.FolderName);
        }
        catch { } // sin recordarla, vale hasta cerrar la app
        // La carpetita acusa recibo, y la lista se vacía y se vuelve a llenar con los clientes de la carpeta nueva.
        Resorte.Animar((cambiarRaiz, "latido"), 1, v => cambiarRaizEscala.ScaleX = cambiarRaizEscala.ScaleY = v, velocidad: 2.5, rigidez: 500, rebote: 0.5);
        _ = RevisarClientes(0);
    }

    void AsomarCarpeta(bool si)
    {
        Resorte.Animar((cambiarRaiz, "giro"), si ? -10 : 0, v => cambiarRaizGiro.Angle = v, rigidez: 300, rebote: 0.3);
        Resorte.Animar((cambiarRaiz, "altura"), si ? -2 : 0, v => cambiarRaizAltura.Y = v, rigidez: 300, rebote: 0.3);
    }

    // ---- Los renglones ----

    double AnchoCliente => Math.Max(200, clientesDesplazar.ActualWidth - 14); // lugar para la barra de desplazamiento

    void AgregarFila(string ruta, int orden)
    {
        var iconos = (FontFamily)FindResource("Iconos");
        var disco = new Formas.Ellipse { Width = 22, Height = 22 };
        disco.Fill = Azul; // todavía sin leer
        var marca = new TextBlock { FontFamily = iconos, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var marcaEscala = new ScaleTransform(0.45, 0.45);
        var punto = new Grid { Width = 22, Height = 22, Margin = new(8, 0, 10, 0), RenderTransformOrigin = new(0.5, 0.5), RenderTransform = marcaEscala, Children = { disco, marca } };
        // ponytail-keep: el nombre en una columna Auto con tope y el detalle en la que se estira, con «…». Al revés (el
        // nombre estirable), un detalle largo («faltan 10 firmas · 4 sin identificar · 3 ilegibles…») se comía el
        // nombre entero y el renglón quedaba sin cliente.
        var nombre = new TextBlock
        {
            Text = Path.GetFileName(ruta), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = AnchoCliente / 2,
        };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var detalle = new TextBlock
        {
            FontSize = 12, Margin = new(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        detalle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        // La flecha de «abrir» aparece cuando hay algo para ver, y apunta hacia el panel, que se abre a la derecha.
        var chevronCorrida = new TranslateTransform();
        var chevron = new TextBlock
        {
            Text = "\uE76C", FontFamily = iconos, FontSize = 9, Margin = new(0, 0, 10, 0), Opacity = 0, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = chevronCorrida
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        // Elegido, el renglón queda levantado: una tarjeta con sombra detrás, mientras el panel muestra ese cliente.
        var tarjeta = new Border { CornerRadius = new(8), BorderThickness = new(1), Opacity = 0 };
        tarjeta.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        tarjeta.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var fondo = new Border { CornerRadius = new(8), Opacity = 0 };
        fondo.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        Grid.SetColumnSpan(fondo, 4);
        Grid.SetColumn(nombre, 1);
        Grid.SetColumn(detalle, 2);
        Grid.SetColumn(chevron, 3);
        // Mientras se lee, en el lugar de la flecha gira un arco: lo único que gira sin parar, con velocidad pareja.
        var rueditaGiro = new RotateTransform();
        var ruedita = new Formas.Ellipse
        {
            Width = 14, Height = 14, StrokeThickness = 2, Margin = new(0, 0, 8, 0), Opacity = 0, VerticalAlignment = VerticalAlignment.Center,
            StrokeDashArray = new DoubleCollection([12, 7]), RenderTransformOrigin = new(0.5, 0.5), RenderTransform = rueditaGiro
        };
        ruedita.SetResourceReference(Formas.Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        Grid.SetColumn(ruedita, 3);
        var cabecera = new Grid
        {
            Height = AltoRenglon, Background = Brushes.Transparent,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { fondo, punto, nombre, detalle, chevron, ruedita }
        };
        var boton = new Button { Style = (Style)FindResource("Llano"), Content = cabecera };
        AutomationProperties.SetName(boton, Path.GetFileName(ruta));
        var corrida = new TranslateTransform();
        var el = new Grid { Width = AnchoCliente, RenderTransform = corrida, Children = { tarjeta, boton } };
        var f = new FilaCliente
        {
            Ruta = ruta, El = el, Tarjeta = tarjeta, Boton = boton, Disco = disco, Marca = marca, Detalle = detalle, Chevron = chevron, MarcaEscala = marcaEscala,
            Corrida = corrida, ChevronCorrida = chevronCorrida, Orden = orden, Ruedita = ruedita, RueditaGiro = rueditaGiro
        };
        boton.Click += (_, _) => Elegir(f);
        // Al apretar se hunde apenas, y la flecha ya empieza a ir hacia el panel.
        Hundir(boton, 0.985, si =>
        {
            if (f.Revision is not null) Resorte.Animar((f, "flecha"), si ? 4 : 0, v => chevronCorrida.X = v, rigidez: 420, rebote: 0.3);
        }, objetivo: cabecera);
        // Al pasar por encima se ilumina el fondo, como una fila de Windows 11.
        boton.MouseEnter += (_, _) => Opacidad(fondo, 1);
        boton.MouseLeave += (_, _) => Opacidad(fondo, 0);
        clientesFilas.Children.Add(el);
        porCliente[ruta] = f;
        // Si son muchos, entran escalonados los primeros catorce, que son los que se ven.
        Aparecer(el, corrida, desdeY: 10, espera: Math.Min(orden, 14) * 30);
    }

    // Los de la revisión anterior se van: se apagan y, al cambiar de mes, se corren hacia el mismo lado que su nombre.
    void SacarFilas(int lado)
    {
        foreach (var f in porCliente.Values)
        {
            var el = f.El;
            var corrida = f.Corrida;
            if (lado != 0) Resorte.Animar((el, "x"), -lado * 60, v => corrida.X = v, rigidez: 260, rebote: 0.1);
            Opacidad(el, 0, alTerminar: () => clientesFilas.Children.Remove(el));
        }
        porCliente.Clear();
        ordenarPorGrupo = false;
    }

    // Cada renglón a su lugar: al terminar la revisión, los que tienen faltantes suben arriba y los demás les hacen lugar.
    void Acomodar()
    {
        double y = 0;
        var orden = ordenarPorGrupo ? porCliente.Values.OrderBy(f => f.Grupo).ThenBy(f => f.Orden) : porCliente.Values.OrderBy(f => f.Orden);
        foreach (var f in orden)
        {
            var el = f.El;
            var top = y;
            Resorte.Animar((el, "top"), top, v => Canvas.SetTop(el, v), desde: double.IsNaN(Canvas.GetTop(el)) ? top : null, rigidez: 220, rebote: 0.12);
            y += AltoRenglon;
        }
        clientesFilas.Height = y;
    }

    // Tocar un cliente lo elige: su renglón queda levantado y el panel de la derecha muestra sus empleados. Tocar
    // el elegido, o la cruz, lo cierra.
    void Elegir(FilaCliente f)
    {
        if (f.Revision is null) return; // todavía sin leer
        if (elegida == f && muestra == Muestra.Cliente)
        {
            CerrarPanel();
            return;
        }
        MarcarElegida(f);
        MostrarCliente(f);
    }

    // La marca de elegido viaja: la tarjeta del anterior se apaga y la del nuevo se levanta, con su sombra.
    void MarcarElegida(FilaCliente? f)
    {
        if (elegida is { } vieja && vieja != f)
        {
            var t = vieja.Tarjeta;
            Opacidad(t, 0, alTerminar: () => { if (elegida != vieja) t.Effect = null; });
        }
        elegida = f;
        if (f is null) return;
        // La sombra se pone al elegir y se saca al soltar: con cincuenta renglones, cincuenta sombras quietas pesan.
        f.Tarjeta.Effect ??= new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1.5, Opacity = 0.16 };
        Opacidad(f.Tarjeta, 1);
    }

    void ResolverCliente(FilaCliente f, Revision r)
    {
        f.Revision = r;
        f.Dificiles = lectorAFondo is null ? 0 : Clientes.Dificiles(r).Count;
        var (glifo, color, texto, grupo) = Resumen(r);
        f.Grupo = grupo;
        if (color is null) f.Disco.SetResourceReference(Formas.Shape.FillProperty, "ControlStrongStrokeColorDefaultBrush");
        else f.Disco.Fill = color;
        f.Marca.Text = glifo;
        f.Marca.Foreground = color == Amarillo ? TintaOscura : Brushes.White;
        f.Detalle.Text = f.Dificiles > 0 ? $"{texto} · {f.Dificiles} por leer" : texto;
        f.Detalle.ToolTip = f.Detalle.Text.Length > 0 ? f.Detalle.Text : null; // entero, por si el renglón lo corta
        f.Detalle.SetResourceReference(TextBlock.ForegroundProperty, grupo switch
        {
            0 => "SystemFillColorCriticalBrush",
            1 => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        });
        AutomationProperties.SetName(f.Boton, $"{r.Cliente}: {texto}");
        Opacidad(f.Chevron, 1);
        // La marca pasa del punto gris a su forma con un rebote chico.
        Resorte.Animar((f, "marca"), 1, v => f.MarcaEscala.ScaleX = f.MarcaEscala.ScaleY = v, desde: 0.45, velocidad: 3, rigidez: 420, rebote: 0.4);
        if (elegida == f && muestra == Muestra.Cliente && !guardando) MostrarCliente(f);
    }

    // El renglón que se está leyendo: la flecha se va y gira la ruedita; al terminar, vuelve la flecha.
    void Rueda(FilaCliente f, bool girando)
    {
        if (girando && !Resorte.Quieto)
            f.RueditaGiro.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
        else f.RueditaGiro.BeginAnimation(RotateTransform.AngleProperty, null);
        Opacidad(f.Ruedita, girando ? 1 : 0);
        Opacidad(f.Chevron, girando || f.Revision is null ? 0 : 1);
    }

    // El renglón toma el color de lo peor que tiene, y lo dice en palabras. Grupo: 0 falta algún recibo (rojo), 1 están
    // los recibos y faltan firmas (amarillo), 2 están todos, 3 sin datos para revisar.
    static (string Glifo, Brush? Color, string Texto, int Grupo) Resumen(Revision r)
    {
        if (r.CarpetaMes is null) return (Raya, null, $"sin carpeta de {Clientes.NombreMes(r.Mes).ToLowerInvariant()}", 3);
        if (r.Nomina is null) return (Pregunta, null, "sin nómina", 3);
        var recibos = r.Renglones.Count(FaltaElRecibo);
        var firmas = r.Faltan - recibos;
        var sinLeer = r.Ilegibles.Select(h => h.Ruta).Distinct().Count();
        // Los encontrados por el nombre o por un CUIL casi igual: están, pero que se vea desde la lista que hay que mirarlos.
        var mirar = r.Renglones.Count(x => x.Firma == Firma.PorNombre);
        var partes = new List<string>();
        if (recibos > 0) partes.Add(recibos == 1 ? "falta 1 recibo" : $"faltan {recibos} recibos");
        if (r.Firmados is null) partes.Add("sin firmados todavía");
        else if (firmas > 0) partes.Add(partes.Count > 0 ? (firmas == 1 ? "1 firma" : $"{firmas} firmas") : firmas == 1 ? "falta 1 firma" : $"faltan {firmas} firmas");
        if (sinLeer > 0) partes.Add($"{sinLeer} sin identificar");
        if (mirar > 0) partes.Add(mirar == 1 ? "1 ilegible, revisar" : $"{mirar} ilegibles, revisar");
        var texto = string.Join(" · ", partes);
        if (recibos > 0) return (Cruz, Rojo, texto, 0);
        if (r.Firmados is null) return (Reloj, Amarillo, texto, 1);
        if (firmas > 0) return (Lapiz, Amarillo, texto, 1);
        var todos = $"{r.Renglones.Count} de {r.Renglones.Count}";
        return (Tilde, Verde, partes.Count > 0 ? $"{todos} · {texto}" : todos, 2);
    }

    // Falta el recibo del todo: ni el firmado ni el sin firmar. Si está el sin firmar, lo que falta es la firma.
    static bool FaltaElRecibo(Renglon x) => x.Firma == Firma.Falta && x.SinFirmar.Count == 0;

    // El panel de un cliente: un renglón por empleado (los que faltan primero), lo que no tiene a nadie de la nómina
    // y, abajo, la carpeta.
    void MostrarCliente(FilaCliente f)
    {
        var r = f.Revision!;
        var mes = Clientes.NombreMes(r.Mes);
        var seccion = paraGuardar = new StackPanel();
        ranuras.Clear();
        iconosPlan.Clear();
        Llenar($"{r.Cliente}", $"{TextoMes()} · {Resumen(r).Texto}", null, lista =>
        {
            // Mientras quedan hojas difíciles por leer, el firmado de alguno puede estar en ellas: no se firman iniciales.
            if (f.Dificiles > 0)
                lista.Add(Nota(f.Dificiles == 1
                    ? "Queda 1 hoja difícil por leer de este cliente. Para firmar iniciales, esperá a que termine: el firmado puede estar ahí."
                    : $"Quedan {f.Dificiles} hojas difíciles por leer de este cliente. Para firmar iniciales, esperá a que terminen: el firmado puede estar ahí."));
            if (Clientes.NoMandoNada(r)) lista.Add(UltimoRecurso(f));
            if (r.Nomina is not null) lista.Add(Seccion("Empleados"));
            if (r.CarpetaMes is null) lista.Add(Nota($"No encontré la carpeta de {mes.ToLowerInvariant()} adentro de la de sueldos."));
            else if (r.Nomina is null) lista.Add(Nota("No encontré la nómina de AFIP del mes: un PDF con «NOMINA» en el nombre."));
            else
            {
                if (r.Firmados is null) lista.Add(Nota($"Todavía no hay carpeta «RECIBOS FIRMADOS {mes}»."));
                // De cada firmado se muestra primero el archivo propio del empleado (el que la app guardó, o una foto
                // suelta) y después el escaneo de varios juntos.
                var porArchivo = r.Renglones.SelectMany(x => x.Firmado.Select(h => (h.Ruta, x.Empleado.Cuil))).Distinct()
                    .GroupBy(t => t.Ruta, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var x in r.Renglones.OrderBy(x => x.Firma != Firma.Falta))
                    lista.Add(LineaEmpleado(f, x with { Firmado = x.Firmado.OrderBy(h => porArchivo[h.Ruta]).ToList() }));
                foreach (var g in r.Ilegibles.GroupBy(h => h.Ruta, StringComparer.OrdinalIgnoreCase))
                    lista.Add(ConPrevia(TextoEnlace($"No encontré a nadie de la nómina en {Path.GetFileName(g.Key)}"), Hojas(g.ToList())));
            }
            foreach (var a in r.Avisos.Take(4)) lista.Add(Nota($"⚠ {a}"));
            if (r.Nomina is null) return;
            // Lo que se guarda de cada empleado va como un ícono en su renglón; acá, solo lo que no es de nadie.
            lista.Add(seccion);
        });
        pieGuardar.Visibility = Visibility.Collapsed;
        pieCliente.Visibility = Visibility.Visible;
        abrirCarpeta.Visibility = (r.Firmados ?? r.CarpetaMes) is null ? Visibility.Collapsed : Visibility.Visible;
        guardarCliente.Visibility = Visibility.Collapsed;
        AbrirPanel(Muestra.Cliente);
        _ = PlanDelCliente(f, seccion);
    }

    // Un cliente que no mandó ningún firmado: se ofrece armarlos todos con las iniciales del estudio, dicho como lo que es,
    // un último recurso para cuando ya no hay tiempo de volver a pedirlos. El botón solo tilda las casillas: cada uno se
    // puede mirar con su lupa y nada se escribe hasta «Guardar».
    FrameworkElement UltimoRecurso(FilaCliente f)
    {
        var titulo = new TextBlock { Text = "No mandó ningún recibo firmado", FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var texto = Nota("Si ya no hay tiempo de volver a pedirlos, se pueden armar con las iniciales de cada empleado sobre su recibo original. " +
                         "Es un último recurso: en la lista quedan como «con iniciales», no como firmados por el empleado.");
        const string Todos = "Firmar todos con iniciales";
        var boton = new Button { Content = Todos, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0), IsEnabled = f.Dificiles == 0 };
        if (f.Dificiles > 0) boton.ToolTip = "Todavía se están leyendo hojas difíciles de este cliente";
        boton.Click += (_, _) =>
        {
            var casillas = ranuras.Values.Select(x => x.Casilla).OfType<CheckBox>().ToList();
            var tildar = casillas.Any(c => c.IsChecked != true);
            // Una tras otra, de arriba abajo: se ve qué se tildó y en qué orden.
            for (var i = 0; i < casillas.Count; i++)
            {
                var c = casillas[i];
                Luego(i * 70, () => c.IsChecked = tildar);
            }
            boton.Content = tildar ? "Desmarcar todos" : Todos;
        };
        var icono = new TextBlock { Text = Alerta, FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 14, Margin = new(0, 1, 10, 0), Foreground = Amarillo, VerticalAlignment = VerticalAlignment.Top };
        var textos = new StackPanel { Children = { titulo, texto, boton } };
        Grid.SetColumn(textos, 1);
        var tarjeta = new Border
        {
            CornerRadius = new(8), BorderThickness = new(1), BorderBrush = Amarillo, Padding = new(12, 10, 12, 12), Margin = new(0, 4, 0, 6),
            Child = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() }, Children = { icono, textos } },
        };
        tarjeta.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        return tarjeta;
    }

    // Lo que se guardaría de un cliente, en su panel. Se calcula aparte (mira en la carpeta qué existe ya) y se vuelve
    // a calcular al marcar o desmarcar iniciales.
    async Task PlanDelCliente(FilaCliente f, StackPanel destino)
    {
        var turno = ++turnoPlanCliente;
        var r = f.Revision!;
        var suyos = ElegidosDe(f);
        guardarCliente.Visibility = Visibility.Collapsed;
        List<Escritura> suyo;
        try { suyo = await Task.Run(() => Clientes.Planear(r, suyos)); }
        catch (Exception ex) { suyo = []; destino.Children.Clear(); destino.Children.Add(Nota($"⚠ {ex.Message}")); }
        if (turno != turnoPlanCliente || elegida != f || muestra != Muestra.Cliente) return;
        destino.Children.Clear();

        // Lo de cada empleado va en su renglón: un ícono que al tocarlo muestra cómo va a quedar y que al guardarse se
        // vuelve tilde. Así cada nombre aparece una sola vez. Abajo, solo lo que no es de nadie: la carpeta nueva y la
        // lista de los que faltan.
        var marcas = new Dictionary<Escritura, (TextBlock Marca, TextBlock Texto)>();
        var conIcono = new HashSet<string>();
        foreach (var e in suyo)
            if (e.Empleado is { } emp && ranuras.TryGetValue(emp.Cuil, out var ranura) && EnRenglon(r, e, ranura, conIcono) is { } marca)
                marcas[e] = (marca, ranura.Debajo);
        // Si al recalcular ya no va el de alguno, sus íconos se desvanecen.
        foreach (var cuil in iconosPlan.Keys.Except(conIcono).ToList())
        {
            var (caja, _) = iconosPlan[cuil];
            iconosPlan.Remove(cuil);
            Opacidad(caja, 0, alTerminar: () => (caja.Parent as Panel)?.Children.Remove(caja));
        }
        var resto = suyo.Where(e => !marcas.ContainsKey(e)).ToList();
        if (resto.Count > 0)
        {
            destino.Children.Add(Seccion("También se guarda"));
            ArmarPlan(r, resto, el => destino.Children.Add(el), (e, marca, texto) => marcas[e] = (marca, texto));
        }
        // En el orden del plan, no en el de la pantalla: la carpeta nueva antes que lo que va adentro, la lista al final.
        planCliente = suyo.Select(e => (f, e, marcas[e].Marca, marcas[e].Texto)).ToList();
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

    // Lo que se le guarda a un empleado, en su renglón. Devuelve lo que acusa recibo al guardarse (se vuelve tilde).
    TextBlock? EnRenglon(Revision r, Escritura e, Ranura ranura, HashSet<string> conIcono)
    {
        // Las iniciales ya tienen en el renglón su casilla y su lupa: la lupa es la que acusa recibo.
        if (e.Accion == Accion.Iniciales) return ranura.Lupa;
        if (e.Accion != Accion.Corregido) return null;
        var cuil = e.Empleado!.Cuil;
        conIcono.Add(cuil);
        if (iconosPlan.TryGetValue(cuil, out var ya)) return ya.Glifo;
        var carpeta = Path.GetFileName(Path.GetDirectoryName(e.Ruta));
        var explica = e.Hojas.Count == 1
            ? $"Se guarda derecho y con más contraste, en «{carpeta}». Tocalo para ver cómo queda."
            : $"Sus {e.Hojas.Count} hojas se guardan juntas, derechas y con más contraste, en «{carpeta}». Tocalo para ver cómo quedan.";
        // El de contraste y no una flecha que gira: esa ya es «Revisar de nuevo», al lado del mes.
        var glifo = new TextBlock { Text = "\uE793", FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 13 };
        var boton = new Button
        {
            Style = (Style)FindResource("Icono"), Width = 32, Height = 32, Margin = new(2, -4, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            ToolTip = explica, Content = glifo, RenderTransformOrigin = new(0.5, 0.5),
        };
        AutomationProperties.SetName(boton, $"Ver cómo se guarda el recibo de {ranura.Nombre}");
        AutomationProperties.SetHelpText(boton, explica);
        // Entra creciendo al lado del nombre cuando el plan termina de calcularse, como la lupa de las iniciales.
        var escala = new ScaleTransform(0.5, 0.5);
        boton.RenderTransform = escala;
        Opacidad(boton, 1, desde: 0);
        Resorte.Animar((boton, "escala"), 1, v => escala.ScaleX = escala.ScaleY = v, desde: 0.5, rigidez: 420, rebote: 0.35);
        var cargar = ComoQueda(r, e)!;
        var viendo = false;
        var nuevo = Path.GetFileName(e.Ruta);
        boton.Click += async (_, _) => await Desplegar(ranura.Despliegue, viendo = !viendo,
            async () => [Nota(e.Hojas.Count == 1 ? $"Así se guarda, como «{nuevo}»: derecho y con más contraste." : $"Así se guardan, juntas en «{nuevo}»: derechas y con más contraste."), .. await cargar()]);
        // Después del sol, que así sigue alineado con las casillas de las iniciales: un lápiz si cambia el nombre.
        var caja = new StackPanel { Orientation = Orientation.Horizontal, Children = { boton } };
        if (Clientes.NombreCorregido(e) is { } corregido) caja.Children.Add(Lapicito(corregido));
        ranura.Acciones.Children.Add(caja);
        iconosPlan[cuil] = (caja, glifo);
        return glifo;
    }

    // El lápiz del nombre corregido no se toca, se lee: al pasar por encima se aclara y dice qué nombre cambia, sin el
    // fondo de los botones. Entra creciendo apenas después del sol, como la segunda opción de un menú.
    FrameworkElement Lapicito(string corregido)
    {
        var glifo = new TextBlock
        {
            Text = Lapiz, FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        glifo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var escala = new ScaleTransform(0.5, 0.5);
        var lapiz = new Border
        {
            Width = 32, Height = 32, Margin = new(2, -4, 0, 0), VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent,
            ToolTip = corregido, Child = glifo, Opacity = 0, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = escala,
        };
        AutomationProperties.SetName(lapiz, corregido);
        lapiz.MouseEnter += (_, _) => glifo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        lapiz.MouseLeave += (_, _) => glifo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Luego(70, () =>
        {
            Opacidad(lapiz, 1, desde: 0);
            Resorte.Animar((lapiz, "escala"), 1, v => escala.ScaleX = escala.ScaleY = v, desde: 0.5, rigidez: 420, rebote: 0.35);
        });
        return lapiz;
    }

    // Abajo del renglón, lo que arma cargar, creciendo con resorte; con ver en false se vuelve a cerrar.
    async Task Desplegar(Border caja, bool ver, Func<Task<List<UIElement>>> cargar)
    {
        if (!ver)
        {
            Resorte.Animar((caja, "alto"), 0, v => caja.Height = Math.Max(0, v), rigidez: 260, rebote: 0.1);
            return;
        }
        var adentro = new StackPanel();
        try { foreach (var c in await cargar()) adentro.Children.Add(c); }
        catch (Exception ex) { adentro.Children.Add(Nota($"No pude armar la vista previa: {ex.Message}")); }
        caja.Child = adentro;
        adentro.Measure(new Size(Math.Max(200, panelLista.ActualWidth - 24), double.PositiveInfinity));
        Resorte.Animar((caja, "alto"), adentro.DesiredSize.Height, v => caja.Height = Math.Max(0, v), rigidez: 260, rebote: 0.1);
    }

    // Los CUIL de este cliente que el usuario eligió para estamparles las iniciales.
    IReadOnlySet<string> ElegidosDe(FilaCliente f) => elegidos
        .Where(e => e.StartsWith(f.Ruta + "|", StringComparison.OrdinalIgnoreCase)).Select(e => e[(f.Ruta.Length + 1)..]).ToHashSet();

    // «Qué se guardará», agrupado por archivo o carpeta: qué se hace con él, y debajo solo los nombres. Más corto se
    // lee mejor que «NOMBRE 10 2026.pdf: enderezado de «…»» repetido en cada renglón. La carpeta nueva va primero:
    // en ese orden se guarda, y lo de adentro la necesita.
    void ArmarPlan(Revision r, IReadOnlyList<Escritura> items, Action<UIElement> agregar, Action<Escritura, TextBlock, TextBlock> registrar)
    {
        const string Hoja = "\uE8A5";
        void Grupo(string glifo, string titulo, string queHace, IEnumerable<Escritura> cuales, Func<Escritura, string> texto)
        {
            agregar(Encabezado(glifo, titulo, queHace));
            foreach (var e in cuales)
            {
                var (linea, marca, t) = LineaPlan(texto(e), e.Ruta);
                // Tocar el nombre muestra cómo va a quedar lo que se guarda.
                agregar(ConPrevia(linea, ComoQueda(r, e), tooltip: "Ver cómo va a quedar", aire: 0));
                registrar(e, marca, t);
            }
        }
        static string Quien(Escritura e) => Regex.Replace(Path.GetFileNameWithoutExtension(e.Ruta), @"\s\d{2}\s\d{4}$", "");
        static string DondeVa(Escritura e) => Path.GetFileName(Path.GetDirectoryName(e.Ruta)) ?? "";
        foreach (var e in items.Where(e => e.Accion == Accion.Carpeta))
            Grupo(Carpeta, Path.GetFileName(e.Ruta), "", [e], _ => "Carpeta nueva");
        foreach (var g in items.Where(e => e.Accion == Accion.Corregido).GroupBy(DondeVa))
            Grupo(Carpeta, g.Key, "Enderezamos estos firmados y les subimos el contraste:", g, e => e.Hojas.Count == 1 ? Quien(e) : $"{Quien(e)} ({e.Hojas.Count} hojas)");
        foreach (var g in items.Where(e => e.Accion == Accion.Iniciales).GroupBy(DondeVa))
            Grupo(Carpeta, g.Key, "Firmamos con las iniciales del estudio:", g, Quien);
        foreach (var e in items.Where(e => e.Accion == Accion.Consolidado))
        {
            // Quiénes están en las hojas que se agregan; si el consolidado ya tiene a alguien, se dice a quiénes se suma.
            var suman = r.Renglones.Where(x => x.Firmado.Any(h => e.Hojas.Any(o => o.Hoja == h.Hoja && o.Ruta == h.Ruta))).ToList();
            var completa = r.Renglones.Any(x => x.Firmado.Any(h => h.Ruta.Equals(e.Ruta, StringComparison.OrdinalIgnoreCase)));
            var hojas = e.Hojas.Count == 1 ? "1 hoja" : $"{e.Hojas.Count} hojas";
            Grupo(Hoja, Path.GetFileNameWithoutExtension(e.Ruta), completa ? "Le sumamos al final los que le faltaban:" : "Juntamos todos los firmados en un PDF:", [e],
                _ => completa ? $"{string.Join(", ", suman.Select(x => x.Nombre))} ({hojas})" : $"{(suman.Count == 1 ? "1 recibo" : $"{suman.Count} recibos")} en {hojas}");
        }
        foreach (var e in items.Where(e => e.Accion == Accion.Lista))
            Grupo(Hoja, Path.GetFileNameWithoutExtension(e.Ruta), "", [e], _ => "La lista de los que faltan");
    }

    // Cómo va a quedar cada cosa: el firmado ya derecho y con contraste, la zona de la firma con las iniciales, o el
    // texto de la lista. Lo arma el mismo código que después lo guarda.
    static Func<Task<List<UIElement>>>? ComoQueda(Revision r, Escritura e) => e.Accion switch
    {
        Accion.Corregido or Accion.Consolidado => async () =>
        {
            var lista = new List<UIElement>();
            foreach (var h in e.Hojas.Take(3)) lista.Add(Imagen(DesdeJpeg(await Task.Run(() => ComoSeGuarda(h)))));
            return lista;
        },
        Accion.Iniciales => async () => [Imagen(await Task.Run(() => RecorteDeLaFirma(e.Hojas[0])), () => AbrirConIniciales(e.Hojas[0]), maxAlto: 220)],
        Accion.Lista => () =>
        {
            var texto = new TextBlock { Text = Clientes.Lista(r), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) };
            texto.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return Task.FromResult(new List<UIElement> { texto });
        },
        _ => null,
    };

    // Una foto o un escaneo, derecho y con contraste; un PDF hecho en la computadora, tal cual.
    static async Task<byte[]> ComoSeGuarda(Hallazgo h)
    {
        if (Clientes.SeCorrige(h)) return (await WindowsOcr.Corregir(h)).Jpeg;
        var (px, ancho, alto) = await WindowsOcr.Cargar(h.Ruta, h.Hoja, 2400);
        return await WindowsOcr.Jpeg(px, ancho, alto);
    }

    static BitmapSource DesdeJpeg(byte[] jpeg)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.StreamSource = new MemoryStream(jpeg);
        b.EndInit();
        b.Freeze();
        return b;
    }

    FrameworkElement Encabezado(string glifo, string titulo, string queHace)
    {
        var icono = new TextBlock { Text = glifo, FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 13, Margin = new(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        icono.SetResourceReference(TextBlock.ForegroundProperty, glifo == Carpeta ? "Carpeta" : "Pdf");
        var nombre = new TextBlock { Text = titulo, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var textos = new StackPanel { Children = { nombre } };
        if (queHace.Length > 0)
        {
            var que = new TextBlock { Text = queHace, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            que.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            textos.Children.Add(que);
        }
        Grid.SetColumn(textos, 1);
        return new Grid
        {
            Margin = new(0, 10, 0, 2),
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { icono, textos }
        };
    }

    // Un nombre de lo que se va a guardar, con un punto que se vuelve tilde cuando se guarda.
    static (Grid Linea, TextBlock Marca, TextBlock Texto) LineaPlan(string texto, string ruta)
    {
        var marca = new TextBlock { Text = "•", FontSize = 12, Width = 18, Margin = new(21, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new(0.5, 0.5) };
        marca.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var t = new TextBlock { Text = texto, FontSize = 12, TextWrapping = TextWrapping.Wrap, ToolTip = ruta };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(t, 1);
        var linea = new Grid
        {
            Margin = new(0, 1, 0, 1),
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { marca, t }
        };
        return (linea, marca, t);
    }

    static TextBlock Seccion(string titulo)
    {
        var t = new TextBlock { Text = titulo.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return t;
    }

    static TextBlock Nota(string texto)
    {
        var t = new TextBlock { Text = texto, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    FrameworkElement LineaEmpleado(FilaCliente f, Renglon x)
    {
        // El disco dice cómo está; al pasar por encima, lo explica en palabras. Si se fue en el mes, su recibo es la
        // liquidación final, y se dice así.
        var baja = x.Baja is { } cese ? (cese.Length > 0 ? $"baja el {cese}" : "baja") : null;
        string ConBaja(string texto) => baja is null ? texto : texto.Length > 0 ? $"{baja} · {texto}" : baja;
        var (glifo, color, nota, explica) = x.Firma switch
        {
            Firma.Firmado => (Tilde, Verde, ConBaja(""), baja is null ? "Firmado: su recibo firmado está con los firmados" : "Se fue en el mes: su liquidación final volvió firmada"),
            Firma.PorNombre => (Tilde, Verde, ConBaja("encontrado por el nombre: ilegible, revisar"), "El CUIL no se leía bien y lo encontré por el nombre: revisalo. Su recibo se guarda igual, y sigue para revisar hasta que se lea su CUIL"),
            Firma.ConIniciales => (Lapiz, Violeta, ConBaja("con las iniciales del estudio"), "No volvió firmado: tiene las iniciales del estudio"),
            _ when FaltaElRecibo(x) => (Cruz, Rojo, ConBaja($"{(baja is null ? "falta el recibo" : "falta la liquidación final")} · CUIL {x.Empleado.Cuil}"),
                baja is null ? "Falta el recibo: no está ni firmado ni sin firmar" : "Se fue en el mes: su recibo es la liquidación final, y no está ni firmada ni sin firmar"),
            _ => (Lapiz, Amarillo, ConBaja($"{(baja is null ? "falta la firma" : "falta la liquidación final firmada")} · CUIL {x.Empleado.Cuil}"),
                baja is null ? "Falta la firma: está su recibo sin firmar, pero no volvió firmado" : "Se fue en el mes: está su liquidación final sin firmar, pero no volvió firmada"),
        };
        var disco = new Grid
        {
            Width = 16, Height = 16, Margin = new(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, ToolTip = explica,
            Children =
            {
                new Formas.Ellipse { Fill = color },
                new TextBlock { Text = glifo, FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 8, Foreground = color == Amarillo ? TintaOscura : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        var nombre = new TextBlock { Text = x.Nombre.Length > 0 ? x.Nombre : x.Empleado.Cuil, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        nombre.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var textos = new StackPanel { Children = { nombre } };
        // La nota de abajo está siempre, aunque vacía: si falla lo que se le guarda, ahí se dice por qué.
        var debajo = Nota(nota);
        debajo.Margin = new(0);
        if (nota.Length == 0) debajo.Visibility = Visibility.Collapsed;
        textos.Children.Add(debajo);
        Grid.SetColumn(textos, 1);
        var izquierda = new Grid
        {
            Background = Brushes.Transparent,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { disco, textos }
        };

        // Lo que se despliega al tocarlo: su firmado, para ver si de verdad está firmado; si falta, su recibo sin
        // firmar, que es donde irían las iniciales.
        var sinFirmar = x.SinFirmar.GroupBy(h => h.Ruta, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.ToList();
        var origen = sinFirmar is null ? "" : Path.GetFileNameWithoutExtension(sinFirmar[0].Ruta);
        IReadOnlyList<Hallazgo>? hojas = x.Firmado.Count > 0 ? x.Firmado : sinFirmar;
        var leyenda = x.Firmado.Count > 0 ? null : $"Sin firmar, de «{origen}»";

        // A la derecha, los íconos de lo que se le va a guardar (los pone PlanDelCliente, que mira la carpeta aparte);
        // abajo, donde se despliega cómo va a quedar.
        var acciones = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var despliegue = new Border { Height = 0, ClipToBounds = true, Margin = new(24, 0, 0, 0) };
        Resorte.Animar((despliegue, "alto"), 0, v => despliegue.Height = Math.Max(0, v), desde: 0);

        // Las iniciales son un parche: solo para quien no tiene el firmado y sí su recibo sin firmar, y nada viene
        // marcado. La app no sabe mirar una firma; lo decide quien revisa.
        CheckBox? casilla = null;
        if (x.Firma == Firma.Falta && sinFirmar is not null)
        {
            var clave = $"{f.Ruta}|{x.Empleado.Cuil}";
            casilla = new CheckBox
            {
                Content = "Firmar iniciales", FontSize = 12, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, IsChecked = elegidos.Contains(clave),
                IsEnabled = f.Dificiles == 0,
                ToolTip = f.Dificiles > 0 ? "Todavía se están leyendo hojas difíciles de este cliente"
                    : $"Le estampa sus iniciales a su recibo sin firmar, el de «{origen}», y lo guarda con los firmados"
            };
            AutomationProperties.SetName(casilla, $"Firmar con las iniciales de {nombre.Text}");
            var esta = casilla;
            // ponytail-keep: Checked y Unchecked, no Click. Tildada con UI Automation (un lector de pantalla, o la
            // prueba en la VM), Click no se dispara: la casilla quedaba tildada sin sumar nada a lo que se guarda.
            void Cambio(object _, RoutedEventArgs __)
            {
                if (esta.IsChecked == true) elegidos.Add(clave);
                else elegidos.Remove(clave);
                if (elegida == f && muestra == Muestra.Cliente && paraGuardar is { } destino) _ = PlanDelCliente(f, destino);
            }
            casilla.Checked += Cambio;
            casilla.Unchecked += Cambio;
        }
        if (casilla is null)
        {
            ranuras[x.Empleado.Cuil] = new(acciones, debajo, despliegue, nombre.Text, null, null);
            return new StackPanel { Children = { ConPrevia(izquierda, Hojas(hojas, leyenda), acciones, x.Firmado.Count > 0 ? "Ver el recibo" : "Ver el recibo sin firmar", grupo: Derecha), despliegue } };
        }

        // Tildada la casilla, aparece a su lado una lupita: muestra cómo quedarían las iniciales. Destildarla guarda las dos.
        var lupaGlifo = new TextBlock { Text = "\uE71E", FontFamily = (FontFamily)FindResource("Iconos"), FontSize = 13 };
        var lupa = new Button
        {
            Style = (Style)FindResource("Icono"), Width = 32, Height = 32, Margin = new(2, -4, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Ver cómo quedarían las iniciales", RenderTransformOrigin = new(0.5, 0.5), Content = lupaGlifo
        };
        AutomationProperties.SetName(lupa, $"Ver cómo quedarían las iniciales de {nombre.Text}");
        var lupaEscala = new ScaleTransform();
        lupa.RenderTransform = lupaEscala;
        var cajaIniciales = despliegue;
        var hojaSinFirmar = sinFirmar![0];
        var viendo = false;
        void Lupa(bool mostrar)
        {
            // Aparece creciendo junto a la casilla; al destildar se achica y se va.
            lupa.IsEnabled = mostrar;
            Opacidad(lupa, mostrar ? 1 : 0);
            Resorte.Animar((lupa, "escala"), mostrar ? 1 : 0.5, v => lupaEscala.ScaleX = lupaEscala.ScaleY = v, desde: mostrar ? 0.5 : null, rigidez: 420, rebote: 0.35);
        }
        lupa.Opacity = 0;
        lupa.IsEnabled = false;
        if (casilla.IsChecked == true) Lupa(true);
        lupa.Click += async (_, _) => await VerIniciales(cajaIniciales, hojaSinFirmar, viendo = !viendo);
        casilla.Checked += (_, _) => Lupa(true);
        casilla.Unchecked += async (_, _) =>
        {
            Lupa(false);
            if (viendo) await VerIniciales(cajaIniciales, hojaSinFirmar, viendo = false);
        };
        var derecha = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { casilla, lupa, acciones } };
        ranuras[x.Empleado.Cuil] = new(acciones, debajo, despliegue, nombre.Text, lupaGlifo, casilla);
        return new StackPanel { Children = { ConPrevia(izquierda, Hojas(hojas, leyenda), derecha, "Ver el recibo sin firmar", grupo: Derecha), despliegue } };
    }

    // Al tildar «Firmar iniciales», cómo quedarían: un recorte ampliado de la zona de la firma, armado con lo mismo que
    // después se guarda. Hasta ver que la app acierta en todos los formatos de recibo, se firma de a uno y mirando.
    Task VerIniciales(Border caja, Hallazgo h, bool ver) => Desplegar(caja, ver, async () =>
        [Nota("Así quedarían: fijate que las iniciales caigan sobre la línea de la firma."),
         Imagen(await Task.Run(() => RecorteDeLaFirma(h)), () => AbrirConIniciales(h), maxAlto: 220)]);

    // El recibo entero con las iniciales, como se va a guardar: un PDF temporal de esta PC con esa sola hoja.
    static async Task AbrirConIniciales(Hallazgo h)
    {
        var (pdf, _, _, _) = await Task.Run(() => Clientes.ConIniciales(h));
        var carpeta = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), AppName)).FullName;
        // Con la hora en el nombre: si el anterior sigue abierto en el visor, no se puede reemplazar.
        var archivo = Path.Combine(carpeta, $"{Path.GetFileNameWithoutExtension(h.Ruta)} - hoja {h.Hoja} con iniciales {DateTime.Now:HHmmss}.pdf");
        await File.WriteAllBytesAsync(archivo, pdf);
        Process.Start(new ProcessStartInfo(archivo) { UseShellExecute = true });
    }

    // La zona de la firma de la hoja ya con las iniciales: el rótulo y lo de encima, con aire a los costados.
    static BitmapSource RecorteDeLaFirma(Hallazgo h)
    {
        var (pdf, firma, anchoPt, altoPt) = Clientes.ConIniciales(h);
        var p = Pdfium.Dibujar(pdf, 0, 220, 4000);
        var escala = p.Ancho / anchoPt;
        int X(double pt) => Math.Clamp((int)(pt * escala), 0, p.Ancho);
        int Y(double pt) => Math.Clamp((int)((altoPt - pt) * escala), 0, p.Alto);
        int x0 = X(firma.Izq - 110), x1 = X(firma.Der + 110), y0 = Y(firma.Arriba + 55), y1 = Y(firma.Abajo - 12);
        int w = x1 - x0, alto = y1 - y0;
        var px = new byte[w * alto * 4];
        for (var y = 0; y < alto; y++) System.Buffer.BlockCopy(p.Pixeles, ((y0 + y) * p.Ancho + x0) * 4, px, y * w * 4, w * 4);
        var b = BitmapSource.Create(w, alto, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    static TextBlock TextoEnlace(string texto)
    {
        var t = new TextBlock { Text = texto, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        return t;
    }

    // Lo que se toca se agranda en el lugar: debajo aparece lo que trae «cargar» (el recibo, cómo va a quedar), a lo
    // ancho del panel. Otro toque lo guarda. «derecha» va al costado y fuera del botón (la casilla de iniciales).
    FrameworkElement ConPrevia(FrameworkElement contenido, Func<Task<List<UIElement>>>? cargar, FrameworkElement? derecha = null,
        string tooltip = "Ver el recibo", double aire = 3, string? grupo = null)
    {
        var grilla = new Grid
        {
            Margin = new(0, aire, 0, aire),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = grupo } },
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } },
        };
        if (derecha is not null)
        {
            Grid.SetColumn(derecha, 1);
            grilla.Children.Add(derecha);
        }
        if (cargar is null)
        {
            grilla.Children.Add(contenido);
            return grilla;
        }
        var adentro = new StackPanel();
        var caja = new Border { Height = 0, ClipToBounds = true, Child = adentro, Margin = new(24, 0, 0, 0) };
        Grid.SetRow(caja, 1);
        Grid.SetColumnSpan(caja, 2);
        var boton = new Button { Style = (Style)FindResource("Llano"), Content = contenido, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = tooltip };
        AutomationProperties.SetHelpText(boton, tooltip);
        grilla.Children.Add(boton);
        grilla.Children.Add(caja);
        Resorte.Animar((caja, "alto"), 0, v => caja.Height = Math.Max(0, v), desde: 0);
        bool abierta = false, cargada = false;
        boton.Click += async (_, _) =>
        {
            abierta = !abierta;
            if (abierta && !cargada)
            {
                cargada = true;
                adentro.Children.Add(Nota("Cargando…"));
                Medir();
                List<UIElement> cargado;
                try { cargado = await cargar(); }
                catch (Exception ex) { cargado = [Nota($"No pude mostrarlo: {ex.Message}")]; }
                adentro.Children.Clear();
                foreach (var c in cargado) adentro.Children.Add(c);
            }
            Medir();
        };
        void Medir()
        {
            adentro.Measure(new Size(Math.Max(200, panelLista.ActualWidth - 24), double.PositiveInfinity));
            Resorte.Animar((caja, "alto"), abierta ? adentro.DesiredSize.Height : 0, v => caja.Height = Math.Max(0, v), rigidez: 260, rebote: 0.1);
        }
        return grilla;
    }

    // Las hojas de un archivo, derechas como las leyó el OCR (a lo sumo tres). Tocar una la abre en tamaño completo.
    static Func<Task<List<UIElement>>>? Hojas(IReadOnlyList<Hallazgo>? hojas, string? leyenda = null)
    {
        if (hojas is not { Count: > 0 }) return null;
        return async () =>
        {
            var lista = new List<UIElement>();
            if (leyenda is not null) lista.Add(Nota(leyenda));
            foreach (var h in hojas.Take(3))
            {
                var hoja = h;
                lista.Add(Imagen(await Task.Run(() => Previa(hoja)), () => AbrirHoja(hoja)));
            }
            if (hojas.Count > 3) lista.Add(Nota($"Y {hojas.Count - 3} hojas más: tocá una para abrir el archivo."));
            return lista;
        };
    }

    static Image Imagen(BitmapSource bmp, Func<Task>? alTocar = null, double maxAlto = double.PositiveInfinity)
    {
        var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new(0, 4, 0, 4), MaxHeight = maxAlto, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (alTocar is null) return img;
        img.Cursor = Cursors.Hand;
        img.ToolTip = "Abrir en tamaño completo";
        img.MouseLeftButtonUp += async (_, _) => await alTocar();
        return img;
    }

    // En tamaño completo, solo esa hoja y derecha. Un escaneo con los firmados de todos juntos, abierto entero, mostraba
    // los recibos de los demás. La hoja se dibuja en una imagen temporal de esta PC, nunca en la carpeta del cliente.
    static async Task AbrirHoja(Hallazgo h)
    {
        var archivo = h.Ruta;
        if (h.Hoja > 0 || h.Lectura.Giro != 0)
        {
            try
            {
                var (px, ancho, alto) = await Task.Run(() => WindowsOcr.Cargar(h.Ruta, h.Hoja, 3200));
                (px, ancho, alto) = Retoque.Girar(px, ancho, alto, h.Lectura.Giro);
                var jpeg = await WindowsOcr.Jpeg(px, ancho, alto);
                // shortcut: las imágenes temporales no se borran; quedan en la carpeta temporal de Windows, que se limpia sola.
                var carpeta = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), AppName)).FullName;
                archivo = Path.Combine(carpeta, $"{Path.GetFileNameWithoutExtension(h.Ruta)}{(h.Hoja > 0 ? $" - hoja {h.Hoja}" : "")}.jpg");
                await File.WriteAllBytesAsync(archivo, jpeg);
            }
            catch { archivo = h.Ruta; } // si no se pudo dibujar, el archivo entero es mejor que nada
        }
        Process.Start(new ProcessStartInfo(archivo) { UseShellExecute = true });
    }

    // La foto o la hoja escaneada, derecha como se leyó y de un tamaño que entra bien en el panel. Congelada: se arma
    // afuera del hilo de la ventana.
    static async Task<BitmapSource> Previa(Hallazgo h)
    {
        var (px, ancho, alto) = await WindowsOcr.Cargar(h.Ruta, h.Hoja, 1400);
        (px, ancho, alto) = Retoque.Girar(px, ancho, alto, h.Lectura.Giro);
        var b = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Bgra32, null, px, ancho * 4);
        b.Freeze();
        return b;
    }

    // ---- El letrero ----

    void CartelClientes()
    {
        faltanClientes = porCliente.Values.Count(f => f.Grupo is 0 or 1);
        // Arranca en 1 y rueda hasta los que faltan: empezar en 0 mostraría un «TODOS OK» de mentira.
        Resorte.Animar((clientesCartel, "n"), faltanClientes, PintarCartelClientes, desde: Math.Min(1, faltanClientes), rigidez: 90);
        if (clientesCartel.Opacity < 0.99) Aparecer(clientesCartel, clientesCartelAltura, desdeY: 24);
    }

    void PintarCartelClientes(double valor)
    {
        var k = Math.Max(1, (int)Math.Round(valor));
        // Rojo si a algún cliente le falta un recibo; amarillo si solo faltan firmas; verde si están todos; gris si no
        // hubo nada que revisar.
        var rojo = porCliente.Values.Any(f => f.Grupo == 0);
        var (texto, icono, color) = porCliente.Count == 0 ? ("No hay clientes con sueldos", Alerta, 0)
            // Un mes sin carpetas (todavía no empezó, o se eligió otro sin querer) no es «TODOS OK»: no se revisó nada.
            : porCliente.Values.All(f => f.Grupo == 3) ? ($"Nada para revisar en {Clientes.NombreMes(mesElegido).ToLowerInvariant()}", Raya, 3)
            : faltanClientes == 0 && valor < 0.5 ? ("TODOS OK", Tilde, 2)
            : rojo ? (k == 1 ? "Faltan recibos de 1 cliente" : $"Faltan recibos de {k} clientes", Alerta, 0)
            : (k == 1 ? "Faltan firmas de 1 cliente" : $"Faltan firmas de {k} clientes", Lapiz, 1);
        clientesCartelTexto.Text = texto;
        if (clientesCartelIcono.Text != icono)
        {
            clientesCartelIcono.Text = icono;
            Resorte.Animar((clientesCartelIcono, "escala"), 1, v => clientesCartelIconoEscala.ScaleX = clientesCartelIconoEscala.ScaleY = v, desde: 0.4, rigidez: 420, rebote: 0.4);
        }
        Opacidad(clientesCartelAmarillo, color == 1 ? 1 : 0);
        Opacidad(clientesCartelVerde, color == 2 ? 1 : 0);
        Opacidad(clientesCartelGris, color == 3 ? 1 : 0);
        clientesCartelTexto.Foreground = clientesCartelIcono.Foreground = color == 1 ? TintaOscura : Brushes.White;
    }

    // ---- El panel de la derecha ----

    // Cambia lo que muestra el panel. Si ya estaba abierto, lo nuevo entra subiendo apenas sobre lo viejo; si estaba
    // cerrado, entra con el panel.
    void Llenar(string titulo, string subtitulo, string? icono, Action<UIElementCollection> armar)
    {
        panelTitulo.Text = titulo;
        panelSubtitulo.Text = subtitulo;
        Icono(icono, null);
        panelLista.Children.Clear();
        armar(panelLista.Children);
        panelDesplazar.ScrollToTop();
        if (muestra == Muestra.Nada) return;
        Opacidad(panelLista, 1, desde: 0);
        Resorte.Animar((panelLista, "y"), 0, v => panelListaCorrida.Y = v, desde: 12, rigidez: 300, rebote: 0.15);
    }

    void Icono(string? glifo, Brush? color)
    {
        panelIcono.Visibility = glifo is null ? Visibility.Collapsed : Visibility.Visible;
        if (glifo is null) return;
        panelIcono.Text = glifo;
        if (color is null) panelIcono.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        else panelIcono.Foreground = color;
        Resorte.Animar((panelIcono, "escala"), 1, v => panelIconoEscala.ScaleX = panelIconoEscala.ScaleY = v, desde: 0.4, rigidez: 420, rebote: 0.4);
    }

    // El panel crece desde el borde de la lista y la ventana se agranda hacia la derecha para hacerle lugar (si no hay
    // lugar, se corre lo justo). La ventana toma su ancho de una vez, como el alto en CambiarVista.
    void AbrirPanel(Muestra que)
    {
        var estaba = muestra != Muestra.Nada;
        muestra = que;
        if (estaba) return;
        var area = SystemParameters.WorkArea;
        panelCaja.Width = AnchoDelPanel;
        Estirable(true);
        var anchoVentana = AnchoSeparar + AnchoDelPanel + Marco();
        Width = anchoVentana;
        var izquierda = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - anchoVentana));
        if (Math.Abs(izquierda - Left) > 0.5) Resorte.Animar((this, "izquierda"), izquierda, v => Left = v, desde: Left, rigidez: 170, rebote: 0.08);
        Resorte.Animar((panel, "ancho"), AnchoDelPanel, v => panel.Width = Math.Max(0, v), rigidez: 260, rebote: 0.12);
        Resorte.Animar((panelCaja, "x"), 0, v => panelCorrida.X = v, desde: -AnchoDelPanel * 0.3, rigidez: 260, rebote: 0.12);
        Opacidad(panelCaja, 1, desde: 0);
    }

    // Se guarda por donde salió, hacia la lista, y la ventana vuelve a su ancho al terminar. «deUna», sin viaje: al
    // cambiar de vista, que ya se lleva todo.
    void CerrarPanel(bool deUna = false)
    {
        if (muestra == Muestra.Nada || guardando) return;
        muestra = Muestra.Nada;
        MarcarElegida(null);
        if (deUna)
        {
            Resorte.Animar((panel, "ancho"), 0, v => panel.Width = Math.Max(0, v), desde: 0);
            Estirable(enClientes);
            Width = AnchoSeparar + Marco();
            return;
        }
        Resorte.Animar((panelCaja, "x"), -AnchoDelPanel * 0.3, v => panelCorrida.X = v, rigidez: 260, rebote: 0.05);
        Opacidad(panelCaja, 0);
        Resorte.Animar((panel, "ancho"), 0, v => panel.Width = Math.Max(0, v), rigidez: 260, rebote: 0.05, alLlegar: () =>
        {
            if (muestra != Muestra.Nada) return;
            Estirable(enClientes);
            Width = AnchoSeparar + Marco();
        });
    }

    // ---- Guardar ----

    async Task AbrirGuardar()
    {
        if (guardando || planeando) return;
        planeando = true;
        var filas = porCliente.Values.Where(f => f.Revision is not null).ToList();
        var porFila = filas.ToDictionary(f => f, ElegidosDe);
        guardarTexto.Text = "Armando la lista…";
        List<(FilaCliente Fila, Escritura E)> nuevo;
        try
        {
            // Planear mira en la carpeta de los clientes qué existe ya: afuera del hilo de la ventana.
            nuevo = await Task.Run(() => filas.SelectMany(f => Clientes.Planear(f.Revision!, porFila[f]).Select(e => (f, e))).ToList());
        }
        catch (Exception ex)
        {
            guardarTexto.Text = ex.Message;
            Luego(2600, () => { if (!guardando) guardarTexto.Text = TextoGuardar; });
            return;
        }
        finally
        {
            planeando = false;
        }
        guardarTexto.Text = TextoGuardar;
        if (nuevo.Count == 0)
        {
            guardarTexto.Text = "No hay nada para guardar";
            Luego(2200, () => { if (!guardando) guardarTexto.Text = TextoGuardar; });
            return;
        }

        // Arriba, el resumen: con doscientas cosas, lo que importa se lee de un vistazo.
        string Cuantos(Accion a, string uno, string varios) => nuevo.Count(p => p.E.Accion == a) is var n and > 0 ? (n == 1 ? uno : $"{n} {varios}") : "";
        var partes = new[]
        {
            Cuantos(Accion.Corregido, "1 firmado corregido", "firmados corregidos"),
            Cuantos(Accion.Iniciales, "1 con iniciales", "con iniciales"),
            Cuantos(Accion.Consolidado, "1 consolidado", "consolidados"),
            Cuantos(Accion.Lista, "1 lista de faltantes", "listas de faltantes"),
            Cuantos(Accion.Carpeta, "1 carpeta nueva", "carpetas nuevas"),
        }.Where(s => s.Length > 0);
        var clientes = nuevo.Select(p => p.Fila).Distinct().Count();
        plan = [];
        MarcarElegida(null);
        Llenar(nuevo.Count == 1 ? $"Se guarda 1 cosa en {Donde}" : $"Se guardan {nuevo.Count} cosas en {Donde}",
            $"{(clientes == 1 ? "En 1 cliente" : $"En {clientes} clientes")}: {string.Join(" · ", partes)}.", Disquete, lista =>
            {
                foreach (var g in nuevo.GroupBy(p => p.Fila))
                {
                    var titulo = new TextBlock { Text = g.Key.Revision!.Cliente, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 3) };
                    titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                    lista.Add(titulo);
                    var cliente = g.Key;
                    ArmarPlan(cliente.Revision!, g.Select(p => p.E).ToList(), el => lista.Add(el), (e, marca, texto) => plan.Add((cliente, e, marca, texto)));
                }
            });
        pieGuardar.Visibility = Visibility.Visible;
        pieCliente.Visibility = Visibility.Collapsed;
        guardarSi.IsEnabled = guardarNo.IsEnabled = true;
        guardarNo.Content = "Cancelar";
        AbrirPanel(Muestra.Guardar);
    }

    // Guarda lo de todos los clientes (el botón de abajo) o lo de uno (su panel). La lista que se escribe es la que
    // se está viendo, renglón por renglón.
    async Task Guardar(List<(FilaCliente Fila, Escritura E, TextBlock Marca, TextBlock Texto)> cuales, bool deUnCliente)
    {
        if (guardando || cuales.Count == 0) return;
        guardando = true;
        guardarSi.IsEnabled = guardarNo.IsEnabled = panelCerrar.IsEnabled = guardarCliente.IsEnabled = false;
        int hechos = 0, fallas = 0;
        var g0 = generacionClientes;
        foreach (var g in cuales.GroupBy(p => p.Fila).ToList())
        {
            var fila = g.Key;
            var r = fila.Revision!;
            // Primero la carpeta y los recibos; la lista al final, con el cliente revisado de nuevo: así ya cuenta
            // lo que se acaba de guardar. Revisarlo es rápido: lo leído está en la memoria, y lo nuevo trae el
            // CUIL en las palabras clave.
            foreach (var p in g.Where(p => p.E.Accion != Accion.Lista)) await Uno(p, r);
            var nueva = await Task.Run(() => Clientes.Revisar(fila.Ruta, r.Mes, r.Anio, ocr, lector, memoria!, _ => { }, CancellationToken.None));
            foreach (var p in g.Where(p => p.E.Accion == Accion.Lista)) await Uno(p, nueva);
            if (g0 == generacionClientes) ResolverCliente(fila, nueva);
        }

        async Task Uno((FilaCliente Fila, Escritura E, TextBlock Marca, TextBlock Texto) p, Revision r)
        {
            panelSubtitulo.Text = $"Guardando {hechos + fallas + 1} de {cuales.Count}…";
            try
            {
                await Task.Run(() => Clientes.Escribir(p.E, r, WindowsOcr.Corregir));
                hechos++;
                Marcar(p.Marca, ok: true);
            }
            catch (Exception ex)
            {
                fallas++;
                Marcar(p.Marca, ok: false);
                // En el renglón de un empleado, la nota de abajo puede estar vacía y escondida.
                p.Texto.Text = p.Texto.Text.Length > 0 ? $"{p.Texto.Text} · no se pudo: {ex.Message}" : $"no se pudo: {ex.Message}";
                p.Texto.Visibility = Visibility.Visible;
                p.Texto.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            }
        }

        _ = Task.Run(memoria!.Guardar);
        // El destino no se ve: el panel mismo se vuelve la confirmación.
        panelTitulo.Text = fallas == 0
            ? hechos == 1 ? $"Guardé 1 cosa en {Donde}" : $"Guardé {hechos} cosas en {Donde}"
            : $"Guardé {hechos}; {fallas} no se pudieron";
        panelSubtitulo.Text = fallas == 0 ? "Cada archivo quedó en su carpeta." : "Las que no se pudieron están marcadas en la lista.";
        Icono(fallas == 0 ? Tilde : Alerta, fallas == 0 ? Verde : Rojo);
        if (g0 == generacionClientes)
        {
            Acomodar();
            CartelClientes();
        }
        guardando = false;
        panelCerrar.IsEnabled = true;
        if (deUnCliente)
        {
            // El panel del cliente vuelve a mostrar su estado, ya con lo guardado.
            var f = cuales[0].Fila;
            Luego(fallas == 0 ? 2400 : 6000, () => { if (elegida == f && muestra == Muestra.Cliente && !guardando) MostrarCliente(f); });
            return;
        }
        // Si salió todo, el panel se cierra solo; con fallas queda abierto para leerlas.
        if (fallas == 0) Luego(2400, () => { if (muestra == Muestra.Guardar) CerrarPanel(); });
        else
        {
            guardarNo.Content = "Cerrar";
            guardarNo.IsEnabled = true;
        }
    }

    // Cada renglón de la lista acusa recibo al guardarse: el punto se vuelve tilde, o alerta si falló.
    void Marcar(TextBlock marca, bool ok)
    {
        var escala = new ScaleTransform();
        marca.RenderTransform = escala;
        marca.FontFamily = (FontFamily)FindResource("Iconos");
        marca.FontSize = 10;
        marca.Text = ok ? Tilde : Alerta;
        marca.Foreground = ok ? Verde : Rojo;
        Resorte.Animar((marca, "escala"), 1, v => escala.ScaleX = escala.ScaleY = v, desde: 0.3, rigidez: 420, rebote: 0.4);
    }
}
