using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}

// El recuadro de Achicar PDF: ícono, nombre y, en rojo, el resultado o el problema.
sealed class DropBox : Control
{
    Bitmap? pdfImage;
    bool dimIcon;
    string result = "";

    public DropBox()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public Bitmap? PdfImage
    {
        get => pdfImage;
        set { pdfImage = value; Invalidate(); }
    }

    public bool DimIcon
    {
        get => dimIcon;
        set { dimIcon = value; Invalidate(); }
    }

    public string Result
    {
        get => result;
        set { result = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        ControlPaint.DrawBorder(g, ClientRectangle, Color.FromArgb(100, 100, 100), ButtonBorderStyle.Solid);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
        // Un hijo visible (el botón "mostrar ahora") ocupa el pie del recuadro.
        var bottom = Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Top - 4).DefaultIfEmpty(Height).Min();
        var textSize = TextRenderer.MeasureText(g, Text, Font, new Size(Width - 24, Height), flags);
        var resultSize = result.Length == 0 ? Size.Empty : TextRenderer.MeasureText(g, result, Font, new Size(Width - 24, Height), flags);
        var iconSize = PdfImage?.Size ?? Size.Empty;
        var gap = PdfImage is null || Text.Length + result.Length == 0 ? 0 : 10;
        var blockHeight = iconSize.Height + gap + textSize.Height + resultSize.Height;
        var y = Math.Max(8, (bottom - blockHeight) / 2);

        if (PdfImage is not null)
        {
            var iconX = (Width - iconSize.Width) / 2;
            if (DimIcon) ControlPaint.DrawImageDisabled(g, PdfImage, iconX, y, BackColor);
            else g.DrawImage(PdfImage, iconX, y, iconSize.Width, iconSize.Height);
            y += iconSize.Height + gap;
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(12, y, Width - 24, textSize.Height), ForeColor, flags | TextFormatFlags.Top);
        if (result.Length > 0)
            TextRenderer.DrawText(g, result, Font, new Rectangle(12, y + textSize.Height, Width - 24, Math.Max(0, bottom - y - textSize.Height)), Color.Red, flags | TextFormatFlags.Top);
    }
}

sealed class MainForm : Form
{
    const string AppName = "Organizador de Recibos";
    const string DropHint = "Tira aquí los PDF con los recibos\no haz clic para buscarlos";

