using System.Text;
using System.Windows;
using ByteCommander.Services;

namespace ByteCommander;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Заголовок вікна під тему (темний/світлий, колір тла) для всіх вікон застосунку.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => NativeMethods.ApplyChrome((Window)s)));

        DispatcherUnhandledException += (_, ex) =>
        {
            Dialogs.Error(MainWindow, "Неочікувана помилка:\n" + ex.Exception.Message);
            ex.Handled = true;
        };

        base.OnStartup(e);

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);

        // ByteCommander.exe --view <файл> — лише переглядач (зручно для «Відкрити за допомогою»).
        if (e.Args.Length >= 2 && e.Args[0] is "--view" or "/view" && File.Exists(e.Args[1]))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new ViewerWindow(Path.GetFullPath(e.Args[1]), false) { WindowStartupLocation = WindowStartupLocation.CenterScreen }.Show();
            return;
        }

        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }
}
