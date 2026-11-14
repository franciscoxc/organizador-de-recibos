using System.Windows;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application();
        // El tema Fluent de Windows 11 por diccionario y no con la API experimental ThemeMode (WPF0001): el
        // efecto es el mismo (claro u oscuro según Windows, Mica de fondo, color de acento) sin depender
        // de una API que todavía puede cambiar.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
        app.Run(new Ventana(args));
    }
}