    readonly Label intro = new()
    {
        Text = "Un PDF por empleado y mes, con sus iniciales,\nen la carpeta del empleador.",
        Left = 24, Top = 34, Width = 278, Height = 40, ForeColor = Color.DimGray
    };
    readonly DropBox drop = new() { Text = DropHint, Left = 24, Top = 78, Width = 278, Height = 190, AllowDrop = true, Cursor = Cursors.Hand };
    readonly Label status = new() { Left = 24, Top = 276, Width = 278, Height = 24, Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold), ForeColor = Color.FromArgb(36, 107, 87) };
    readonly Button showOutput = new() { Text = "mostrar ahora", Left = 84, Top = 150, Width = 110, Height = 28, Visible = false };
    // Arriba a la derecha: la flecha circular busca una versión nueva; cuando la descargó, el mismo
    // botón dice «Reiniciar para actualizar».
    readonly Button update = new() { FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, TabStop = false };
    readonly ToolTip tips = new();
    static readonly Font IconFont = new("Segoe MDL2 Assets", 10f);
    readonly string[] startupFiles;
    readonly Ocr? ocr = CrearOcr();
    bool working;
    bool buscando;
    string? nuevoExe; // descargado y verificado: reemplaza al actual al reiniciar

    public MainForm(string[] files)
    {
        startupFiles = files;
        Text = AppName;
        ClientSize = new Size(326, 308);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon; } catch { }

        drop.PdfImage = PdfIcon();
        drop.Controls.Add(showOutput);
        Controls.AddRange([intro, drop, status, update]);
        MostrarIcono();

        update.Click += async (_, _) =>
        {
            if (nuevoExe is null) await BuscarActualizacion(manual: true);
            else Reiniciar();
        };
        showOutput.Click += (_, _) => OpenOutput();
        drop.Click += async (_, _) => await Pick();
        drop.DragEnter += (_, e) =>
        {
            if (!working && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        drop.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped) _ = ProcessAll(dropped);
        };
        Shown += async (_, _) =>
        {
            AddToSendTo();
            _ = BuscarActualizacion(manual: false);
            // La versión anterior se cierra apenas abre esta: un rato después ya se puede borrar.
            _ = Task.Delay(3000).ContinueWith(_ => Actualizador.BorrarViejo());
            if (startupFiles.Length > 0) await ProcessAll(startupFiles);
        };
    }

    async Task BuscarActualizacion(bool manual)
    {
        if (buscando) return;
        buscando = true;
        update.Enabled = false;
        try
        {
            var nueva = await Actualizador.Buscar();
            if (nueva is null)
            {
                if (manual) status.Text = $"Ya tenés la última versión ({Actualizador.Actual})";
                return;
            }
            nuevoExe = await Actualizador.Descargar(nueva, new Progress<int>(p => MostrarTexto($"descargando {p}%")));
            tips.SetToolTip(update, $"La versión {nueva.Version} ya está descargada");
        }
        catch (UnauthorizedAccessException) when (manual)
        {
            // La carpeta del programa no deja escribir (Archivos de programa, por ejemplo): a mano.
            Process.Start(new ProcessStartInfo(Actualizador.Pagina) { UseShellExecute = true });
            status.Text = "Bajá la versión nueva de la página que se abrió";
        }
        catch (Exception) when (manual)
        {
            status.Text = "No pude buscar actualizaciones";
        }
        catch { } // la búsqueda al abrir es silenciosa: sin internet, no molesta
        finally
        {
            buscando = false;
            update.Enabled = true;
            if (nuevoExe is null) MostrarIcono();
            else MostrarTexto("Reiniciar para actualizar");
        }
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
            status.Text = "No pude reiniciar: cerrá y abrí el programa";
        }
    }

    void MostrarIcono()
    {
        update.FlatAppearance.BorderSize = 0;
        update.Font = IconFont;
        update.Text = ""; // Refresh: el ícono de actualizar de Windows
        update.AccessibleName = "Buscar actualización"; // el lector de pantalla no puede leer el ícono
        update.ForeColor = Color.DimGray;
        update.SetBounds(ClientSize.Width - 34, 4, 28, 26);
        tips.SetToolTip(update, $"Versión {Actualizador.Actual}: buscar actualización");
    }

    void MostrarTexto(string texto)
    {
        if (update.Text == texto) return; // el progreso llega cientos de veces con el mismo porcentaje
        update.FlatAppearance.BorderSize = 1;
        update.Font = SystemFonts.DefaultFont;
        update.Text = texto;
        update.AccessibleName = texto;
        update.ForeColor = Color.FromArgb(36, 107, 87);
        var ancho = TextRenderer.MeasureText(texto, update.Font).Width + 20;
        update.SetBounds(ClientSize.Width - ancho - 6, 4, ancho, 26);
    }

    static Ocr? CrearOcr()
    {
        try { return WindowsOcr.Crear(); }
        catch { return null; } // sin OCR igual funcionan los recibos con texto
    }

    async Task Pick()
    {
        if (working) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Elegí los PDF con los recibos",
            Filter = "PDF (*.pdf)|*.pdf",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) await ProcessAll(dialog.FileNames);
    }

    async Task ProcessAll(string[] paths)
    {
        if (working) return;
        // Una carpeta cuenta como todos los PDF que tiene adentro.
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.pdf") : [p])
            .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        showOutput.Visible = false;
        drop.DimIcon = false;
        drop.Result = "";
        if (files.Length == 0)
        {
            status.Text = "eso no es un PDF";
            return;
        }

        working = true;
        drop.Text = files.Length == 1 ? Path.GetFileName(files[0]) : $"{files.Length} PDF";
        var problems = new List<string>();
        List<string> created = [];
        try
        {
            // El OCR de Windows es asíncrono, pero el armado de los PDF no: afuera del hilo de la ventana.
            // Invoke y no BeginInvoke: un aviso en cola llegaría después de mostrar el resultado y se perdería.
            created = await Task.Run(() => Recibos.Procesar(files, ocr, msg => Invoke(() =>
            {
                if (msg.StartsWith('⚠')) problems.Add(msg[2..]);
                else status.Text = msg.StartsWith('✓') ? msg[2..] : msg;
            })));
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }
        finally
        {
            working = false;
            status.Text = "";
        }

        drop.Text = created.Count == 0 ? "No separé ningún recibo"
            : $"{created.Count} recibos separados en\n«{Path.GetFileName(Path.GetDirectoryName(created[0]))}»";
        drop.Result = problems.Count == 0 ? "" : problems.Count == 1 ? problems[0] : $"{problems[0]}\n(y {problems.Count - 1} avisos más)";
        if (created.Count > 0)
        {
            showOutput.Tag = created[0];
            showOutput.Visible = true;
            drop.DimIcon = true;
        }
    }

    void OpenOutput()
    {
        if (showOutput.Tag is string path && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
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

    static Bitmap PdfIcon()
    {
        var image = new Bitmap(56, 56);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.Transparent);
        using var paper = new SolidBrush(Color.White);
        using var red = new SolidBrush(Color.FromArgb(210, 30, 36));
        using var dark = new Pen(Color.FromArgb(90, 90, 90), 2);
        using var fold = new SolidBrush(Color.FromArgb(230, 230, 230));

        g.FillRectangle(paper, 16, 6, 28, 38);
        g.DrawRectangle(dark, 16, 6, 28, 38);
        g.FillPolygon(fold, new Point[] { new(44, 6), new(44, 18), new(32, 6) });
        g.DrawLine(dark, 32, 6, 44, 18);
        g.FillRectangle(red, 8, 28, 40, 18);
        using var font = new Font(SystemFonts.DefaultFont.FontFamily, 8, FontStyle.Bold);
        TextRenderer.DrawText(g, "PDF", font, new Rectangle(10, 29, 36, 15), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return image;
    }
}
